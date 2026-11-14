using System.Diagnostics;

namespace PhoneBox.Agent;

internal sealed partial class SpeechService
{
    internal static string? DetectWhisperCli(SpeechConfig cfg) => ResolveWhisperCliPath(cfg);

    private static string? ResolveWhisperCliPath(SpeechConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.WhisperCliPath))
        {
            var expanded = ExpandPath(cfg.WhisperCliPath);
            if (File.Exists(expanded)) return expanded;
            var foundCustom = ToolProcess.Find(cfg.WhisperCliPath);
            if (foundCustom is not null) return foundCustom;
            return null;
        }

        foreach (var candidate in new[] { "mlx_whisper", "whisper-cli", "whisper-cpp", "whisper" })
        {
            var path = ToolProcess.Find(candidate);
            if (path is not null) return path;
        }
        return null;
    }

    private static SpeechTestResult TestWhisperCli(SpeechConfig cfg)
    {
        var cli = ResolveWhisperCliPath(cfg);
        if (cli is null)
            return new SpeechTestResult(false, "whisper-cli", "No Whisper CLI found (checked mlx_whisper, whisper-cli, whisper-cpp, whisper).");

        if (!string.IsNullOrWhiteSpace(cfg.WhisperModelPath)
            && (cfg.WhisperModelPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(cfg.WhisperModelPath)))
        {
            var expandedModel = ExpandPath(cfg.WhisperModelPath);
            if (!File.Exists(expandedModel) && !Directory.Exists(expandedModel))
                return new SpeechTestResult(false, "whisper-cli", $"Whisper model not found at '{expandedModel}'.");
        }

        return new SpeechTestResult(true, "whisper-cli", $"Whisper CLI ready: {cli}");
    }

    private async Task<string> TranscribeWhisperCliAsync(string wavPath, string language, SpeechConfig cfg, CancellationToken ct)
    {
        var cli = ResolveWhisperCliPath(cfg)
            ?? throw new InvalidOperationException("Whisper CLI binary not found (install mlx_whisper, whisper-cli, whisper-cpp, or whisper, or configure its path).");

        var outDir = Path.Combine(_tempRoot, "wout-" + Guid.NewGuid().ToString("N"));
        SpeechStoragePermissions.EnsurePrivateDir(outDir);

        try
        {
            var cliName = Path.GetFileNameWithoutExtension(cli).ToLowerInvariant();
            var langIso2 = ToIso2Language(language);
            var modelArg = ExpandPath(cfg.WhisperModelPath);
            var psi = new ProcessStartInfo(cli) { WorkingDirectory = AppPaths.NeutralCwd };
            SpeechProcessEnv.ConfigureMinimal(psi);

            string expectedTxt;
            if (cliName.Contains("mlx_whisper", StringComparison.Ordinal))
            {
                psi.ArgumentList.Add(wavPath);
                psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("txt");
                psi.ArgumentList.Add("--output-dir"); psi.ArgumentList.Add(outDir);
                if (!string.IsNullOrWhiteSpace(modelArg)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(modelArg); }
                if (!string.IsNullOrWhiteSpace(langIso2)) { psi.ArgumentList.Add("--language"); psi.ArgumentList.Add(langIso2); }
                expectedTxt = Path.Combine(outDir, Path.GetFileNameWithoutExtension(wavPath) + ".txt");
            }
            else if (cliName == "whisper")
            {
                psi.ArgumentList.Add(wavPath);
                psi.ArgumentList.Add("--output_format"); psi.ArgumentList.Add("txt");
                psi.ArgumentList.Add("--output_dir"); psi.ArgumentList.Add(outDir);
                if (!string.IsNullOrWhiteSpace(modelArg)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(modelArg); }
                if (!string.IsNullOrWhiteSpace(langIso2)) { psi.ArgumentList.Add("--language"); psi.ArgumentList.Add(langIso2); }
                expectedTxt = Path.Combine(outDir, Path.GetFileNameWithoutExtension(wavPath) + ".txt");
            }
            else
            {
                var outBase = Path.Combine(outDir, "transcript");
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(wavPath);
                if (!string.IsNullOrWhiteSpace(modelArg)) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(modelArg); }
                if (!string.IsNullOrWhiteSpace(langIso2)) { psi.ArgumentList.Add("-l"); psi.ArgumentList.Add(langIso2); }
                psi.ArgumentList.Add("-nt");
                psi.ArgumentList.Add("-otxt");
                psi.ArgumentList.Add("-of"); psi.ArgumentList.Add(outBase);
                expectedTxt = outBase + ".txt";
            }

            var res = await ToolProcess.RunAsync(psi, TimeSpan.FromMinutes(3), ct);
            if (res.ExitCode != 0)
            {
                var err = string.IsNullOrWhiteSpace(res.Error) ? res.Output : res.Error;
                throw new InvalidOperationException($"Whisper CLI failed (exit {res.ExitCode}): {err.Trim()}");
            }

            if (File.Exists(expectedTxt))
                return (await File.ReadAllTextAsync(expectedTxt, ct)).Trim();

            return res.Output.Trim();
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { }
        }
    }
}
