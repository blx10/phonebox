using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace PhoneBox.Agent;

public sealed record CommandResult(string Output, long At, bool Ok, bool Running, double DurationMs);

/// <summary>
/// Runs the PowerShell commands of "command" widgets. Commands live only in config.json on this PC; the phone
/// asks for a widget by page and widget id and gets the cached output. A command runs at most once per its
/// interval, and only while a phone is actually looking at it.
/// </summary>
public sealed class CommandRunner(ConfigStore cfg, ILogger<CommandRunner> log)
{
    private const int MaxOutput = 4000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, CommandResult> _results = new();
    private readonly ConcurrentDictionary<string, byte> _running = new();

    /// <returns>The cached result (possibly stale or empty while the first run is in progress), or null if there is no such widget.</returns>
    public CommandResult? Get(string pageId, string widgetId, bool force = false)
    {
        var w = cfg.Get().Pages.FirstOrDefault(p => p.Id == pageId)?.Widgets?.FirstOrDefault(x => x.Id == widgetId && x.Type == "command");
        if (w is null) return null;
        // an empty Linux / macOS command means "the same one"
        var command = (!OperatingSystem.IsWindows() && w.Str("commandUnix") is { } unix && !string.IsNullOrWhiteSpace(unix) ? unix : null) ?? w.Str("command") ?? "";
        var interval = Math.Clamp(w.Int("interval") ?? 30, 5, 86400);
        var key = $"{pageId}/{widgetId}:{command.GetHashCode()}";
        var cached = _results.GetValueOrDefault(key);
        var stale = cached is null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cached.At >= interval * 1000L;
        if ((stale || force) && command.Trim() != "" && _running.TryAdd(key, 0))
            _ = Task.Run(async () => { try { _results[key] = await RunAsync(command); } finally { _running.TryRemove(key, out _); } });
        var running = _running.ContainsKey(key);
        return cached is null
            ? new CommandResult(command.Trim() == "" ? "(no command)" : "", 0, true, running, 0)
            : cached with { Running = running };
    }

    private async Task<CommandResult> RunAsync(string command)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var shell = OperatingSystem.IsWindows() ? "powershell.exe" : OperatingSystem.IsMacOS() ? "/bin/zsh" : File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            var psi = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            string[] args = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                   "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command]
                : ["-lc", command]; // login shell: the user's PATH (docker, brew…) is available
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = new Capture();
            var stderr = new Capture();
            var pumps = Task.WhenAll(stdout.Pump(p.StandardOutput.BaseStream), stderr.Pump(p.StandardError.BaseStream));
            using var cts = new CancellationTokenSource(Timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new CommandResult($"timed out after {Timeout.TotalSeconds:0} s", Now(), false, false, sw.Elapsed.TotalMilliseconds);
            }
            // The shell is done. A process it left running in the background (start /b, cmd &) inherits the output pipe
            // and may keep it open for ever: wait a moment for the rest of the output, not for that process.
            await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(1)));
            var output = stdout.Text().TrimEnd();
            var err = stderr.Text().Trim();
            var ok = p.ExitCode == 0;
            if (output == "" && err != "") output = err;
            if (output.Length > MaxOutput) output = output[..MaxOutput] + "…";
            return new CommandResult(output, Now(), ok, false, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            log.LogWarning("command widget failed: {Msg}", ex.Message);
            return new CommandResult(ex.Message, Now(), false, false, sw.Elapsed.TotalMilliseconds);
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Collects a stream's bytes (up to a cap, the rest is drained and dropped) and can be read at any time.</summary>
    private sealed class Capture
    {
        private const int Cap = 64 * 1024;
        private readonly MemoryStream _buf = new();

        public async Task Pump(Stream s)
        {
            var chunk = new byte[8192];
            try
            {
                int n;
                while ((n = await s.ReadAsync(chunk)) > 0)
                    lock (_buf) if (_buf.Length < Cap) _buf.Write(chunk, 0, (int)Math.Min(n, Cap - _buf.Length));
            }
            catch (Exception) { /* the process object was disposed while a background process still held the pipe */ }
        }

        public string Text() { lock (_buf) return Encoding.UTF8.GetString(_buf.GetBuffer(), 0, (int)_buf.Length); }
    }
}
