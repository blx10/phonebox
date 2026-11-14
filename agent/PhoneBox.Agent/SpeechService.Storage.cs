using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

internal static class SpeechStoragePermissions
{
    public static void EnsurePrivateDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { }
        }
    }

    public static void EnsurePrivateFile(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { }
        }
    }
}

internal sealed partial class SpeechService
{
    public SpeechBrowseResult BrowseDirectories(string? requestedPath)
    {
        var defaultDir = GetDefaultTranscriptDirectory();
        var presets = BuildDirectoryPresets(defaultDir);

        string target = string.IsNullOrWhiteSpace(requestedPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : ExpandPath(requestedPath.Trim());

        if (!Path.IsPathRooted(target) || target.IndexOf('\0') >= 0)
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            var full = Path.GetFullPath(target);
            if (!Directory.Exists(full))
            {
                var parent = Path.GetDirectoryName(full);
                full = !string.IsNullOrEmpty(parent) && Directory.Exists(parent)
                    ? parent
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            var dirInfo = new DirectoryInfo(full);
            var entries = dirInfo.EnumerateDirectories()
                .Where(d => !d.Name.StartsWith('.') && (d.Attributes & FileAttributes.Hidden) == 0)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .Select(d => new SpeechDirectoryEntry(d.Name, d.FullName))
                .ToList();

            var parentPath = dirInfo.Parent?.FullName;
            var writable = IsSafeTranscriptDirectory(full) && CanWriteDirectory(full);
            return new SpeechBrowseResult(true, full, parentPath, defaultDir, writable, presets, entries);
        }
        catch (Exception ex)
        {
            return new SpeechBrowseResult(false, target, null, defaultDir, false, presets, [], ex.Message);
        }
    }

    internal async Task<string?> SaveTranscriptToDiskAsync(
        string entryId,
        string text,
        string provider,
        string language,
        int durationMs,
        DateTimeOffset when,
        SpeechConfig cfg,
        string? wavPath,
        CancellationToken ct,
        string? widgetTranscriptDir = null)
    {
        var dir = ResolveTranscriptDirectory(cfg, widgetTranscriptDir);
        if (!IsSafeTranscriptDirectory(dir))
            throw new InvalidOperationException("Configured transcript directory is not permitted.");

        var local = when.ToLocalTime();
        var inv = CultureInfo.InvariantCulture;
        string filePath;
        string contentToWrite;
        bool appendMode = true;

        switch (cfg.TranscriptFormat)
        {
            case "daily-txt":
                filePath = Path.Combine(dir, local.ToString("yyyy-MM-dd", inv) + ".txt");
                contentToWrite = $"[{local.ToString("HH:mm:ss", inv)}] ({provider}) {text}\n\n";
                break;

            case "single-md":
                filePath = Path.Combine(dir, "transcripts.md");
                contentToWrite = $"## {local.ToString("yyyy-MM-dd HH:mm:ss", inv)} — {provider}\n\n{text}\n\n";
                break;

            case "per-item-txt":
                filePath = Path.Combine(dir, $"{local.ToString("yyyyMMdd-HHmmss", inv)}-{entryId}.txt");
                contentToWrite = text + "\n";
                appendMode = false;
                break;

            case "jsonl":
                filePath = Path.Combine(dir, "transcripts.jsonl");
                contentToWrite = JsonSerializer.Serialize(new
                {
                    timestamp = when.ToString("O", inv),
                    id = entryId,
                    provider,
                    language,
                    durationMs,
                    text,
                }, CompactJsonOpts) + "\n";
                break;

            default: // daily-md
                filePath = Path.Combine(dir, local.ToString("yyyy-MM-dd", inv) + ".md");
                contentToWrite = $"## {local.ToString("HH:mm:ss", inv)} — {provider}\n\n{text}\n\n";
                break;
        }

        await _diskGate.WaitAsync(ct);
        try
        {
            SpeechStoragePermissions.EnsurePrivateDir(dir);
            if (appendMode)
                await File.AppendAllTextAsync(filePath, contentToWrite, Encoding.UTF8, ct);
            else
                await File.WriteAllTextAsync(filePath, contentToWrite, Encoding.UTF8, ct);
            SpeechStoragePermissions.EnsurePrivateFile(filePath);

            if (cfg.SaveAudioCopy && !string.IsNullOrWhiteSpace(wavPath) && File.Exists(wavPath))
            {
                var audioDir = Path.Combine(dir, "audio");
                SpeechStoragePermissions.EnsurePrivateDir(audioDir);
                var destWav = Path.Combine(audioDir, $"{local.ToString("yyyyMMdd-HHmmss", inv)}-{entryId}.wav");
                File.Copy(wavPath, destWav, overwrite: true);
                SpeechStoragePermissions.EnsurePrivateFile(destWav);
            }
        }
        finally
        {
            _diskGate.Release();
        }

        return filePath;
    }

    public static string GetDefaultTranscriptDirectory()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(docs))
            docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(docs, "PhoneBox", "Transcripts");
    }

    public static string ResolveTranscriptDirectory(SpeechConfig cfg, string? widgetTranscriptDir = null) =>
        !string.IsNullOrWhiteSpace(widgetTranscriptDir)
            ? ExpandPath(widgetTranscriptDir)
            : string.IsNullOrWhiteSpace(cfg.TranscriptDir)
                ? GetDefaultTranscriptDirectory()
                : ExpandPath(cfg.TranscriptDir);

    public static string FormatDisplayPath(string? path)
    {
        var trimmed = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return "";

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)?
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(trimmed, home, cmp))
                return "~";
            if (trimmed.StartsWith(home + Path.DirectorySeparatorChar, cmp)
                || trimmed.StartsWith(home + Path.AltDirectorySeparatorChar, cmp))
            {
                return "~" + trimmed[home.Length..];
            }
        }
        return trimmed;
    }

    /// <summary>Formats a path safely for Wi-Fi clients by keeping only the leaf folder and filename (no host directories or username).</summary>
    public static string FormatLanDisplayPath(string? path)
    {
        var trimmed = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return "";
        var clean = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var file = Path.GetFileName(clean);
        var parentDir = Path.GetDirectoryName(clean);
        var parentName = string.IsNullOrWhiteSpace(parentDir) ? "" : Path.GetFileName(parentDir);
        return string.IsNullOrWhiteSpace(parentName) ? file : $"{parentName}/{file}";
    }

    internal static string PreviewTranscriptTarget(SpeechConfig cfg, string? widgetTranscriptDir, DateTimeOffset when, bool isLan = false)
    {
        var dir = ResolveTranscriptDirectory(cfg, widgetTranscriptDir);
        if (!IsSafeTranscriptDirectory(dir))
            return "Invalid transcript folder";

        var local = when.ToLocalTime();
        var inv = CultureInfo.InvariantCulture;
        var fileName = cfg.TranscriptFormat switch
        {
            "daily-txt" => local.ToString("yyyy-MM-dd", inv) + ".txt",
            "single-md" => "transcripts.md",
            "per-item-txt" => "<timestamp>.txt",
            "jsonl" => "transcripts.jsonl",
            _ => local.ToString("yyyy-MM-dd", inv) + ".md",
        };
        var full = Path.Combine(dir, fileName);
        return isLan ? FormatLanDisplayPath(full) : FormatDisplayPath(full);
    }

    public static bool IsSafeTranscriptDirectory(string? rawDir)
    {
        if (string.IsNullOrWhiteSpace(rawDir) || rawDir.IndexOf('\0') >= 0 || rawDir.Any(char.IsControl))
            return false;

        string full;
        try { full = Path.GetFullPath(ExpandPath(rawDir)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return false; }

        if (!Path.IsPathRooted(full)) return false;

        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(full) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return false;

        var forbiddenPrefixes = new[]
        {
            AppPaths.ConfigDir,
            "/System", "/Library", "/bin", "/sbin", "/usr", "/etc", "/var/db", "/private/etc",
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };

        foreach (var bad in forbiddenPrefixes)
        {
            if (string.IsNullOrWhiteSpace(bad)) continue;
            var nb = Path.GetFullPath(bad).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, nb, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(nb + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    internal static string ExpandPath(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (s.StartsWith("~/", StringComparison.Ordinal) || s.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), s[2..]);
        return Environment.ExpandEnvironmentVariables(s);
    }

    private static IReadOnlyList<SpeechDirectoryEntry> BuildDirectoryPresets(string defaultDir)
    {
        var list = new List<SpeechDirectoryEntry>
        {
            new("PhoneBox Transcripts (Default)", defaultDir),
        };
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(docs)) list.Add(new("Documents", docs));
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop)) list.Add(new("Desktop", desktop));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home)) list.Add(new("Home", home));
        return list;
    }

    private static bool CanWriteDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch { return false; }
    }
}
