using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace PhoneBox.Agent;

internal interface IActiveRecording : IAsyncDisposable
{
    string WavPath { get; }
    /// <summary>Stops recording and returns an error string if the capture failed, or null on success.</summary>
    Task<string?> StopAsync(CancellationToken ct = default);
    /// <summary>Aborts recording and deletes the temporary capture file.</summary>
    Task CancelAsync();
}

internal interface ISpeechAudioCapture
{
    bool HasPlatformCapture { get; }
    Task<IActiveRecording> StartAsync(string wavPath, int maxSec, Action? onMaxReached, CancellationToken ct = default);
}

/// <summary>Cross-platform microphone capture (macOS Swift helper + ffmpeg fallback, Windows NAudio, Linux pw-record/parecord/arecord/ffmpeg).</summary>
internal sealed class PlatformSpeechAudioCapture : ISpeechAudioCapture
{
    private readonly ISpeechAppleClient _appleHelper;

    public PlatformSpeechAudioCapture(ISpeechAppleClient appleHelper)
    {
        _appleHelper = appleHelper;
    }

    public bool HasPlatformCapture
    {
        get
        {
#if WINDOWS
            return NAudio.Wave.WaveIn.DeviceCount > 0 || ToolProcess.Find("ffmpeg") is not null;
#else
            if (OperatingSystem.IsMacOS())
                return File.Exists("/usr/bin/swiftc") || ToolProcess.Find("swiftc") is not null || ToolProcess.Find("ffmpeg") is not null;
            return FindLinuxRecorder() is not null || ToolProcess.Find("ffmpeg") is not null;
#endif
        }
    }

    public async Task<IActiveRecording> StartAsync(string wavPath, int maxSec, Action? onMaxReached, CancellationToken ct = default)
    {
        SpeechStoragePermissions.EnsurePrivateDir(Path.GetDirectoryName(wavPath)!);

#if WINDOWS
        if (NAudio.Wave.WaveIn.DeviceCount > 0)
            return WindowsWaveRecording.Start(wavPath, maxSec, onMaxReached);
        var ffmpeg = ToolProcess.Find("ffmpeg");
        if (ffmpeg is not null)
            return await ProcessRecording.StartFfmpegAsync(ffmpeg, ["-f", "dshow", "-i", "audio=default"], wavPath, maxSec, onMaxReached, ct);
        throw new InvalidOperationException("No microphone device found (and ffmpeg is not installed).");
#else
        if (OperatingSystem.IsMacOS())
        {
            var (exe, err) = await _appleHelper.EnsureBuiltAsync(ct);
            if (exe is not null)
                return await ProcessRecording.StartAppleHelperAsync(exe, wavPath, maxSec, onMaxReached, ct);

            var ffmpeg = ToolProcess.Find("ffmpeg");
            if (ffmpeg is not null)
                return await ProcessRecording.StartFfmpegAsync(ffmpeg, ["-f", "avfoundation", "-i", ":0"], wavPath, maxSec, onMaxReached, ct);

            throw new InvalidOperationException(err ?? "No macOS audio recorder available.");
        }

        var linuxTool = FindLinuxRecorder();
        if (linuxTool is not null)
            return await ProcessRecording.StartLinuxToolAsync(linuxTool.Value.Path, linuxTool.Value.Kind, wavPath, maxSec, onMaxReached, ct);

        var linFfmpeg = ToolProcess.Find("ffmpeg");
        if (linFfmpeg is not null)
            return await ProcessRecording.StartFfmpegAsync(linFfmpeg, ["-f", "pulse", "-i", "default"], wavPath, maxSec, onMaxReached, ct);

        throw new InvalidOperationException("No audio recorder found. Install pipewire-utils, pulseaudio-utils, alsa-utils, or ffmpeg.");
#endif
    }

#if !WINDOWS
    private static (string Path, string Kind)? FindLinuxRecorder()
    {
        foreach (var name in new[] { "pw-record", "parecord", "arecord" })
        {
            var path = ToolProcess.Find(name);
            if (path is not null) return (path, name);
        }
        return null;
    }
#endif

    /// <summary>Updates WAV RIFF/data chunk lengths in place if a subprocess exited before flushing header sizes.</summary>
    internal static void RepairWavHeaderIfNeeded(string wavPath)
    {
        try
        {
            var info = new FileInfo(wavPath);
            if (!info.Exists || info.Length < 44) return;
            using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            Span<byte> riffHdr = stackalloc byte[12];
            if (fs.Read(riffHdr) < 12) return;
            if (riffHdr[0] != (byte)'R' || riffHdr[1] != (byte)'I' || riffHdr[2] != (byte)'F' || riffHdr[3] != (byte)'F') return;
            if (riffHdr[8] != (byte)'W' || riffHdr[9] != (byte)'A' || riffHdr[10] != (byte)'V' || riffHdr[11] != (byte)'E') return;

            var total = (uint)Math.Min(info.Length, uint.MaxValue);
            var currentRiffSize = BinaryPrimitives.ReadUInt32LittleEndian(riffHdr.Slice(4, 4));
            var expectedRiffSize = total - 8;

            Span<byte> chunkHdr = stackalloc byte[8];
            Span<byte> sizeBuf = stackalloc byte[4];
            long pos = 12;
            while (pos + 8 <= info.Length)
            {
                fs.Position = pos;
                if (fs.Read(chunkHdr) < 8) break;
                var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr.Slice(4, 4));

                if (chunkHdr[0] == (byte)'d' && chunkHdr[1] == (byte)'a' && chunkHdr[2] == (byte)'t' && chunkHdr[3] == (byte)'a')
                {
                    var dataOffset = pos + 8;
                    if (total <= (uint)dataOffset) break;

                    var expectedDataSize = total - (uint)dataOffset;
                    var dataInvalid = chunkSize == 0 || chunkSize >= 0x7FFF_FFFFu || (ulong)dataOffset + chunkSize > total;
                    var riffInvalid = currentRiffSize == 0
                        || currentRiffSize >= 0x7FFF_FFFFu
                        || currentRiffSize > expectedRiffSize
                        || (ulong)currentRiffSize + 8 < (ulong)dataOffset + (dataInvalid ? expectedDataSize : chunkSize);

                    if (riffInvalid)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, expectedRiffSize);
                        fs.Position = 4;
                        fs.Write(sizeBuf);
                    }
                    if (dataInvalid)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, expectedDataSize);
                        fs.Position = pos + 4;
                        fs.Write(sizeBuf);
                    }
                    break;
                }

                // RIFF chunks are word-aligned (odd sizes have a trailing pad byte)
                var nextPos = pos + 8L + chunkSize + (chunkSize & 1u);
                if (nextPos <= pos || nextPos > info.Length) break;
                pos = nextPos;
            }
        }
        catch { }
    }
}

#if WINDOWS
internal sealed class WindowsWaveRecording : IActiveRecording
{
    private readonly NAudio.Wave.WaveIn _waveIn;
    private readonly NAudio.Wave.WaveFileWriter _writer;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Threading.Timer _limitTimer;
    private readonly object _sync = new();
    private Exception? _recordingError;
    private bool _disposed;
    private bool _stopRequested;

    public string WavPath { get; }

    private WindowsWaveRecording(string wavPath, NAudio.Wave.WaveIn waveIn, NAudio.Wave.WaveFileWriter writer, int maxSec, Action? onMaxReached)
    {
        WavPath = wavPath;
        _waveIn = waveIn;
        _writer = writer;
        _limitTimer = new System.Threading.Timer(_ =>
        {
            lock (_sync)
            {
                if (_stopRequested) return;
                _stopRequested = true;
            }
            try { _waveIn.StopRecording(); } catch { }
            onMaxReached?.Invoke();
        }, null, TimeSpan.FromSeconds(Math.Clamp(maxSec, 5, 600)), Timeout.InfiniteTimeSpan);
    }

    public static WindowsWaveRecording Start(string wavPath, int maxSec, Action? onMaxReached)
    {
        var format = new NAudio.Wave.WaveFormat(16000, 16, 1);
        NAudio.Wave.WaveIn? waveIn = null;
        NAudio.Wave.WaveFileWriter? writer = null;
        WindowsWaveRecording? rec = null;

        try
        {
            waveIn = new NAudio.Wave.WaveIn { WaveFormat = format, BufferMilliseconds = 50 };
            writer = new NAudio.Wave.WaveFileWriter(wavPath, format);
            rec = new WindowsWaveRecording(wavPath, waveIn, writer, maxSec, onMaxReached);

            waveIn.DataAvailable += (_, e) =>
            {
                lock (rec._sync)
                {
                    if (!rec._disposed && e.BytesRecorded > 0)
                        writer.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };

            waveIn.RecordingStopped += (_, e) =>
            {
                lock (rec._sync)
                {
                    if (e.Exception is not null && rec._recordingError is null)
                        rec._recordingError = e.Exception;
                    if (!rec._disposed)
                    {
                        rec._disposed = true;
                        try { writer.Dispose(); } catch { }
                    }
                }
                rec._stopped.TrySetResult();
            };

            waveIn.StartRecording();
            return rec;
        }
        catch
        {
            if (rec is not null)
            {
                rec.DisposeCore();
            }
            else
            {
                try { writer?.Dispose(); } catch { }
                try { waveIn?.Dispose(); } catch { }
            }
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
            throw;
        }
    }

    public async Task<string?> StopAsync(CancellationToken ct = default)
    {
        lock (_sync) { _stopRequested = true; }
        _limitTimer.Dispose();
        try { _waveIn.StopRecording(); } catch { }
        await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Exception? recErr;
        lock (_sync) { recErr = _recordingError; }
        if (recErr is not null)
            return $"Audio capture failed: {recErr.Message}";
        return File.Exists(WavPath) && new FileInfo(WavPath).Length > 44 ? null : "No audio was captured.";
    }

    public async Task CancelAsync()
    {
        try { await StopAsync(); } catch { }
        try { File.Delete(WavPath); } catch { }
    }

    private void DisposeCore()
    {
        _limitTimer.Dispose();
        lock (_sync)
        {
            if (!_disposed)
            {
                _disposed = true;
                try { _writer.Dispose(); } catch { }
            }
        }
        try { _waveIn.Dispose(); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }
}
#endif

internal sealed class ProcessRecording : IActiveRecording
{
    private readonly Process _proc;
    private readonly StringBuilder _stderr = new();
    private readonly string _stopMode; // "apple-stdin" | "ffmpeg-q" | "sigint"
    private readonly System.Threading.Timer _maxTimer;
    private int _stopping;

    public string WavPath { get; }

    private ProcessRecording(Process proc, string wavPath, string stopMode, int maxSec, Action? onMaxReached)
    {
        _proc = proc;
        WavPath = wavPath;
        _stopMode = stopMode;

        _proc.EnableRaisingEvents = true;
        _proc.Exited += (_, _) =>
        {
            if (Volatile.Read(ref _stopping) == 0)
                onMaxReached?.Invoke();
        };

        _maxTimer = new System.Threading.Timer(_ =>
        {
            if (Volatile.Read(ref _stopping) == 0)
                onMaxReached?.Invoke();
        }, null, TimeSpan.FromSeconds(Math.Clamp(maxSec, 5, 600) + 1), Timeout.InfiniteTimeSpan);
    }

    public static async Task<ProcessRecording> StartAppleHelperAsync(
        string exePath, string wavPath, int maxSec, Action? onMaxReached, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = AppPaths.NeutralCwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        SpeechProcessEnv.ConfigureMinimal(psi);
        psi.ArgumentList.Add("record");
        psi.ArgumentList.Add(wavPath);
        psi.ArgumentList.Add(maxSec.ToString());

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start macOS speech helper.");
        var rec = new ProcessRecording(proc, wavPath, "apple-stdin", maxSec, onMaxReached);
        rec.StartErrorPump();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(timeoutCts.Token);
                if (line is null) break;
                if (line.Trim() == "READY")
                {
                    _ = rec.DrainStdoutAsync();
                    return rec;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await rec.CancelAsync();
            throw new TimeoutException("Timed out waiting for macOS microphone to start (check Microphone permissions).");
        }

        var errText = await rec.ReadErrorAndCleanupAsync();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(errText) ? "macOS microphone failed to start." : errText);
    }

    public static async Task<ProcessRecording> StartFfmpegAsync(
        string ffmpegPath, string[] inputArgs, string wavPath, int maxSec, Action? onMaxReached, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            WorkingDirectory = AppPaths.NeutralCwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        SpeechProcessEnv.ConfigureMinimal(psi);
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var a in inputArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(maxSec.ToString());
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add(wavPath);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg recorder.");
        var rec = new ProcessRecording(proc, wavPath, "ffmpeg-q", maxSec, onMaxReached);
        rec.StartErrorPump();
        _ = rec.DrainStdoutAsync();

        await Task.Delay(250, ct);
        if (proc.HasExited && proc.ExitCode != 0)
        {
            var errText = await rec.ReadErrorAndCleanupAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errText) ? "ffmpeg microphone capture failed." : errText);
        }
        return rec;
    }

    public static async Task<ProcessRecording> StartLinuxToolAsync(
        string toolPath, string kind, string wavPath, int maxSec, Action? onMaxReached, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(toolPath)
        {
            WorkingDirectory = AppPaths.NeutralCwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        SpeechProcessEnv.ConfigureMinimal(psi);

        switch (kind)
        {
            case "pw-record":
                psi.ArgumentList.Add("--rate"); psi.ArgumentList.Add("16000");
                psi.ArgumentList.Add("--channels"); psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("--format"); psi.ArgumentList.Add("s16");
                psi.ArgumentList.Add(wavPath);
                break;
            case "parecord":
                psi.ArgumentList.Add("--rate=16000");
                psi.ArgumentList.Add("--channels=1");
                psi.ArgumentList.Add("--format=s16le");
                psi.ArgumentList.Add("--file-format=wav");
                psi.ArgumentList.Add(wavPath);
                break;
            default:
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("S16_LE");
                psi.ArgumentList.Add("-r"); psi.ArgumentList.Add("16000");
                psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(maxSec.ToString());
                psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("wav");
                psi.ArgumentList.Add(wavPath);
                break;
        }

        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {kind}.");
        var rec = new ProcessRecording(proc, wavPath, "sigint", maxSec, onMaxReached);
        rec.StartErrorPump();
        _ = rec.DrainStdoutAsync();

        await Task.Delay(250, ct);
        if (proc.HasExited && proc.ExitCode != 0)
        {
            var errText = await rec.ReadErrorAndCleanupAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errText) ? $"{kind} failed to record." : errText);
        }
        return rec;
    }

    private void StartErrorPump()
    {
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr)
            {
                if (_stderr.Length < 4096) _stderr.AppendLine(e.Data);
            }
        };
        _proc.BeginErrorReadLine();
    }

    private async Task DrainStdoutAsync()
    {
        try
        {
            while (await _proc.StandardOutput.ReadLineAsync() is not null) { }
        }
        catch { }
    }

    private async Task<string> ReadErrorAndCleanupAsync()
    {
        try { await _proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        string msg;
        lock (_stderr) { msg = _stderr.ToString().Trim(); }
        await DisposeAsync();
        return msg;
    }

    public async Task<string?> StopAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _stopping, 1);
        _maxTimer.Dispose();

        if (!_proc.HasExited)
        {
            try
            {
                if (_stopMode == "apple-stdin")
                {
                    await _proc.StandardInput.WriteLineAsync("STOP");
                    await _proc.StandardInput.FlushAsync();
                    _proc.StandardInput.Close();
                }
                else if (_stopMode == "ffmpeg-q")
                {
                    await _proc.StandardInput.WriteAsync("q");
                    await _proc.StandardInput.FlushAsync();
                    _proc.StandardInput.Close();
                }
                else if (_stopMode == "sigint" && File.Exists("/bin/kill"))
                {
                    var sigPsi = new ProcessStartInfo("/bin/kill", ["-INT", _proc.Id.ToString()]);
                    SpeechProcessEnv.ConfigureMinimal(sigPsi);
                    using var sig = Process.Start(sigPsi);
                    if (sig is not null) await sig.WaitForExitAsync(ct);
                }
            }
            catch { }

            try
            {
                await _proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch
            {
                try { _proc.Kill(entireProcessTree: true); } catch { }
            }
        }

        PlatformSpeechAudioCapture.RepairWavHeaderIfNeeded(WavPath);
        SpeechStoragePermissions.EnsurePrivateFile(WavPath);

        if (!File.Exists(WavPath) || new FileInfo(WavPath).Length <= 44)
        {
            string err;
            lock (_stderr) { err = _stderr.ToString().Trim(); }
            return string.IsNullOrWhiteSpace(err) ? "No audio was captured." : err;
        }
        return null;
    }

    public async Task CancelAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _maxTimer.Dispose();
        try
        {
            if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
        }
        catch { }
        await Task.CompletedTask;
        try { File.Delete(WavPath); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        _maxTimer.Dispose();
        try { _proc.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
