using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class RadioFocusTests
{
    [Fact]
    public async Task RadioPlaybackPausesActiveMediaStopsPreviousRadioAndYieldsWhenMediaResumes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-focus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-fenomen", "label": "Radyo Fenomen", "kind": "radio", "target": "https://live.radyofenomen.com/fenomen/128/icecast.audio", "value": 80 },
                      { "id": "r-kafa", "label": "Kafa Radyo", "kind": "radio", "target": "https://stream.radyotvonline.net/hls/play/kafaradyo.m3u8", "value": 75 }
                    ]
                  }
                }
                """);

            var native = new StatefulRadioNative { SpotifyPlaying = true };
            using var deck = new ControlDeck(new ConfigStore(cfgPath), native, NullLogger<ControlDeck>.Instance);

            // 1. Starting Fenomen while Spotify plays pauses Spotify and starts Fenomen.
            Assert.True((await deck.Act("action", "r-fenomen", "", null, null, null, default)).Ok);
            Assert.False(native.SpotifyPlaying);
            Assert.Contains("media:pause", native.Events);
            Assert.Equal(["r-fenomen"], native.PlayingRadios.OrderBy(x => x));

            // 2. Starting Kafa stops Fenomen and starts Kafa (never two radios playing together).
            Assert.True((await deck.Act("action", "r-kafa", "", null, null, null, default)).Ok);
            Assert.Equal(["r-kafa"], native.PlayingRadios.OrderBy(x => x));
            Assert.Contains("stop:r-fenomen", native.Events);

            // 3. External media resuming (Spotify paused -> playing) stops active radio on focus tick.
            native.SpotifyPlaying = true;
            await deck.TickRadioMediaFocus(default);
            Assert.Empty(native.PlayingRadios);

            // 4. Starting radio again, then triggering media playpause via ControlDeck stops radio immediately.
            native.SpotifyPlaying = false;
            Assert.True((await deck.Act("action", "r-fenomen", "", null, null, null, default)).Ok);
            Assert.Contains("r-fenomen", native.PlayingRadios);
            Assert.True((await deck.Act("media", "Spotify", "playpause", null, null, null, default)).Ok);
            Assert.True(native.SpotifyPlaying);
            Assert.Empty(native.PlayingRadios);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class StatefulRadioNative : IDeckNative
    {
        public readonly List<string> Events = [];
        public readonly HashSet<string> PlayingRadios = [];
        public bool SpotifyPlaying;

        public DeckAudio Audio() => new(true, null, [], []);
        public bool Device(string id, int? volume, bool? muted, int? channel = null) => true;
        public bool Session(string id, int? volume, bool? muted) => true;
        public bool DefaultDevice(string id, int? role = null) => true;
        public IReadOnlyList<DeckMonitor> Monitors() => [];
        public bool Brightness(string id, int value) => false;
        public IReadOnlyList<DeckItem> PowerPlans() => [];
        public Task<bool> PlatformAction(string kind, string target, string? argument, int value, CancellationToken ct) => Task.FromResult(false);
        public string? ActionState(DeckAction action) => action.Kind is "sound" or "radio"
            ? (PlayingRadios.Contains(action.Id) ? "Playing" : "Stopped")
            : null;
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>(
            [new("Spotify", "Spotify", "Song", SpotifyPlaying, 0, 180_000, true, false, false, false, "None")]);
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct)
        {
            Events.Add("media:" + action);
            if (action == "pause") SpotifyPlaying = false;
            else if (action == "playpause") SpotifyPlaying = !SpotifyPlaying;
            else if (action is "play" or "next" or "prev") SpotifyPlaying = true;
            return Task.FromResult(true);
        }
        public void Play(string id, string file, string? device, int volume)
        {
            Events.Add("play:" + id);
            PlayingRadios.Add(id);
        }
        public void StopSound(string? id)
        {
            Events.Add("stop:" + (id ?? "*"));
            if (id is null) PlayingRadios.Clear();
            else PlayingRadios.Remove(id);
        }
        public IReadOnlyList<DeckItem> MidiPorts() => [];
        public void Midi(int port, string message, int value) { }
        public async IAsyncEnumerable<DeckAudioPacket> Listen([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
        public void Dispose() => PlayingRadios.Clear();
    }
}
