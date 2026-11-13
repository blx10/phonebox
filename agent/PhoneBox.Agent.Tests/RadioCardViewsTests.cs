using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class RadioCardViewsTests
{
    [Fact]
    public async Task TapToToggleStartsAndStopsRadioWithoutSeparateStopAction()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-toggle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-1", "label": "Jazz FM", "kind": "radio", "target": "https://example.com/jazz.mp3", "value": 80 },
                      { "id": "r-2", "label": "Classic FM", "kind": "radio", "target": "https://example.com/classic.mp3", "value": 75 }
                    ]
                  }
                }
                """);

            var native = new StatefulRadioNative();
            using var deck = new ControlDeck(new ConfigStore(cfgPath), native, NullLogger<ControlDeck>.Instance);

            Assert.True((await deck.Act("action", "r-1", "toggle", null, null, null, default)).Ok);
            Assert.Equal(["r-1"], native.PlayingRadios.ToArray());

            // Empty op (e.g. from ApplyProfile) keeps playing instead of toggling off
            Assert.True((await deck.Act("action", "r-1", "", null, null, null, default)).Ok);
            Assert.Equal(["r-1"], native.PlayingRadios.ToArray());

            Assert.True((await deck.Act("action", "r-2", "toggle", null, null, null, default)).Ok);
            Assert.Equal(["r-2"], native.PlayingRadios.ToArray());

            Assert.True((await deck.Act("action", "r-2", "toggle", null, null, null, default)).Ok);
            Assert.Empty(native.PlayingRadios);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class StatefulRadioNative : IDeckNative
    {
        public readonly HashSet<string> PlayingRadios = [];
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
        public Task<IReadOnlyList<DeckMedia>> Media(CancellationToken ct) => Task.FromResult<IReadOnlyList<DeckMedia>>([]);
        public Task<bool> MediaAction(string id, string action, long value, CancellationToken ct) => Task.FromResult(true);
        public void Play(string id, string file, string? device, int volume) => PlayingRadios.Add(id);
        public void StopSound(string? id)
        {
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
