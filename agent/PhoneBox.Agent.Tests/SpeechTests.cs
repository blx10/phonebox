using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

public sealed class SpeechTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "phonebox-speech-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private PushStore CreatePushStore() =>
        new(NullLogger<PushStore>.Instance, Path.Combine(_dir, "push-" + Guid.NewGuid().ToString("N") + ".json"));

    private ConfigStore CreateStore(AgentConfig? custom = null, bool speechEnabled = true)
    {
        var path = Path.Combine(_dir, "config-" + Guid.NewGuid().ToString("N") + ".json");
        var cfg = custom ?? new AgentConfig
        {
            Speech = new SpeechConfig
            {
                Enabled = speechEnabled,
                Provider = "apple",
                SaveTranscripts = false,
            },
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets =
                    [
                        new WidgetConfig
                        {
                            Id = "speech-1",
                            Type = "speech",
                            X = 0, Y = 0, W = 4, H = 4,
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["title"] = JsonSerializer.SerializeToElement("Quick Dictate"),
                                ["provider"] = JsonSerializer.SerializeToElement("default"),
                                ["noteTarget"] = JsonSerializer.SerializeToElement("home/note-1"),
                                ["noteMode"] = JsonSerializer.SerializeToElement("append"),
                                ["autoAppendNote"] = JsonSerializer.SerializeToElement(false),
                                ["control"] = JsonSerializer.SerializeToElement(true),
                            },
                        },
                        new WidgetConfig
                        {
                            Id = "note-1",
                            Type = "note",
                            X = 4, Y = 0, W = 4, H = 3,
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["text"] = JsonSerializer.SerializeToElement("Write something here…"),
                            },
                        },
                        new WidgetConfig
                        {
                            Id = "stack-1",
                            Type = "stack",
                            X = 8, Y = 0, W = 4, H = 4,
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["widgets"] = JsonSerializer.SerializeToElement(new object[]
                                {
                                    new
                                    {
                                        id = "s-inactive-note",
                                        type = "note",
                                        enabled = true,
                                        active = false,
                                        options = new { text = "Disabled card" },
                                    },
                                    new
                                    {
                                        id = "s-note",
                                        type = "note",
                                        enabled = true,
                                        options = new { text = "Existing line" },
                                    },
                                    new
                                    {
                                        id = "s-speech",
                                        type = "speech",
                                        enabled = true,
                                        options = new
                                        {
                                            title = "Stack Mic",
                                            mode = "hold",
                                            noteTarget = "home/stack-1:s-note",
                                            noteMode = "bullet",
                                            autoAppendNote = true,
                                            control = true,
                                        },
                                    },
                                }),
                            },
                        },
                    ],
                },
            ],
        };
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, ConfigStore.WriteOpts));
        return new ConfigStore(path);
    }

    private static void UpdateStore(ConfigStore store, Action<AgentConfig> mutate)
    {
        var cur = JsonSerializer.Deserialize<AgentConfig>(JsonSerializer.Serialize(store.Get(), ConfigStore.WriteOpts), ConfigStore.WriteOpts)!;
        mutate(cur);
        Assert.Null(store.Save(JsonSerializer.Serialize(cur, ConfigStore.WriteOpts)));
    }

    private sealed class FakeActiveRecording(string wavPath) : IActiveRecording
    {
        public string WavPath { get; } = wavPath;
        public bool ThrowOnStop { get; set; }
        public bool WasCanceled { get; private set; }

        public Task<string?> StopAsync(CancellationToken ct = default)
        {
            if (ThrowOnStop)
                throw new InvalidOperationException("Simulated capture failure at " + WavPath);
            Directory.CreateDirectory(Path.GetDirectoryName(WavPath)!);
            File.WriteAllBytes(WavPath, BuildTestWav(1600));
            return Task.FromResult<string?>(null);
        }

        public Task CancelAsync()
        {
            WasCanceled = true;
            try { if (File.Exists(WavPath)) File.Delete(WavPath); } catch { }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCapture : ISpeechAudioCapture
    {
        public bool HasPlatformCapture => true;
        public FakeActiveRecording? LastRecording { get; private set; }
        public bool NextThrowOnStop { get; set; }

        public Task<IActiveRecording> StartAsync(string wavPath, int maxSec, Action? onMaxReached, CancellationToken ct = default)
        {
            LastRecording = new FakeActiveRecording(wavPath) { ThrowOnStop = NextThrowOnStop };
            return Task.FromResult<IActiveRecording>(LastRecording);
        }
    }

    private sealed class FakeAppleClient : ISpeechAppleClient
    {
        public bool IsSupported { get; set; } = true;
        public string NextTranscript { get; set; } = "Hello from Apple Speech";
        public Func<CancellationToken, Task<(string? Text, string? Error)>>? CustomTranscribe { get; set; }

        public Task<(string? Executable, string? Error)> EnsureBuiltAsync(CancellationToken ct = default) =>
            Task.FromResult<(string?, string?)>((IsSupported ? "/tmp/PhoneBoxSpeech" : null, IsSupported ? null : "macOS only"));

        public Task<AppleSpeechStatus> GetStatusAsync(string? locale, CancellationToken ct = default) =>
            Task.FromResult(new AppleSpeechStatus(IsSupported, IsSupported, IsSupported ? "authorized" : "unsupported", IsSupported ? "authorized" : "unsupported"));

        public Task<(string? Text, string? Error)> TranscribeAsync(string wavPath, string? locale, bool onDevice, CancellationToken ct) =>
            CustomTranscribe is not null
                ? CustomTranscribe(ct)
                : Task.FromResult<(string?, string?)>((NextTranscript, null));
    }

    private static byte[] BuildTestWav(int pcmBytes)
    {
        var total = 44 + pcmBytes;
        var wav = new byte[total];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(total - 8).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(16000).CopyTo(wav, 24);
        BitConverter.GetBytes(32000).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(pcmBytes).CopyTo(wav, 40);
        return wav;
    }

    private static int FreeLoopbackPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public void SpeechConfigNormalize_DefaultsOffAndClampsValues()
    {
        var cfg = new SpeechConfig
        {
            Provider = "openai", // Stage 1 does not support cloud providers
            MaxRecordSec = 9999,
            HistoryLimit = 0,
            TranscriptFormat = "invalid-format",
            TranscriptDir = "bad\0path",
        };
        cfg.Normalize();

        Assert.False(cfg.Enabled);
        Assert.Equal("auto", cfg.Provider);
        Assert.Equal(600, cfg.MaxRecordSec);
        Assert.Equal(15, cfg.HistoryLimit);
        Assert.Equal("daily-md", cfg.TranscriptFormat);
        Assert.Equal("", cfg.TranscriptDir);
    }

    [Fact]
    public void ToPublic_ExcludesSpeechConfigAndUsesAllowListWidgetScrubbing()
    {
        var store = CreateStore(new AgentConfig
        {
            Speech = new SpeechConfig
            {
                Enabled = true,
                TranscriptDir = "/Users/secret/Transcripts",
            },
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets =
                    [
                        new WidgetConfig
                        {
                            Id = "s1",
                            Type = "speech",
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["title"] = JsonSerializer.SerializeToElement("Mic"),
                                ["transcriptDir"] = JsonSerializer.SerializeToElement("/Users/secret/Folder"),
                                ["unexpectedSecret"] = JsonSerializer.SerializeToElement("should-not-leak"),
                                ["control"] = JsonSerializer.SerializeToElement(true),
                            },
                        },
                        new WidgetConfig
                        {
                            Id = "st1",
                            Type = "stack",
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["widgets"] = JsonSerializer.SerializeToElement(new object[]
                                {
                                    new
                                    {
                                        id = "c1",
                                        type = "speech",
                                        enabled = true,
                                        options = new
                                        {
                                            title = "Stack Mic",
                                            transcriptDir = "/Users/secret/StackFolder",
                                            customHidden = "secret",
                                            control = false,
                                        },
                                    },
                                }),
                            },
                        },
                    ],
                },
            ],
        });

        var json = JsonSerializer.Serialize(store.Get().ToPublic(), ConfigStore.WriteOpts);
        Assert.DoesNotContain("should-not-leak", json);
        Assert.DoesNotContain("/Users/secret", json);
        Assert.DoesNotContain("customHidden", json);
        using var pubDoc = JsonDocument.Parse(json);
        Assert.False(pubDoc.RootElement.TryGetProperty("speech", out _));

        var parsed = SpeechWidgetSettings.Parse(null);
        Assert.False(parsed.Control);
    }

    [Fact]
    public void LanAllowedOverLan_PermitsPhoneEndpointsAndBlocksSettingsEndpoints()
    {
        Assert.True(Lan.AllowedOverLan("/api/speech/state"));
        Assert.True(Lan.AllowedOverLan("/api/speech/start"));
        Assert.True(Lan.AllowedOverLan("/api/speech/stop"));
        Assert.True(Lan.AllowedOverLan("/api/speech/cancel"));
        Assert.True(Lan.AllowedOverLan("/api/speech/append-note"));
        Assert.True(Lan.AllowedOverLan("/api/speech/clear"));

        Assert.False(Lan.AllowedOverLan("/api/speech/setup-status"));
        Assert.False(Lan.AllowedOverLan("/api/speech/browse-dirs"));
        Assert.False(Lan.AllowedOverLan("/api/speech/test"));
    }

    [Fact]
    public void ProcessEnvConfigureMinimal_StripsSecrets()
    {
        var key = "PHONEBOX_TEST_SECRET_KEY";
        Environment.SetEnvironmentVariable(key, "super-secret-value");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("echo");
            psi.Environment[key] = "super-secret-value";
            SpeechProcessEnv.ConfigureMinimal(psi);

            Assert.False(psi.UseShellExecute);
            Assert.False(psi.Environment.ContainsKey(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void StoragePermissions_SetOwnerOnlyModesOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        var secretDir = Path.Combine(_dir, "private-dir");
        var secretFile = Path.Combine(secretDir, "note.txt");
        SpeechStoragePermissions.EnsurePrivateDir(secretDir);
        File.WriteAllText(secretFile, "secret");
        SpeechStoragePermissions.EnsurePrivateFile(secretFile);

        var dirMode = File.GetUnixFileMode(secretDir);
        var fileMode = File.GetUnixFileMode(secretFile);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, dirMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);
    }

    [Fact]
    public void RepairWavHeaderIfNeeded_FixesRiffAndDataSizes()
    {
        var raw = BuildTestWav(1600);
        // Corrupt size fields like an interrupted streaming WAV writer
        BitConverter.GetBytes(0).CopyTo(raw, 4);
        BitConverter.GetBytes(0).CopyTo(raw, 40);
        var wavPath = Path.Combine(_dir, "unfinalized.wav");
        File.WriteAllBytes(wavPath, raw);

        PlatformSpeechAudioCapture.RepairWavHeaderIfNeeded(wavPath);

        var fixedBytes = File.ReadAllBytes(wavPath);
        Assert.Equal(raw.Length - 8, BitConverter.ToInt32(fixedBytes, 4));
        Assert.Equal(1600, BitConverter.ToInt32(fixedBytes, 40));
    }

    [Fact]
    public async Task FeatureDisabled_AndControlFalse_AreRejected()
    {
        var capture = new FakeCapture();
        var apple = new FakeAppleClient();

        // Disabled globally -> 404
        using (var disabledSvc = new SpeechService(CreateStore(speechEnabled: false), CreatePushStore(), new AgentEndpoint(8765), apple, capture, null, Path.Combine(_dir, "tmp1")))
        {
            var r = await disabledSvc.StartAsync("home/speech-1");
            Assert.False(r.Ok);
            Assert.Equal(404, r.StatusCode);
        }

        // Control false (default) -> 403
        var lockedCfg = new AgentConfig
        {
            Speech = new SpeechConfig { Enabled = true, Provider = "apple" },
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets =
                    [
                        new WidgetConfig { Id = "speech-locked", Type = "speech", Options = new() },
                    ],
                },
            ],
        };
        using var lockedSvc = new SpeechService(CreateStore(lockedCfg, speechEnabled: true), CreatePushStore(), new AgentEndpoint(8765), apple, capture, null, Path.Combine(_dir, "tmp2"));
        var lockedRes = await lockedSvc.StartAsync("home/speech-locked");
        Assert.False(lockedRes.Ok);
        Assert.Equal(403, lockedRes.StatusCode);
    }

    [Fact]
    public async Task StartStopAppendNote_SupportsModesSkipsInactiveStackAndRejectsEmptyTarget()
    {
        var store = CreateStore();
        var capture = new FakeCapture();
        var apple = new FakeAppleClient { NextTranscript = "First note line" };
        using var svc = new SpeechService(store, CreatePushStore(), new AgentEndpoint(8765), apple, capture, null, Path.Combine(_dir, "tmp-note"));

        Assert.True((await svc.StartAsync("home/speech-1")).Ok);
        var stop1 = await svc.StopAsync("home/speech-1");
        Assert.True(stop1.Ok);

        var entry = Assert.Single(svc.GetState().Entries);
        Assert.Equal("First note line", entry.Text);
        Assert.False(entry.AppendedToNote);

        // Append replaces default placeholder
        var app1 = svc.AppendEntryToNote(entry.Id, "home/speech-1");
        Assert.True(app1.Ok);
        var noteText = store.Get().Pages[0].Widgets![1].Options!["text"].GetString();
        Assert.Equal("First note line", noteText);

        // AutoAppend bullet into active stack Note (skips inactive s-inactive-note!)
        apple.NextTranscript = "Stack bullet item";
        Assert.True((await svc.StartAsync("home/stack-1:s-speech")).Ok);
        Assert.True((await svc.StopAsync("home/stack-1:s-speech")).Ok);

        var stackWidgetsJson = store.Get().Pages[0].Widgets![2].Options!["widgets"].GetRawText();
        Assert.Contains("Existing line\\n\\u2022 Stack bullet item", stackWidgetsJson);
        Assert.Contains("Disabled card", stackWidgetsJson);
        Assert.DoesNotContain("Disabled card\\n", stackWidgetsJson);

        // Empty noteTarget must NOT silently fall back to nearest Note widget
        var noTargetCfg = new AgentConfig
        {
            Speech = new SpeechConfig { Enabled = true, Provider = "apple" },
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets =
                    [
                        new WidgetConfig
                        {
                            Id = "sp-empty-target",
                            Type = "speech",
                            Options = new Dictionary<string, JsonElement>
                            {
                                ["noteTarget"] = JsonSerializer.SerializeToElement(""),
                                ["control"] = JsonSerializer.SerializeToElement(true),
                            },
                        },
                        new WidgetConfig
                        {
                            Id = "note-1",
                            Type = "note",
                            Options = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("Original") },
                        },
                    ],
                },
            ],
        };
        using var noTargetSvc = new SpeechService(CreateStore(noTargetCfg), CreatePushStore(), new AgentEndpoint(8765), apple, capture, null, Path.Combine(_dir, "tmp-notarget"));
        Assert.True((await noTargetSvc.StartAsync("home/sp-empty-target")).Ok);
        Assert.True((await noTargetSvc.StopAsync("home/sp-empty-target")).Ok);
        var noTargetEntry = Assert.Single(noTargetSvc.GetState().Entries);
        var failAppend = noTargetSvc.AppendEntryToNote(noTargetEntry.Id, "home/sp-empty-target");
        Assert.False(failAppend.Ok);
        Assert.Equal(400, failAppend.StatusCode);
    }

    [Fact]
    public async Task DiskStorage_SupportsFormatsAudioCopyRejectsUnsafeDirAndScrubsSavedFileInJson()
    {
        var outDir = Path.Combine(_dir, "transcripts");
        foreach (var fmt in SpeechConfig.ValidTranscriptFormats)
        {
            var cfg = new AgentConfig
            {
                Speech = new SpeechConfig
                {
                    Enabled = true,
                    Provider = "apple",
                    SaveTranscripts = true,
                    TranscriptDir = outDir,
                    TranscriptFormat = fmt,
                    SaveAudioCopy = fmt == "per-item-txt",
                },
                Pages =
                [
                    new PageConfig
                    {
                        Id = "home",
                        Title = "Home",
                        Enabled = true,
                        Widgets =
                        [
                            new WidgetConfig
                            {
                                Id = "sp",
                                Type = "speech",
                                Options = new Dictionary<string, JsonElement> { ["control"] = JsonSerializer.SerializeToElement(true) },
                            },
                        ],
                    },
                ],
            };
            using var svc = new SpeechService(CreateStore(cfg), CreatePushStore(), new AgentEndpoint(8765), new FakeAppleClient { NextTranscript = "Saved item " + fmt }, new FakeCapture(), null, Path.Combine(_dir, "tmp-" + fmt));
            Assert.True((await svc.StartAsync("home/sp")).Ok);
            Assert.True((await svc.StopAsync("home/sp")).Ok);
            var st = svc.GetState();
            var dto = Assert.Single(st.Entries);
            Assert.False(string.IsNullOrWhiteSpace(dto.SavedFile));
            Assert.True(File.Exists(dto.SavedFile));
            Assert.False(string.IsNullOrWhiteSpace(dto.SavedFileDisplay));

            // Wire JSON must NEVER include local SavedFile path ([JsonIgnore])
            var wireJson = JsonSerializer.Serialize(st);
            Assert.DoesNotContain("savedFile\"", wireJson, StringComparison.OrdinalIgnoreCase);

            if (fmt == "per-item-txt")
                Assert.True(Directory.EnumerateFiles(Path.Combine(outDir, "audio"), "*.wav").Any());
        }

        // Unsafe root/system paths rejected
        Assert.False(SpeechService.IsSafeTranscriptDirectory("/"));
        Assert.False(SpeechService.IsSafeTranscriptDirectory("/etc"));
        Assert.False(SpeechService.IsSafeTranscriptDirectory("/System"));
    }

    [Fact]
    public async Task CancelAsync_PreventsLatePublicationAndNoteMutation()
    {
        var store = CreateStore();
        // Set autoAppendNote = true on speech-1 to verify CancelAsync prevents Note mutation
        UpdateStore(store, c =>
        {
            c.Pages[0].Widgets![0].Options!["autoAppendNote"] = JsonSerializer.SerializeToElement(true);
        });

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apple = new FakeAppleClient
        {
            CustomTranscribe = async ct =>
            {
                started.TrySetResult();
                await release.Task;
                return ("Ghost transcript after cancel", null);
            },
        };
        using var svc = new SpeechService(store, CreatePushStore(), new AgentEndpoint(8765), apple, new FakeCapture(), null, Path.Combine(_dir, "tmp-cancel"));

        Assert.True((await svc.StartAsync("home/speech-1")).Ok);
        var stopTask = svc.StopAsync("home/speech-1");
        await started.Task;

        // Cancel BEFORE transcription completes (even though CustomTranscribe ignores ct and returns text afterward)
        var cancelRes = await svc.CancelAsync("home/speech-1");
        Assert.True(cancelRes.Ok);

        release.TrySetResult();
        await stopTask;
        await Task.Delay(50);

        Assert.Empty(svc.GetState().Entries);
        Assert.Equal("idle", svc.GetState().Status);
        Assert.Equal("Write something here…", store.Get().Pages[0].Widgets![1].Options!["text"].GetString());
    }

    [Fact]
    public async Task StopAsyncFailure_ResetsStateAndScrubsLocalPaths()
    {
        var capture = new FakeCapture { NextThrowOnStop = true };
        using var svc = new SpeechService(CreateStore(), CreatePushStore(), new AgentEndpoint(8765), new FakeAppleClient(), capture, null, Path.Combine(_dir, "tmp-err"));

        Assert.True((await svc.StartAsync("home/speech-1")).Ok);
        var stopRes = await svc.StopAsync("home/speech-1");
        Assert.False(stopRes.Ok);
        Assert.Equal("idle", svc.GetState().Status);
        Assert.DoesNotContain("/tmp-", stopRes.Error ?? "");
        Assert.DoesNotContain(_dir, stopRes.Error ?? "");
    }

    [Fact]
    public async Task DuplicateWidgetIdsAcrossPages_AreIsolatedAndUnqualifiedIsRejected()
    {
        var cfg = new AgentConfig
        {
            Speech = new SpeechConfig { Enabled = true, Provider = "apple" },
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets = [new WidgetConfig { Id = "speech", Type = "speech", Options = new Dictionary<string, JsonElement> { ["control"] = JsonSerializer.SerializeToElement(true) } }],
                },
                new PageConfig
                {
                    Id = "work",
                    Title = "Work",
                    Enabled = true,
                    Widgets = [new WidgetConfig { Id = "speech", Type = "speech", Options = new Dictionary<string, JsonElement> { ["control"] = JsonSerializer.SerializeToElement(true) } }],
                },
            ],
        };

        var apple = new FakeAppleClient { NextTranscript = "Work item" };
        using var svc = new SpeechService(CreateStore(cfg), CreatePushStore(), new AgentEndpoint(8765), apple, new FakeCapture(), null, Path.Combine(_dir, "tmp-dup"));

        // Unqualified "speech" must NOT silently pick page 1 when duplicate exists
        var ambig = await svc.StartAsync("speech");
        Assert.False(ambig.Ok);
        Assert.Equal(400, ambig.StatusCode);

        // Canonical IDs work independently and Clear scopes strictly
        Assert.True((await svc.StartAsync("home/speech")).Ok);
        apple.NextTranscript = "Home item";
        Assert.True((await svc.StopAsync("home/speech")).Ok);

        Assert.True((await svc.StartAsync("work/speech")).Ok);
        apple.NextTranscript = "Work item";
        Assert.True((await svc.StopAsync("work/speech")).Ok);

        Assert.False(SpeechService.WidgetIdMatches("home/speech", "work/speech", "work/speech"));
        Assert.False(SpeechService.WidgetIdMatches("home/speech", "speech", "work/speech"));

        Assert.True(svc.Clear("work/speech").Ok);
        var remaining = Assert.Single(svc.GetState().Entries);
        Assert.Equal("home/speech", remaining.WidgetId);
    }

    [Fact]
    public async Task KestrelMiddleware_EnforcesDisabledLocalCorsLanTokenDeviceScopeAndRateLimit()
    {
        var localPort = FreeLoopbackPort();
        var lanPort = FreeLoopbackPort();
        while (lanPort == localPort) lanPort = FreeLoopbackPort();

        var token = "test-lan-token-123456";
        var cfg = new AgentConfig
        {
            Speech = new SpeechConfig { Enabled = false, Provider = "apple" },
            Lan = new LanConfig { Enabled = true, Port = lanPort, Token = token, WebhookToken = "wh-token" },
            Phones =
            [
                new PhoneConfig { Id = "dev-speech", Name = "Allowed Phone", Pages = ["home"] },
                new PhoneConfig { Id = "dev-other", Name = "Other Phone", Pages = ["other"] },
            ],
            Pages =
            [
                new PageConfig
                {
                    Id = "home",
                    Title = "Home",
                    Enabled = true,
                    Widgets = [new WidgetConfig { Id = "sp", Type = "speech", Options = new Dictionary<string, JsonElement> { ["control"] = JsonSerializer.SerializeToElement(true) } }],
                },
                new PageConfig
                {
                    Id = "other",
                    Title = "Other",
                    Enabled = true,
                    Widgets = [new WidgetConfig { Id = "clk", Type = "clock" }],
                },
            ],
        };

        var store = CreateStore(cfg);
        var push = CreatePushStore();
        var endpoint = new AgentEndpoint(localPort);
        var limiter = new SpeechRateLimiter(maxRequests: 2, window: TimeSpan.FromMinutes(1));
        using var speech = new SpeechService(store, push, endpoint, new FakeAppleClient(), new FakeCapture(), limiter, Path.Combine(_dir, "tmp-kestrel"));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, localPort);
            k.Listen(IPAddress.Loopback, lanPort);
        });
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(push);
        builder.Services.AddSingleton(endpoint);
        builder.Services.AddSingleton(speech);

        await using var app = builder.Build();
        AgentRequestGuard.UseSecurityMiddleware(app, store, localPort, lanPort, hasLanCert: true, _ => false);
        SpeechEndpoints.Map(app, localPort);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient();

            // 1) Feature disabled -> 404 on local state & start
            var disabledGet = await client.GetAsync($"http://127.0.0.1:{localPort}/api/speech/state");
            Assert.Equal(HttpStatusCode.NotFound, disabledGet.StatusCode);

            // Enable feature
            UpdateStore(store, c => c.Speech.Enabled = true);

            // 2) Cross-origin POST -> 403 from AgentRequestGuard
            using var corsReq = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{localPort}/api/speech/start?widgetId=home%2Fsp");
            corsReq.Headers.Add("Origin", "https://evil.example");
            var corsRes = await client.SendAsync(corsReq);
            Assert.Equal(HttpStatusCode.Forbidden, corsRes.StatusCode);

            // 3) Unauthenticated LAN request -> 403
            var noAuthLan = await client.GetAsync($"http://127.0.0.1:{lanPort}/api/speech/state");
            Assert.Equal(HttpStatusCode.Forbidden, noAuthLan.StatusCode);

            // 4) Settings-only endpoint over LAN -> 403
            using var setupLanReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{lanPort}/api/speech/setup-status");
            setupLanReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var setupLanRes = await client.SendAsync(setupLanReq);
            Assert.Equal(HttpStatusCode.Forbidden, setupLanRes.StatusCode);

            // 5) Authenticated LAN request from phone without speech page -> 403
            using var wrongPhoneReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{lanPort}/api/speech/state");
            wrongPhoneReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            wrongPhoneReq.Headers.Add("X-PhoneBox-Device", "dev-other");
            var wrongPhoneRes = await client.SendAsync(wrongPhoneReq);
            Assert.Equal(HttpStatusCode.Forbidden, wrongPhoneRes.StatusCode);

            // 6) Authenticated LAN request from assigned phone -> 200
            using var okLanReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{lanPort}/api/speech/state");
            okLanReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            okLanReq.Headers.Add("X-PhoneBox-Device", "dev-speech");
            var okLanRes = await client.SendAsync(okLanReq);
            Assert.Equal(HttpStatusCode.OK, okLanRes.StatusCode);

            // 7) Rate limiter -> 3rd rapid action returns 429
            var r1 = await client.PostAsync($"http://127.0.0.1:{localPort}/api/speech/clear?widgetId=home%2Fsp", null);
            var r2 = await client.PostAsync($"http://127.0.0.1:{localPort}/api/speech/clear?widgetId=home%2Fsp", null);
            var r3 = await client.PostAsync($"http://127.0.0.1:{localPort}/api/speech/clear?widgetId=home%2Fsp", null);
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, r3.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task AppleSpeechHelper_CompilesOnMacOs()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var helper = new SpeechAppleHelper();
        var (exe, err) = await helper.EnsureBuiltAsync();
        Assert.Null(err);
        Assert.False(string.IsNullOrWhiteSpace(exe));
        Assert.True(File.Exists(exe));
    }
}
