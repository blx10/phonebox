using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class RadioDirectoryTests
{
    [Fact]
    public async Task RadioDirectoryParsesM3uVerifiesLiveAndHlsStreamsAndRepairsDeadStations()
    {
        const string sampleM3u = """
            #EXTM3U
            #EXTINF:-1 tvg-logo="https://cdn.example/fenomen.png" group-title="Pop, Hit Music",Radyo Fenomen
            https://live.example/fenomen.mp3
            #EXTINF:-1 tvg-logo="https://cdn.example/shout.png" group-title="Turkish",Kral Pop
            http://46.20.3.201:80/listen.pls?sid=1
            #EXTINF:-1,Legacy Dead Playlist
            http://old.example/stream.asx
            """;

        var parsed = RadioDirectory.ParseM3u(sampleM3u);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("Radyo Fenomen", parsed[0].Name);
        Assert.Equal("https://cdn.example/fenomen.png", parsed[0].Logo);
        Assert.Equal("Pop, Hit Music", parsed[0].Group);
        Assert.Equal("Kral Pop", parsed[1].Name);
        Assert.Equal("http://46.20.3.201/", parsed[1].Url);

        var fakeAudio = Enumerable.Repeat((byte)0xFF, 256).ToArray();
        using var http = new HttpClient(new FakeHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/countries/tr.m3u", StringComparison.Ordinal))
                return TextResponse(req, """
                    #EXTM3U
                    #EXTINF:-1 group-title="pop",Kral Pop
                    https://good.example/kralpop/master.m3u8
                    """, "audio/x-mpegurl");
            if (url.EndsWith("/streams/tr/tr.m3u", StringComparison.Ordinal))
                return TextResponse(req, """
                    #EXTM3U
                    #EXTINF:-1 tvg-logo="https://img.example/kralpop.png" group-title="Pop",Kral Pop
                    https://good.example/kralpop/master.m3u8
                    """, "audio/x-mpegurl");
            if (url == "https://live.example/good.mp3")
                return BytesResponse(req, fakeAudio, "audio/mpeg");
            if (url == "https://live.example/fake200")
                return TextResponse(req, "<!DOCTYPE html><html><body>Stream offline</body></html>", "text/html");
            if (url == "https://dead.example/kralpop/playlist.m3u8")
                return TextResponse(req, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=64000\nchunklist.m3u8\n");
            if (url == "https://dead.example/kralpop/chunklist.m3u8")
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req };
            if (url == "https://good.example/kralpop/master.m3u8")
                return TextResponse(req, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=96000\nmedia.m3u8\n");
            if (url == "https://good.example/kralpop/media.m3u8")
                return TextResponse(req, "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\nseg0.aac\n");
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req };
        }));

        using var directory = new RadioDirectory(http);
        Assert.True(await directory.VerifyStreamAsync("https://live.example/good.mp3", default));
        Assert.False(await directory.VerifyStreamAsync("https://live.example/fake200", default));
        Assert.False(await directory.VerifyStreamAsync("https://dead.example/kralpop/playlist.m3u8", default));
        Assert.True(await directory.VerifyStreamAsync("https://good.example/kralpop/master.m3u8", default));

        var repaired = await directory.RepairStationsAsync(
        [
            new("r-good", "Fenomen", "https://live.example/good.mp3"),
            new("r-kral", "Kral Pop", "https://dead.example/kralpop/playlist.m3u8"),
            new("r-dead", "Unknown Station", "https://dead.example/missing.mp3"),
        ], "tr", default);

        Assert.True(repaired[0].Ok);
        Assert.False(repaired[1].Ok);
        Assert.Equal("https://good.example/kralpop/master.m3u8", repaired[1].ReplacementUrl);
        Assert.Equal("https://img.example/kralpop.png", repaired[1].ReplacementLogo);
        Assert.False(repaired[2].Ok);
        Assert.Null(repaired[2].ReplacementUrl);
    }

    [Fact]
    public async Task DisabledRadioStationsAreHiddenFromSnapshotAndStoppedImmediately()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phonebox-radio-enabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfgPath = Path.Combine(dir, "config.json");
            File.WriteAllText(cfgPath, """
                {
                  "controls": {
                    "actions": [
                      { "id": "r-1", "label": "Radio 1", "kind": "radio", "target": "https://example.com/1.mp3", "enabled": true },
                      { "id": "r-2", "label": "Passive Radio", "kind": "radio", "target": "https://example.com/2.mp3", "enabled": false },
                      { "id": "r-3", "label": "Radio 3", "kind": "radio", "target": "https://example.com/3.mp3", "enabled": true }
                    ]
                  }
                }
                """);

            var store = new ConfigStore(cfgPath);
            var native = new StatefulRadioNative();
            using var deck = new ControlDeck(store, native, NullLogger<ControlDeck>.Instance);

            using var snapDoc = JsonSerializer.SerializeToDocument(await deck.Snapshot(default));
            var ids = snapDoc.RootElement.GetProperty("actions").EnumerateArray().Select(e => e.GetProperty("Id").GetString()!).ToArray();
            Assert.Equal(["r-1", "r-3"], ids);

            Assert.True((await deck.Act("action", "r-3", "", null, null, null, default)).Ok);
            Assert.Contains("r-3", native.PlayingRadios);

            store.Get().Controls.Actions.First(a => a.Id == "r-3").Enabled = false;
            deck.StopDisabledRadios();
            Assert.Empty(native.PlayingRadios);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RadioBrowserAndNationalModeRankMostListenedStationsFirstAndFilterOutAmateurStreams()
    {
        Assert.Equal("🇹🇷", RadioDirectory.Iso2ToFlag("tr"));
        Assert.Equal("palfm", RadioDirectory.CanonicalStationKey("Pal FM 99.2 MHz"));
        Assert.Equal("number1", RadioDirectory.CanonicalStationKey("NR1 FM 102.4"));
        Assert.NotEqual(RadioDirectory.CanonicalStationKey("Power FM"), RadioDirectory.CanonicalStationKey("Power POP"));
        Assert.NotEqual(RadioDirectory.CanonicalStationKey("TRT FM"), RadioDirectory.CanonicalStationKey("TRT Radyo 1"));

        const string wikiJson = """
            {
              "parse": {
                "wikitext": {
                  "*": "== İstanbul ==\n{|\n|-\n|'''97.2 MHz'''\n|[[Metro FM]]\n|-\n|'''89.6 MHz'''\n|[[Kafa Radyo]]\n|}\n== Ankara ==\n{|\n|-\n|'''97.2 MHz'''\n|[[Metro FM]]\n|}\n== İzmir ==\n{|\n|-\n|'''97.2 MHz'''\n|[[Metro FM]]\n|}"
                }
              }
            }
            """;
        var wikiMap = RadioDirectory.ParseTurkishWikipediaStationListJson(wikiJson);
        Assert.Equal(3, wikiMap["metro"].Cities);
        Assert.True(wikiMap["metro"].HasWikiArticle);

        var fakeAudio = Enumerable.Repeat((byte)0xFF, 256).ToArray();
        using var http = new HttpClient(new FakeHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.radio-browser.info/json/stations/search", StringComparison.Ordinal))
            {
                return TextResponse(req, """
                    [
                      {"name": "ANKARA HAVALARI ZENO SUPER", "url_resolved": "https://stream.zeno.fm/amateur123", "votes": 15000, "clickcount": 900, "bitrate": 128, "lastcheckok": 1},
                      {"name": "JOY TÜRK TURKEY", "url_resolved": "https://live.example/joyturk.aac", "favicon": "https://img.example/joyturk.png", "votes": 3400, "clickcount": 320, "bitrate": 64, "lastcheckok": 1},
                      {"name": "KRAL FM", "url_resolved": "https://dead.example/kral.aac", "votes": 500, "clickcount": 180, "bitrate": 128, "lastcheckok": 1}
                    ]
                    """, "application/json");
            }
            if (url.Contains("tr.wikipedia.org/w/api.php", StringComparison.Ordinal))
                return TextResponse(req, wikiJson, "application/json");
            if (url.EndsWith("/countries/tr.m3u", StringComparison.Ordinal))
                return TextResponse(req, "#EXTM3U\n#EXTINF:-1,Kral FM\nhttps://live.example/kral-backup.aac\n", "audio/x-mpegurl");
            if (url.EndsWith("/streams/tr/tr.m3u", StringComparison.Ordinal))
                return TextResponse(req, "#EXTM3U\n#EXTINF:-1 tvg-logo=\"https://img.example/kralfm.png\",Kral FM\nhttps://live.example/kral-backup.aac\n", "audio/x-mpegurl");
            if (url is "https://stream.zeno.fm/amateur123" or "https://live.example/joyturk.aac" or "https://live.example/kral-backup.aac")
                return BytesResponse(req, fakeAudio, "audio/aac");
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req };
        }));

        using var dir = new RadioDirectory(http);

        var popular = await dir.GetStationsAsync("tr", limit: 10, verify: true, default, source: "popular");
        Assert.Equal(["Kral FM", "JoyTürk", "Ankara Havalari Zeno Super"], popular.Select(s => s.Name).ToArray());
        Assert.Equal("https://live.example/kral-backup.aac", popular[0].Url);
        Assert.Equal("https://img.example/kralfm.png", popular[0].Logo);

        var nationalOnly = await dir.GetStationsAsync("tr", limit: 10, verify: true, default, source: "national");
        Assert.Equal(["Kral FM", "JoyTürk"], nationalOnly.Select(s => s.Name).ToArray());
    }

    private static HttpResponseMessage TextResponse(HttpRequestMessage req, string body, string mediaType = "application/vnd.apple.mpegurl")
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new StringContent(body, Encoding.UTF8) };
        res.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return res;
    }

    private static HttpResponseMessage BytesResponse(HttpRequestMessage req, byte[] body, string mediaType)
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new ByteArrayContent(body) };
        res.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return res;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
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
