using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed record AppleSpeechStatus(
    bool Available,
    bool OnDeviceSupported,
    string SpeechAuth,
    string MicAuth,
    string? Error = null);

internal interface ISpeechAppleClient
{
    bool IsSupported { get; }
    Task<(string? Executable, string? Error)> EnsureBuiltAsync(CancellationToken ct = default);
    Task<AppleSpeechStatus> GetStatusAsync(string? locale, CancellationToken ct = default);
    Task<(string? Text, string? Error)> TranscribeAsync(string wavPath, string? locale, bool onDevice, CancellationToken ct);
}

/// <summary>Lazily compiles, signs, and invokes a lightweight macOS Swift helper app for mic capture and SFSpeechRecognizer.</summary>
internal sealed class SpeechAppleHelper : ISpeechAppleClient
{
    private const string BundleId = "net.oxog.phonebox.speech-helper";
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    private static readonly string SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SwiftSource + InfoPlist)));

    public bool IsSupported => OperatingSystem.IsMacOS();
    public static string BundleDir => Path.Combine(AppPaths.LocalDir, "speech-helper", "PhoneBoxSpeech.app");
    public static string ExecutablePath => Path.Combine(BundleDir, "Contents", "MacOS", "PhoneBoxSpeech");
    private static string StampPath => Path.Combine(BundleDir, "Contents", "Resources", "source.sha256");

    public async Task<(string? Executable, string? Error)> EnsureBuiltAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS())
            return (null, "Apple Speech Recognition is only supported on macOS.");

        if (IsUpToDate()) return (ExecutablePath, null);

        await _buildLock.WaitAsync(ct);
        try
        {
            if (IsUpToDate()) return (ExecutablePath, null);

            var swiftc = ToolProcess.Find("swiftc");
            if (string.IsNullOrWhiteSpace(swiftc) && File.Exists("/usr/bin/swiftc")) swiftc = "/usr/bin/swiftc";
            if (string.IsNullOrWhiteSpace(swiftc))
                return (null, "Apple swiftc compiler not found. Install Xcode Command Line Tools (`xcode-select --install`).");

            var helperRoot = Path.GetDirectoryName(BundleDir)!;
            var contentsDir = Path.Combine(BundleDir, "Contents");
            var macOsDir = Path.Combine(contentsDir, "MacOS");
            var resDir = Path.Combine(contentsDir, "Resources");
            SpeechStoragePermissions.EnsurePrivateDir(helperRoot);
            SpeechStoragePermissions.EnsurePrivateDir(BundleDir);
            SpeechStoragePermissions.EnsurePrivateDir(contentsDir);
            SpeechStoragePermissions.EnsurePrivateDir(macOsDir);
            SpeechStoragePermissions.EnsurePrivateDir(resDir);

            var srcFile = Path.Combine(helperRoot, "main.swift");
            var plistFile = Path.Combine(contentsDir, "Info.plist");
            var tmpExe = ExecutablePath + ".tmp";

            await File.WriteAllTextAsync(srcFile, SwiftSource, Encoding.UTF8, ct);
            await File.WriteAllTextAsync(plistFile, InfoPlist, Encoding.UTF8, ct);
            SpeechStoragePermissions.EnsurePrivateFile(srcFile);
            SpeechStoragePermissions.EnsurePrivateFile(plistFile);

            var compile = new ProcessStartInfo(swiftc)
            {
                WorkingDirectory = AppPaths.NeutralCwd,
                ArgumentList =
                {
                    "-O",
                    "-swift-version", "5",
                    srcFile,
                    "-o", tmpExe,
                    "-framework", "Foundation",
                    "-framework", "Speech",
                    "-framework", "AVFoundation",
                },
            };
            SpeechProcessEnv.ConfigureMinimal(compile);

            var res = await ToolProcess.RunAsync(compile, TimeSpan.FromSeconds(120), ct);
            if (res.ExitCode != 0 || !File.Exists(tmpExe))
            {
                var detail = string.IsNullOrWhiteSpace(res.Error) ? res.Output : res.Error;
                return (null, $"Failed to compile macOS speech helper: {detail.Trim()}");
            }

            File.Move(tmpExe, ExecutablePath, overwrite: true);

            var codesign = File.Exists("/usr/bin/codesign") ? "/usr/bin/codesign" : ToolProcess.Find("codesign");
            if (!string.IsNullOrWhiteSpace(codesign))
            {
                var sign = new ProcessStartInfo(codesign)
                {
                    WorkingDirectory = AppPaths.NeutralCwd,
                    ArgumentList = { "--force", "--deep", "--sign", "-", BundleDir },
                };
                SpeechProcessEnv.ConfigureMinimal(sign);
                await ToolProcess.RunAsync(sign, TimeSpan.FromSeconds(30), ct);
            }

            await File.WriteAllTextAsync(StampPath, SourceHash, Encoding.UTF8, ct);
            SpeechStoragePermissions.EnsurePrivateFile(StampPath);
            return (ExecutablePath, null);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    private static bool IsUpToDate() =>
        File.Exists(ExecutablePath)
        && File.Exists(StampPath)
        && string.Equals(File.ReadAllText(StampPath).Trim(), SourceHash, StringComparison.Ordinal);

    public async Task<AppleSpeechStatus> GetStatusAsync(string? locale, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS())
            return new AppleSpeechStatus(false, false, "unsupported", "unsupported", "macOS required.");

        var (exe, buildErr) = await EnsureBuiltAsync(ct);
        if (exe is null)
            return new AppleSpeechStatus(false, false, "error", "error", buildErr);

        var psi = new ProcessStartInfo(exe) { WorkingDirectory = AppPaths.NeutralCwd };
        SpeechProcessEnv.ConfigureMinimal(psi);
        psi.ArgumentList.Add("status");
        if (!string.IsNullOrWhiteSpace(locale)) psi.ArgumentList.Add(locale.Trim());

        var res = await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(15), ct);
        if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.Output))
            return new AppleSpeechStatus(false, false, "unknown", "unknown", string.IsNullOrWhiteSpace(res.Error) ? "Helper failed." : res.Error.Trim());

        try
        {
            using var doc = JsonDocument.Parse(res.Output);
            var root = doc.RootElement;
            return new AppleSpeechStatus(
                root.TryGetProperty("available", out var av) && av.ValueKind == JsonValueKind.True,
                root.TryGetProperty("onDeviceSupported", out var od) && od.ValueKind == JsonValueKind.True,
                root.TryGetProperty("speechAuth", out var sa) ? sa.GetString() ?? "unknown" : "unknown",
                root.TryGetProperty("micAuth", out var ma) ? ma.GetString() ?? "unknown" : "unknown");
        }
        catch (JsonException ex)
        {
            return new AppleSpeechStatus(false, false, "unknown", "unknown", ex.Message);
        }
    }

    public async Task<(string? Text, string? Error)> TranscribeAsync(string wavPath, string? locale, bool onDevice, CancellationToken ct)
    {
        var (exe, buildErr) = await EnsureBuiltAsync(ct);
        if (exe is null) return (null, buildErr);

        var psi = new ProcessStartInfo(exe) { WorkingDirectory = AppPaths.NeutralCwd };
        SpeechProcessEnv.ConfigureMinimal(psi);
        psi.ArgumentList.Add("transcribe");
        psi.ArgumentList.Add(wavPath);
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(locale) ? "" : locale.Trim());
        psi.ArgumentList.Add(onDevice ? "1" : "0");

        var res = await ToolProcess.RunAsync(psi, TimeSpan.FromSeconds(120), ct);
        if (res.ExitCode != 0)
        {
            var err = string.IsNullOrWhiteSpace(res.Error) ? res.Output : res.Error;
            return (null, string.IsNullOrWhiteSpace(err) ? $"Apple Speech failed (exit {res.ExitCode})." : err.Trim());
        }

        try
        {
            using var doc = JsonDocument.Parse(res.Output);
            var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            return (text.Trim(), null);
        }
        catch (JsonException ex)
        {
            return (null, $"Invalid Apple Speech response: {ex.Message}");
        }
    }

    private const string InfoPlist = $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>{BundleId}</string>
    <key>CFBundleName</key>
    <string>PhoneBoxSpeech</string>
    <key>CFBundleExecutable</key>
    <string>PhoneBoxSpeech</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>PhoneBox needs microphone access to record speech for transcription.</string>
    <key>NSSpeechRecognitionUsageDescription</key>
    <string>PhoneBox uses Apple Speech Recognition to transcribe your voice.</string>
</dict>
</plist>
""";

    private const string SwiftSource = """
import Foundation
import Speech
import AVFoundation

func fail(_ msg: String) -> Never {
    FileHandle.standardError.write(Data((msg + "\n").utf8))
    exit(1)
}

func authLabel(_ status: SFSpeechRecognizerAuthorizationStatus) -> String {
    switch status {
    case .authorized: return "authorized"
    case .notDetermined: return "notDetermined"
    case .denied: return "denied"
    case .restricted: return "restricted"
    @unknown default: return "unknown"
    }
}

func micLabel(_ status: AVAuthorizationStatus) -> String {
    switch status {
    case .authorized: return "authorized"
    case .notDetermined: return "notDetermined"
    case .denied: return "denied"
    case .restricted: return "restricted"
    @unknown default: return "unknown"
    }
}

func makeRecognizer(_ localeId: String?) -> SFSpeechRecognizer? {
    if let raw = localeId, !raw.isEmpty {
        return SFSpeechRecognizer(locale: Locale(identifier: raw))
    }
    return SFSpeechRecognizer()
}

func runStatus(_ localeId: String?) {
    let rec = makeRecognizer(localeId)
    let payload: [String: Any] = [
        "available": rec?.isAvailable ?? false,
        "onDeviceSupported": rec?.supportsOnDeviceRecognition ?? false,
        "speechAuth": authLabel(SFSpeechRecognizer.authorizationStatus()),
        "micAuth": micLabel(AVCaptureDevice.authorizationStatus(for: .audio)),
    ]
    let data = try! JSONSerialization.data(withJSONObject: payload)
    print(String(data: data, encoding: .utf8)!)
}

func ensureMicPermission() -> Bool {
    let cur = AVCaptureDevice.authorizationStatus(for: .audio)
    if cur == .authorized { return true }
    if cur == .denied || cur == .restricted { return false }
    let sem = DispatchSemaphore(value: 0)
    var granted = false
    AVCaptureDevice.requestAccess(for: .audio) { ok in
        granted = ok
        sem.signal()
    }
    _ = sem.wait(timeout: .now() + 60)
    return granted
}

func ensureSpeechPermission() -> SFSpeechRecognizerAuthorizationStatus {
    let cur = SFSpeechRecognizer.authorizationStatus()
    if cur != .notDetermined { return cur }
    let sem = DispatchSemaphore(value: 0)
    var result = cur
    SFSpeechRecognizer.requestAuthorization { st in
        result = st
        sem.signal()
    }
    _ = sem.wait(timeout: .now() + 60)
    return result
}

func runRecord(_ wavPath: String, _ maxSec: Double) {
    if !ensureMicPermission() {
        fail("Microphone permission denied. Grant access in System Settings > Privacy & Security > Microphone.")
    }

    let url = URL(fileURLWithPath: wavPath)
    let settings: [String: Any] = [
        AVFormatIDKey: Int(kAudioFormatLinearPCM),
        AVSampleRateKey: 16000.0,
        AVNumberOfChannelsKey: 1,
        AVLinearPCMBitDepthKey: 16,
        AVLinearPCMIsBigEndianKey: false,
        AVLinearPCMIsFloatKey: false,
    ]

    let recorder: AVAudioRecorder
    do {
        recorder = try AVAudioRecorder(url: url, settings: settings)
    } catch {
        fail("Could not initialize audio recorder: \(error.localizedDescription)")
    }

    if !recorder.prepareToRecord() || !recorder.record() {
        fail("Failed to start macOS microphone recording.")
    }

    print("READY")
    fflush(stdout)

    let stopFlag = UnsafeMutablePointer<Int32>.allocate(capacity: 1)
    stopFlag.initialize(to: 0)
    let lock = NSLock()
    func markStop() {
        lock.lock()
        stopFlag.pointee = 1
        lock.unlock()
    }
    func shouldStop() -> Bool {
        lock.lock()
        let v = stopFlag.pointee != 0
        lock.unlock()
        return v
    }

    signal(SIGTERM, SIG_IGN)
    signal(SIGINT, SIG_IGN)
    let termSrc = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .global())
    termSrc.setEventHandler { markStop() }
    termSrc.resume()
    let intSrc = DispatchSource.makeSignalSource(signal: SIGINT, queue: .global())
    intSrc.setEventHandler { markStop() }
    intSrc.resume()

    DispatchQueue.global().async {
        while let line = readLine() {
            if line.trimmingCharacters(in: .whitespacesAndNewlines).uppercased() == "STOP" {
                markStop()
                break
            }
        }
        markStop()
    }

    let deadline = Date().addingTimeInterval(max(1.0, min(maxSec, 600.0)))
    while !shouldStop() && Date() < deadline {
        RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.05))
    }

    recorder.stop()
    termSrc.cancel()
    intSrc.cancel()
    print("DONE")
    fflush(stdout)
}

func runTranscribe(_ wavPath: String, _ localeId: String?, _ onDeviceFlag: String?) {
    let auth = ensureSpeechPermission()
    guard auth == .authorized else {
        fail("Speech recognition permission (\(authLabel(auth))). Allow access in System Settings > Privacy & Security > Speech Recognition.")
    }

    guard let rec = makeRecognizer(localeId) else {
        fail("Unsupported Apple Speech locale: \(localeId ?? "default")")
    }
    guard rec.isAvailable else {
        fail("Apple Speech recognizer is currently unavailable.")
    }

    let onDevice = (onDeviceFlag == "1")
    if onDevice && !rec.supportsOnDeviceRecognition {
        fail("On-device Apple Speech recognition is not available on this Mac for locale '\(rec.locale.identifier)'.")
    }

    let cbQueue = OperationQueue()
    cbQueue.name = "net.oxog.phonebox.speech-helper.callback"
    cbQueue.maxConcurrentOperationCount = 1
    rec.queue = cbQueue

    let req = SFSpeechURLRecognitionRequest(url: URL(fileURLWithPath: wavPath))
    req.shouldReportPartialResults = false
    if onDevice {
        req.requiresOnDeviceRecognition = true
    }

    let sem = DispatchSemaphore(value: 0)
    let lock = NSLock()
    var finalText = ""
    var taskError: Error?

    let task = rec.recognitionTask(with: req) { result, error in
        lock.lock()
        if let r = result {
            finalText = r.bestTranscription.formattedString
            if r.isFinal {
                lock.unlock()
                sem.signal()
                return
            }
        }
        if let err = error {
            taskError = err
            lock.unlock()
            sem.signal()
            return
        }
        lock.unlock()
    }

    if sem.wait(timeout: .now() + 120) == .timedOut {
        task.cancel()
        fail("Apple Speech recognition timed out.")
    }

    lock.lock()
    let textOut = finalText.trimmingCharacters(in: .whitespacesAndNewlines)
    let errOut = taskError
    lock.unlock()

    if let err = errOut, textOut.isEmpty {
        let nsErr = err as NSError
        // Code 1110 = No speech detected; treat as empty transcript cleanly
        if !(nsErr.domain == "kAFAssistantErrorDomain" && nsErr.code == 1110) {
            fail("Apple Speech error: \(err.localizedDescription)")
        }
    }

    let data = try! JSONSerialization.data(withJSONObject: ["text": textOut])
    print(String(data: data, encoding: .utf8)!)
}

let args = CommandLine.arguments
guard args.count >= 2 else { fail("Usage: PhoneBoxSpeech <status|record|transcribe> ...") }

switch args[1] {
case "status":
    runStatus(args.count > 2 ? args[2] : nil)
case "record":
    guard args.count >= 4, let sec = Double(args[3]) else { fail("Usage: PhoneBoxSpeech record <wavPath> <maxSec>") }
    runRecord(args[2], sec)
case "transcribe":
    guard args.count >= 3 else { fail("Usage: PhoneBoxSpeech transcribe <wavPath> [locale] [onDevice]") }
    runTranscribe(args[2], args.count > 3 ? args[3] : nil, args.count > 4 ? args[4] : nil)
default:
    fail("Unknown command: \(args[1])")
}
""";
}
