using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Regression tests for the Control Deck defects found in the 0.9.4 audit.</summary>
public sealed class DeckAuditTests
{
    private sealed class Native : IDeckNative
    {
        public readonly List<string> Writes = [];
        public List<DeckSession> Sessions = [];
        public List<DeckMedia> Players = [new("Spotify.exe", "Spotify", "Track", true, 0, 100000, true, true, true, false, "None")];
        public DeckAudio Audio() => new(true, null, [new("speaker", "Speaker", "output", true, 40, false, 0, 2), new("mic", "Mic", "input", true, 70, true, 0, 1)], Sessions.ToArray());
        public bool Device(string id, int? volume, bool? muted, int? channel = null) { Writes.Add("device:" + id); return true; }
        public bool Session(string id, int? volume, bool? muted) { Writes.Add("session:" + id + ":" + volume); Sessions = Sessions.Select(s => s.Id == id ? s with { Volume = volume ?? s.Volume, Muted = muted ?? s.Muted } : s).ToList(); return true; }
        public bool DefaultDevice(string id, int? role = null) => true;
        public IReadOnlyList<DeckMonitor> Monitors() => [];
        public bool Brightness(string id, int value) => false;
        public IReadOnlyList<DeckItem> PowerPlans() => [];
        public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>(Players.ToArray());
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) { Writes.Add("media:" + action + ":" + id); return Task.FromResult(Players.Any(p => p.Id == id)); }
        public void Play(string id, string file, string? device, int volume) { }
        public void StopSound(string? id) { }
        public IReadOnlyList<DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public void Dispose() { }
    }
    private sealed class Capture<T> : ILogger<T>
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format) { lock (Lines) Lines.Add(format(state, ex)); }
    }
    private static string TempDir(string name) { var d = Path.Combine(Path.GetTempPath(), "phonebox-audit-" + name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    // ---- P1: a Spotify HTTP timeout escapes Snapshot()/Act() instead of becoming "unavailable"
    private sealed class Slow : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new(HttpStatusCode.OK); }
    }
    [Fact]
    public async Task SpotifyTimeoutIsReportedAsUnavailableInsteadOfThrowing()
    {
        var dir = TempDir("spotify");
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { controls = new { spotifyClientId = "test-client", spotifyToken = "manual-token" } }));
            using var spotify = new SpotifyControl(new ConfigStore(path), new AgentEndpoint(19885), new HttpClient(new Slow()) { Timeout = TimeSpan.FromMilliseconds(200) });
            var state = await spotify.Snapshot(default);
            Assert.False(state["connected"]!.GetValue<bool>());
            Assert.False((await spotify.Act("favorite", null, default)).Ok);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- P2: a state-keyed toggle shortcut run through the Control Deck always runs the "on" command
    [Fact]
    public async Task DeckRunsTheOffCommandOfAToggleWhosePushedStateIsOn()
    {
        var dir = TempDir("toggle");
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { shortcuts = new[] { new { id = "lamp", label = "Lamp", toggle = true, stateKey = "lamp",
                cmd = Path.Combine(dir, "lamp-on-missing.exe"), offCmd = Path.Combine(dir, "lamp-off-missing.exe") } } }));
            var config = new ConfigStore(path);
            var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(dir, "data.json"));
            Assert.Null(push.Set("lamp", JsonSerializer.SerializeToElement(true), null));

            // Reference behaviour (/api/run): the pushed state is on, so the off command is the one attempted.
            var reference = new Capture<DeckAuditTests>();
            Shortcuts.Run(config.Get(), "lamp", reference, push);
            Assert.Contains(reference.Lines, l => l.Contains("lamp-off-missing.exe"));

            var log = new Capture<ControlDeck>();
            using var deck = new ControlDeck(config, new Native(), log, push: push);
            await deck.Act("action", "shortcut:lamp", null, null, null, null, default);
            Assert.DoesNotContain(log.Lines, l => l.Contains("lamp-on-missing.exe"));
            Assert.Contains(log.Lines, l => l.Contains("lamp-off-missing.exe"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- P3: a task whose executable cannot be started is reported to the phone as started
    [Fact]
    public void ATaskThatCannotStartIsNotReportedAsStarted()
    {
        var dir = TempDir("task");
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { shortcuts = new[] { new { id = "build", cmd = Path.Combine(dir, "missing-build-tool.exe"), args = "", cwd = dir } } }));
            using var tasks = new ControlTasks(new ConfigStore(path), NullLogger.Instance);
            var result = tasks.Start(new() { Id = "run", Target = "build", Kind = "task" }, false);
            Assert.Equal("Failed to start", tasks.State("run"));
            Assert.False(result.Ok);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- P4: sleep-timer fade does not recognise packaged-app players and can pick an unrelated app
    [Theory]
    [InlineData("Spotify.exe", "Spotify", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify", true)]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Microsoft.Photos", false)]
    public void SleepFadeMatchesThePlayersOwnAudioSession(string player, string session, bool expected) =>
        Assert.Equal(expected, ControlDeck.PlayerSessionMatches(player, session));

    // ---- P5: a sleep timer cannot be cancelled once its player has closed, and still fires later
    [Fact]
    public async Task ASleepTimerCanBeCancelledAfterItsPlayerClosed()
    {
        var dir = TempDir("sleep");
        try
        {
            var path = Path.Combine(dir, "config.json"); File.WriteAllText(path, "{}");
            long now = 1000; var native = new Native();
            using var deck = new ControlDeck(new ConfigStore(path), native, NullLogger<ControlDeck>.Instance, () => now);
            Assert.True((await deck.Act("sleep", "Spotify.exe", null, 30, null, null, default)).Ok);
            var player = native.Players[0]; native.Players = [];           // the player is closed
            var cancel = await deck.Act("sleep", "Spotify.exe", null, 0, null, null, default);
            native.Players = [player];                                      // ... and reopened later
            now += 31 * 60_000; await deck.TickSleep(default);
            Assert.DoesNotContain("media:pause:Spotify.exe", native.Writes);
            Assert.True(cancel.Ok);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- P7: a folder shortcut with a trailing separator counts as a "running application"
    [Fact]
    public async Task AFolderShortcutIsNotTreatedAsARunningApplication()
    {
        var dir = TempDir("folder");
        try
        {
            var path = Path.Combine(dir, "config.json");
            var folder = Path.Combine(dir, "missing-project") + Path.DirectorySeparatorChar;   // nothing is opened: the folder does not exist
            File.WriteAllText(path, JsonSerializer.Serialize(new { shortcuts = new[] { new { id = "project", label = "Project", cmd = folder } } }));
            var native = new FocusNative();
            using var deck = new ControlDeck(new ConfigStore(path), native, NullLogger<ControlDeck>.Instance);
            var state = JsonSerializer.SerializeToElement(await deck.Snapshot(default));
            var result = await deck.Act("action", "shortcut:project", "focus", null, null, null, default);   // what the App Dock sends
            Assert.Empty(native.Focused);
            Assert.NotEqual("Running", state.GetProperty("shortcuts")[0].GetProperty("State").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }
    private sealed class FocusNative : IDeckNative
    {
        public readonly List<string> Focused = [];
        public DeckAudio Audio() => new(true, null, [], []);
        public bool Device(string id, int? volume, bool? muted, int? channel = null) => false;
        public bool Session(string id, int? volume, bool? muted) => false;
        public bool DefaultDevice(string id, int? role = null) => false;
        public IReadOnlyList<DeckMonitor> Monitors() => [];
        public bool Brightness(string id, int value) => false;
        public IReadOnlyList<DeckItem> PowerPlans() => [];
        public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) { if (kind == "focus") Focused.Add(target); return Task.FromResult(true); }
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>([]);
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) => Task.FromResult(false);
        public void Play(string id, string file, string? device, int volume) { }
        public void StopSound(string? id) { }
        public IReadOnlyList<DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public void Dispose() { }
    }

    // ---- P8: the OSC binding hint promises a float value, but a fractional value makes the whole settings save fail
}
