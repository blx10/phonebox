using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed record GitHubRadioCountry(string Code, string Name, string Flag, int Count);
public sealed record GitHubRadioStation(string Name, string Url, string? Logo, string? Group);
public sealed record RadioRepairInput(string Id, string Label, string Target);
public sealed record RadioRepairItem(string Id, string Label, bool Ok, string? ReplacementUrl, string? ReplacementLogo);

internal sealed record RadioBrowserEntry(
    string Name,
    string Url,
    string? Favicon,
    string? Tags,
    int Votes,
    int ClickCount,
    int Bitrate,
    bool LastCheckOk);

internal sealed class RadioDirectory : IDisposable
{
    private const string IndexUrl = "https://raw.githubusercontent.com/AlonDrilich/radio-playlists/main/index.json";
    private const string PlaylistsBaseUrl = "https://raw.githubusercontent.com/AlonDrilich/radio-playlists/main/countries/";
    private const string IprdBaseUrl = "https://raw.githubusercontent.com/iprd-org/iprd/main/streams/";
    private const string TrWikiListApiUrl = "https://tr.wikipedia.org/w/api.php?action=parse&page=T%C3%BCrkiye%27deki_radyo_istasyonlar%C4%B1_listesi&prop=wikitext&format=json";
    private const string EnWikiCategoryApiBase = "https://en.wikipedia.org/w/api.php?action=query&list=categorymembers&cmtype=page&cmlimit=200&format=json&cmtitle=";
    private const long CacheTtlMs = 3600_000L;

    private static readonly string[] RadioBrowserMirrors =
    [
        "https://de1.api.radio-browser.info",
        "https://fi1.api.radio-browser.info",
        "https://at1.api.radio-browser.info",
    ];

    private static readonly string[] AmateurStreamHosts =
    [
        "zeno.fm",
        "caster.fm",
        "listen2myradio",
        "myradiostream",
        "radionomy.com",
        "laut.fm",
        "radioking.com",
    ];

    // Official RTÜK R1 National Terrestrial Radio Licensees + TRT/Public National Channels,
    // ordered by RİAK (Radyo İzleme ve Araştırma Kurulu) national audience reach & prominence.
    internal static readonly (string CanonicalName, string Group, string[] Aliases)[] TurkeyNationalStations =
    [
        ("Kral FM", "Ulusal · Arabesk / Fantezi", ["kralfm"]),
        ("TRT FM", "Kamu Ulusal · Türkçe Pop", ["trtfm", "trtradyo2"]),
        ("Kral Pop", "Ulusal · Türkçe Pop", ["kralpop", "kralpopradyo"]),
        ("Süper FM", "Ulusal · Türkçe Pop", ["superfm", "super"]),
        ("PowerTürk", "Ulusal · Türkçe Pop", ["powerturk", "powerturkfm"]),
        ("Metro FM", "Ulusal · Yabancı Hit", ["metrofm", "metro"]),
        ("Slow Türk", "Ulusal · Türkçe Slow", ["slowturk"]),
        ("Radyo Fenomen", "Ulusal · Yabancı Hit", ["fenomen", "radyofenomen"]),
        ("Kafa Radyo", "Ulusal · Konuşan Radyo / Pop", ["kafaradyo", "kafa"]),
        ("Radyo 7", "Ulusal · Türkçe Müzik", ["radyo7"]),
        ("Alem FM", "Ulusal · Türkçe Pop", ["alemfm", "alem"]),
        ("Best FM", "Ulusal · Türkçe Pop", ["bestfm"]),
        ("Radyo D", "Ulusal · Türkçe Pop", ["radyod"]),
        ("Virgin Radio Türkiye", "Ulusal · Yabancı Hit", ["virgin", "virginradio"]),
        ("Power FM", "Ulusal · Yabancı Hit", ["powerfm", "power100"]),
        ("JoyTürk", "Ulusal · Türkçe Akustik / Slow", ["joyturk"]),
        ("Joy FM", "Ulusal · Easy Listening", ["joyfm"]),
        ("Show Radyo", "Ulusal · Türkçe Pop", ["showradyo", "show"]),
        ("Radyo Viva", "Ulusal · Türkçe Pop", ["radyoviva", "viva"]),
        ("Pal Station", "Ulusal · Yabancı Hit", ["palstation", "palstation106"]),
        ("Number1 FM", "Ulusal · Yabancı Hit", ["number1", "number1fm", "nr1", "nr1fm"]),
        ("Number1 Türk", "Ulusal · Türkçe Pop", ["number1turk", "nr1turk"]),
        ("Fenomen Türk", "Ulusal · Türkçe Hit", ["fenomenturk"]),
        ("Pal Nostalji", "Ulusal · Nostalji", ["palnostalji"]),
        ("Pal FM", "Ulusal · Türkçe Müzik", ["palfm"]),
        ("Radyo 45'lik", "Ulusal · Nostalji", ["radyo45lik", "45lik"]),
        ("TRT Radyo 1", "Kamu Ulusal · Haber / Kültür", ["trtradyo1", "trt1"]),
        ("TRT Nağme", "Kamu Ulusal · Türk Sanat Müziği", ["trtnagme"]),
        ("TRT Türkü", "Kamu Ulusal · Türk Halk Müziği", ["trtturku"]),
        ("TRT Radyo 3", "Kamu Ulusal · Klasik / Caz", ["trtradyo3", "trt3"]),
        ("TRT Radyo Haber", "Kamu Ulusal · Haber", ["trtradyohaber", "trthaber"]),
        ("NTV Radyo", "Ulusal · Haber", ["ntvradyo", "ntv"]),
        ("CNN Türk Radyo", "Ulusal · Haber", ["cnnturk", "cnnturkradyo"]),
        ("Habertürk Radyo", "Ulusal · Haber", ["haberturk", "haberturkradyo"]),
        ("A Haber Radyo", "Ulusal · Haber", ["ahaber", "ahaberradyo"]),
        ("Bloomberg HT Radyo", "Ulusal · Ekonomi / Müzik", ["bloomberght", "bloomberghtradyo"]),
        ("24 Radyo", "Ulusal · Haber", ["24radyo", "yirmidortradyo"]),
        ("Radyo Spor", "Ulusal · Spor", ["radyospor"]),
        ("A Spor Radyo", "Ulusal · Spor", ["aspor", "asporradyo"]),
        ("Lig Radyo", "Ulusal · Spor", ["ligradyo"]),
        ("Radyo Eksen", "Ulusal · Rock / Alternatif", ["radyoeksen", "eksen"]),
        ("Radyo Voyage", "Ulusal · New Age / Dünya", ["voyage", "radyovoyage"]),
        ("Radyo Alaturka", "Ulusal · Türk Sanat Müziği", ["alaturka", "radyoalaturka"]),
        ("Baba Radyo", "Ulusal · Arabesk / Halk", ["babaradyo"]),
        ("Radyo Seymen", "Ulusal · Oyun Havaları / Halk", ["seymen", "radyoseymen"]),
        ("Radyo Turkuvaz", "Ulusal · Türkçe Pop", ["turkuvaz", "radyoturkuvaz"]),
        ("Radyo Trafik", "Ulusal · Trafik / Haber", ["radyotrafik"]),
        ("TGRT FM", "Ulusal · Haber / Kültür", ["tgrtfm", "tgrt"]),
        ("Türkiye Polis Radyosu", "Kamu Ulusal · Kültür / Müzik", ["polisradyosu", "turkiyepolisradyosu"]),
        ("Meteorolojinin Sesi Radyosu", "Kamu Ulusal · Hava / Müzik", ["meteorolojininsesiradyosu", "meteorolojininsesi"]),
        ("Diyanet Radyo", "Kamu Ulusal · Dinî Yayın", ["diyanet", "diyanetradyo"]),
        ("Diyanet Kur'an Radyo", "Kamu Ulusal · Dinî Yayın", ["diyanetkuran", "diyanetkuranradyo"]),
        ("İstanbul FM", "Ulusal · Türkçe Pop", ["istanbulfm", "istanbul"]),
        ("Radyo ODTÜ", "Üniversite / Alternatif", ["radyoodtu", "odtu"]),
        ("Semerkand Radyo", "Ulusal · Tematik", ["semerkand", "semerkandradyo"]),
        ("Akra FM", "Ulusal · Tematik", ["akrafm", "akra"]),
        ("Vav Radyo", "Ulusal · Tematik", ["vavradyo"]),
        ("Erkam Radyo", "Ulusal · Tematik", ["erkamradyo", "erkam"]),
    ];

    private static readonly GitHubRadioCountry[] FallbackCountries =
    [
        new("tr", "Türkiye", "🇹🇷", 550),
        new("us", "United States", "🇺🇸", 6500),
        new("gb", "United Kingdom", "🇬🇧", 2100),
        new("de", "Germany", "🇩🇪", 4800),
        new("fr", "France", "🇫🇷", 2400),
        new("nl", "Netherlands", "🇳🇱", 950),
        new("it", "Italy", "🇮🇹", 2100),
        new("es", "Spain", "🇪🇸", 1800),
        new("az", "Azerbaijan", "🇦🇿", 60),
        new("gr", "Greece", "🇬🇷", 950),
        new("ch", "Switzerland", "🇨🇭", 650),
        new("at", "Austria", "🇦🇹", 450),
        new("be", "Belgium", "🇧🇪", 450),
        new("se", "Sweden", "🇸🇪", 320),
        new("no", "Norway", "🇳🇴", 260),
        new("pl", "Poland", "🇵🇱", 900),
        new("ru", "Russia", "🇷🇺", 1500),
        new("jp", "Japan", "🇯🇵", 320),
        new("kr", "South Korea", "🇰🇷", 120),
        new("ca", "Canada", "🇨🇦", 1400),
        new("au", "Australia", "🇦🇺", 750),
        new("br", "Brazil", "🇧🇷", 2600),
    ];

    private sealed record StationCandidate(
        string Name,
        IReadOnlyList<string> Urls,
        string? Logo,
        string? Group,
        bool IsNational,
        double Score);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private (IReadOnlyList<GitHubRadioCountry> Items, long ExpiresAt)? _countriesCache;
    private readonly ConcurrentDictionary<string, (IReadOnlyList<StationCandidate> Items, long ExpiresAt)> _catalogCache = new();
    private readonly ConcurrentDictionary<string, (Dictionary<string, (string DisplayName, int Cities, bool HasWikiArticle)> Map, long ExpiresAt)> _wikiCache = new();

    public RadioDirectory(HttpClient? http = null)
    {
        if (http is not null)
        {
            _http = http;
            _ownsHttp = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(4),
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBox/" + AppPaths.Version);
            _ownsHttp = true;
        }
    }

    public async Task<IReadOnlyList<GitHubRadioCountry>> GetCountriesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_countriesCache is { } cached && cached.ExpiresAt > now)
            return cached.Items;

        var m3uIndexTask = FetchM3uCountriesAsync(ct);
        var rbCountriesTask = FetchRadioBrowserCountriesAsync(ct);
        await Task.WhenAll(m3uIndexTask, rbCountriesTask);

        var merged = new Dictionary<string, GitHubRadioCountry>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in m3uIndexTask.Result)
            merged[c.Code] = c;

        foreach (var (code, count) in rbCountriesTask.Result)
        {
            if (merged.TryGetValue(code, out var existing))
            {
                merged[code] = existing with { Count = Math.Max(existing.Count, count) };
            }
            else
            {
                merged[code] = new GitHubRadioCountry(code, code.ToUpperInvariant(), Iso2ToFlag(code), count);
            }
        }

        if (merged.Count > 0)
        {
            var ordered = merged.Values
                .OrderByDescending(c => c.Code == "tr")
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _countriesCache = (ordered, now + CacheTtlMs);
            return ordered;
        }

        return FallbackCountries;
    }

    public async Task<IReadOnlyList<GitHubRadioStation>> GetStationsAsync(
        string countryCode, int limit, bool verify, CancellationToken ct, string? source = "popular")
    {
        var code = (countryCode ?? "").Trim().ToLowerInvariant();
        if (!IsValidCountryCode(code))
            throw new ArgumentException("Invalid ISO country code", nameof(countryCode));

        var mode = NormalizeMode(source);
        var cap = limit <= 0 ? 200 : Math.Clamp(limit, 1, 250);
        var allCandidates = await LoadCountryCatalogAsync(code, mode, ct);
        var filtered = mode == "national"
            ? allCandidates.Where(c => c.IsNational).ToArray()
            : allCandidates;

        if (!verify)
        {
            return filtered
                .Where(c => c.Urls.Count > 0)
                .Take(cap)
                .Select(c => new GitHubRadioStation(c.Name, c.Urls[0], c.Logo, c.Group))
                .ToArray();
        }

        var pool = filtered.Take(Math.Min(filtered.Count, Math.Max(cap * 2, 65))).ToArray();
        var resolvedUrls = new string?[pool.Length];
        using var gate = new SemaphoreSlim(12, 12);
        await Task.WhenAll(pool.Select(async (cand, idx) =>
        {
            await gate.WaitAsync(ct);
            try { resolvedUrls[idx] = await VerifyCandidateUrlsAsync(cand.Urls, ct); }
            finally { gate.Release(); }
        }));

        var verified = new List<GitHubRadioStation>(cap);
        var seenFinalUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pool.Length && verified.Count < cap; i++)
        {
            if (resolvedUrls[i] is { } liveUrl && seenFinalUrls.Add(liveUrl))
                verified.Add(new GitHubRadioStation(pool[i].Name, liveUrl, pool[i].Logo, pool[i].Group));
        }
        return verified;
    }

    public async Task<IReadOnlyList<RadioRepairItem>> RepairStationsAsync(IReadOnlyList<RadioRepairInput> items, string fallbackCountry, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        var rawCountry = (fallbackCountry ?? "").Trim().ToLowerInvariant();
        var code = IsValidCountryCode(rawCountry) ? rawCountry : "tr";

        var okFlags = new bool[items.Count];
        using (var gate = new SemaphoreSlim(10, 10))
        {
            await Task.WhenAll(items.Select(async (item, idx) =>
            {
                await gate.WaitAsync(ct);
                try { okFlags[idx] = await VerifyStreamAsync(item.Target, ct); }
                finally { gate.Release(); }
            }));
        }

        IReadOnlyList<StationCandidate> catalog = [];
        if (okFlags.Any(ok => !ok))
        {
            try { catalog = await LoadCountryCatalogAsync(code, "popular", ct); }
            catch { catalog = []; }
        }

        var results = new RadioRepairItem[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (okFlags[i])
            {
                results[i] = new RadioRepairItem(item.Id, item.Label, true, null, null);
                continue;
            }

            var normLabel = CanonicalStationKey(item.Label);
            string? replacementUrl = null;
            string? replacementLogo = null;
            if (normLabel.Length >= 2 && catalog.Count > 0)
            {
                var matches = catalog
                    .Select(s => (Station: s, Key: CanonicalStationKey(s.Name)))
                    .Where(x => x.Key.Length >= 2 && (x.Key == normLabel || x.Key.Contains(normLabel, StringComparison.Ordinal) || normLabel.Contains(x.Key, StringComparison.Ordinal)))
                    .OrderByDescending(x => x.Key == normLabel ? 2 : 1)
                    .ThenByDescending(x => x.Station.Score)
                    .Take(5)
                    .Select(x => x.Station);

                foreach (var candidate in matches)
                {
                    foreach (var url in candidate.Urls.Take(4))
                    {
                        if (string.Equals(url, item.Target, StringComparison.OrdinalIgnoreCase)) continue;
                        if (await VerifyStreamAsync(url, ct))
                        {
                            replacementUrl = url;
                            replacementLogo = candidate.Logo;
                            break;
                        }
                    }
                    if (replacementUrl is not null) break;
                }
            }
            results[i] = new RadioRepairItem(item.Id, item.Label, false, replacementUrl, replacementLogo);
        }
        return results;
    }

    internal static string NormalizeMode(string? source) => (source ?? "popular").Trim().ToLowerInvariant() switch
    {
        "national" or "ulusal" or "r1" => "national",
        "radiobrowser" or "raw" or "clicks" => "radiobrowser",
        _ => "popular",
    };

    private async Task<IReadOnlyList<StationCandidate>> LoadCountryCatalogAsync(string code, string mode, CancellationToken ct)
    {
        var cacheKey = $"{code}:{mode}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_catalogCache.TryGetValue(cacheKey, out var hit) && hit.ExpiresAt > now)
            return hit.Items;

        var countryName = (_countriesCache?.Items ?? FallbackCountries)
            .FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase))?.Name ?? code.ToUpperInvariant();

        var rbClicksTask = FetchRadioBrowserStationsAsync(code, "clickcount", 350, ct);
        var rbVotesTask = mode == "radiobrowser"
            ? Task.FromResult<IReadOnlyList<RadioBrowserEntry>>([])
            : FetchRadioBrowserStationsAsync(code, "votes", 350, ct);
        var primaryM3uTask = FetchTextSafeAsync($"{PlaylistsBaseUrl}{code}.m3u", ct);
        var iprdM3uTask = FetchTextSafeAsync($"{IprdBaseUrl}{code}/{code}.m3u", ct);
        var wikiTask = mode == "radiobrowser"
            ? Task.FromResult(new Dictionary<string, (string DisplayName, int Cities, bool HasWikiArticle)>(StringComparer.Ordinal))
            : FetchWikipediaNotableStationsAsync(code, countryName, ct);

        await Task.WhenAll(rbClicksTask, rbVotesTask, primaryM3uTask, iprdM3uTask, wikiTask);

        var rbClicks = rbClicksTask.Result;
        var rbVotes = rbVotesTask.Result;
        var primaryM3u = ParseM3u(primaryM3uTask.Result);
        var iprdM3u = ParseM3u(iprdM3uTask.Result);
        var wikiMap = wikiTask.Result;

        // Build alias lookup for curated national stations (TR RTÜK R1 + TRT/Public).
        var curatedByKey = new Dictionary<string, (int Rank, string CanonicalName, string Group)>(StringComparer.Ordinal);
        if (code == "tr")
        {
            for (var i = 0; i < TurkeyNationalStations.Length; i++)
            {
                var entry = TurkeyNationalStations[i];
                curatedByKey[CanonicalStationKey(entry.CanonicalName)] = (i, entry.CanonicalName, entry.Group);
                foreach (var alias in entry.Aliases)
                    curatedByKey.TryAdd(alias, (i, entry.CanonicalName, entry.Group));
            }
        }

        // Aggregate all open-source streams grouped by canonical station key.
        var buckets = new Dictionary<string, MutableStationBucket>(StringComparer.Ordinal);
        var insertionSeq = 0;

        MutableStationBucket GetBucket(string rawName)
        {
            var key = CanonicalStationKey(rawName);
            if (!buckets.TryGetValue(key, out var b))
            {
                b = new MutableStationBucket(key, rawName.Trim(), insertionSeq++);
                buckets[key] = b;
            }
            return b;
        }

        foreach (var rb in rbClicks.Concat(rbVotes))
        {
            if (string.IsNullOrWhiteSpace(rb.Name) || string.IsNullOrWhiteSpace(rb.Url)) continue;
            var b = GetBucket(rb.Name);
            b.AddStream(rb.Url, rb.Bitrate, isHttps: rb.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            b.MaxVotes = Math.Max(b.MaxVotes, rb.Votes);
            b.MaxClicks = Math.Max(b.MaxClicks, rb.ClickCount);
            b.Logo ??= SanitizeLogo(rb.Favicon);
            if (!string.IsNullOrWhiteSpace(rb.Tags) && b.Group is null)
                b.Group = rb.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        }

        foreach (var st in iprdM3u)
        {
            var b = GetBucket(st.Name);
            b.InIprd = true;
            b.AddStream(st.Url, 128, st.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            b.Logo = SanitizeLogo(st.Logo) ?? b.Logo;
            b.Group ??= st.Group;
        }

        foreach (var st in primaryM3u)
        {
            var b = GetBucket(st.Name);
            b.AddStream(st.Url, 128, st.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            b.Logo ??= SanitizeLogo(st.Logo);
            b.Group ??= st.Group;
        }

        var candidates = new List<StationCandidate>(buckets.Count);
        foreach (var b in buckets.Values)
        {
            if (b.Key.Length == 0 || b.Streams.Count == 0) continue;

            var orderedUrls = b.Streams
                .OrderByDescending(s => !IsAmateurStreamHost(s.Url))
                .ThenByDescending(s => s.IsHttps)
                .ThenByDescending(s => Math.Min(s.Bitrate, 320))
                .Select(s => s.Url)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            curatedByKey.TryGetValue(b.Key, out var curated);
            var isCurated = curated.CanonicalName is not null;
            wikiMap.TryGetValue(b.Key, out var wiki);
            var hasWiki = wiki.DisplayName is not null;

            var displayName = isCurated
                ? curated.CanonicalName!
                : hasWiki
                    ? wiki.DisplayName!
                    : CleanDisplayName(b.RawName);

            var group = isCurated ? curated.Group : b.Group;
            var logo = b.Logo;

            // National classification:
            // - In TR: RTÜK R1 / TRT curated list OR >= 3 major cities on Wikipedia terrestrial FM tables with Wikipedia article.
            // - In other countries: Wikipedia radio station article OR public broadcaster prefix.
            var isNational = code == "tr"
                ? (isCurated || (hasWiki && wiki.Cities >= 3 && wiki.HasWikiArticle))
                : (hasWiki || IsKnownPublicBroadcaster(code, displayName));

            double score;
            if (mode == "radiobrowser")
            {
                score = b.MaxClicks * 10.0 + Math.Log10(1 + b.MaxVotes) * 5.0 - b.Order * 0.001;
            }
            else
            {
                var popularitySignal = Math.Log10(1 + b.MaxVotes) * 22.0 + Math.Log10(1 + b.MaxClicks) * 26.0;
                var tierBonus = isCurated
                    ? 10_000.0 - curated.Rank * 25.0
                    : (hasWiki && (wiki.HasWikiArticle || wiki.Cities >= 2))
                        ? 3_000.0 + wiki.Cities * 120.0 + (wiki.HasWikiArticle ? 400.0 : 0.0)
                        : IsKnownPublicBroadcaster(code, displayName)
                            ? 3_500.0
                            : 0.0;
                var iprdBonus = b.InIprd ? 35.0 : 0.0;
                var logoBonus = logo is not null ? 12.0 : 0.0;
                var spamPenalty = (!isNational && IsSpammyOrAmateur(b.RawName, orderedUrls[0])) ? 250.0 : 0.0;
                score = tierBonus + popularitySignal + iprdBonus + logoBonus - spamPenalty;
            }

            candidates.Add(new StationCandidate(displayName, orderedUrls, logo, group, isNational, score));
        }

        var sorted = candidates
            .GroupBy(c => CanonicalStationKey(c.Name), StringComparer.Ordinal)
            .Select(g =>
            {
                var best = g.OrderByDescending(x => x.Score).First();
                var mergedUrls = g.OrderByDescending(x => x.Score)
                    .SelectMany(x => x.Urls)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var mergedLogo = g.Select(x => x.Logo).FirstOrDefault(l => l is not null);
                return best with { Urls = mergedUrls, Logo = mergedLogo, IsNational = g.Any(x => x.IsNational) };
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sorted.Length > 0)
            _catalogCache[cacheKey] = (sorted, now + CacheTtlMs);
        return sorted;
    }

    private sealed class MutableStationBucket(string key, string rawName, int order)
    {
        public string Key { get; } = key;
        public string RawName { get; } = rawName;
        public int Order { get; } = order;
        public int MaxVotes { get; set; }
        public int MaxClicks { get; set; }
        public bool InIprd { get; set; }
        public string? Logo { get; set; }
        public string? Group { get; set; }
        public List<(string Url, int Bitrate, bool IsHttps)> Streams { get; } = [];

        public void AddStream(string url, int bitrate, bool isHttps)
        {
            if (Streams.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase))) return;
            Streams.Add((url, bitrate, isHttps));
        }
    }

    internal static string CanonicalStationKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().ToLowerInvariant();
        s = s.Replace("ç", "c").Replace("ğ", "g").Replace("ı", "i").Replace("i\u0307", "i")
             .Replace("ö", "o").Replace("ş", "s").Replace("ü", "u").Replace("â", "a").Replace("î", "i");
        s = Regex.Replace(s, @"\((?:yedek|backup|turkey|turkiye|radio\s*station|radyo|aac|mp3|hls|hd|hq|[^)]{0,12}mhz)\)", " ", RegexOptions.CultureInvariant);
        // Strip FM frequencies like 97.2, 102.4, 89,6 and bitrate tags like 128k, 64kbps.
        s = Regex.Replace(s, @"\b(?:8[7-9]|9\d|10[0-8])[.,]\d\b", " ", RegexOptions.CultureInvariant);
        s = Regex.Replace(s, @"\b\d{2,3}\s*k(?:bps)?\b", " ", RegexOptions.CultureInvariant);
        s = Regex.Replace(s, @"\b(?:number\s*one|nr\s*1)\b", "number1", RegexOptions.CultureInvariant);
        s = Regex.Replace(s, @"\b(?:turkiye|turkey|canli|live|stream|online|mhz|hd|hq|aac|mp3)\b", " ", RegexOptions.CultureInvariant);

        var tokens = Regex.Matches(s, @"[a-z0-9]+", RegexOptions.CultureInvariant)
            .Select(m => m.Value)
            .ToList();

        while (tokens.Count > 1 && tokens[^1] is "fm" or "radyo" or "radio" && tokens.Take(tokens.Count - 1).Sum(t => t.Length) >= 5)
            tokens.RemoveAt(tokens.Count - 1);
        while (tokens.Count > 1 && tokens[0] is "radyo" or "radio" && tokens.Skip(1).Sum(t => t.Length) >= 6)
            tokens.RemoveAt(0);

        return string.Concat(tokens);
    }

    private static string CleanDisplayName(string raw)
    {
        var s = Regex.Replace(raw.Trim(), @"\s+", " ");
        // Fix shouty ALL-CAPS titles from crowd-sourced directories when > 5 letters.
        var letters = s.Where(char.IsLetter).ToArray();
        if (letters.Length > 5 && letters.All(char.IsUpper) && !s.StartsWith("TRT ", StringComparison.Ordinal) && !s.StartsWith("BBC ", StringComparison.Ordinal) && !s.StartsWith("NTV ", StringComparison.Ordinal) && !s.StartsWith("CNN ", StringComparison.Ordinal))
        {
            s = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
            s = Regex.Replace(s, @"\bFm\b", "FM");
            s = Regex.Replace(s, @"\bTrt\b", "TRT");
        }
        return s;
    }

    private static bool IsAmateurStreamHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        return AmateurStreamHosts.Any(h => host.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSpammyOrAmateur(string rawName, string primaryUrl)
    {
        if (IsAmateurStreamHost(primaryUrl)) return true;
        if (rawName.Length > 42) return true;
        var letters = rawName.Where(char.IsLetter).ToArray();
        if (letters.Length >= 10 && letters.All(char.IsUpper)) return true;
        return false;
    }

    private static bool IsKnownPublicBroadcaster(string isoCode, string name) => isoCode switch
    {
        "gb" => name.StartsWith("BBC ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Classic FM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Absolute Radio", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Capital ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Heart ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LBC", StringComparison.OrdinalIgnoreCase),
        "de" => name.StartsWith("Deutschlandfunk", StringComparison.OrdinalIgnoreCase) || name.StartsWith("1LIVE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WDR ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SWR", StringComparison.OrdinalIgnoreCase) || name.StartsWith("NDR ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Bayern ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("BR", StringComparison.OrdinalIgnoreCase) || name.StartsWith("hr", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MDR ", StringComparison.OrdinalIgnoreCase),
        "fr" => name.StartsWith("France Inter", StringComparison.OrdinalIgnoreCase) || name.StartsWith("France Info", StringComparison.OrdinalIgnoreCase) || name.StartsWith("France Culture", StringComparison.OrdinalIgnoreCase) || name.StartsWith("France Musique", StringComparison.OrdinalIgnoreCase) || name.StartsWith("FIP", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RTL", StringComparison.OrdinalIgnoreCase) || name.StartsWith("NRJ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Europe ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Nostalgie", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Skyrock", StringComparison.OrdinalIgnoreCase),
        "us" => name.StartsWith("NPR", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WNYC", StringComparison.OrdinalIgnoreCase) || name.StartsWith("KEXP", StringComparison.OrdinalIgnoreCase) || name.StartsWith("KQED", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WBEZ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("KCRW", StringComparison.OrdinalIgnoreCase),
        "nl" => name.StartsWith("NPO ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Radio 538", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Qmusic", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Sky Radio", StringComparison.OrdinalIgnoreCase) || name.StartsWith("100% NL", StringComparison.OrdinalIgnoreCase),
        "it" => name.StartsWith("Rai Radio", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RTL 102.5", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RDS", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Radio Deejay", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Radio Italia", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Radio 24", StringComparison.OrdinalIgnoreCase),
        "es" => name.StartsWith("RNE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Radio Nacional", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LOS40", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Cadena SER", StringComparison.OrdinalIgnoreCase) || name.StartsWith("COPE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Onda Cero", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private async Task<Dictionary<string, (string DisplayName, int Cities, bool HasWikiArticle)>> FetchWikipediaNotableStationsAsync(
        string isoCode, string countryName, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_wikiCache.TryGetValue(isoCode, out var cached) && cached.ExpiresAt > now)
            return cached.Map;

        Dictionary<string, (string DisplayName, int Cities, bool HasWikiArticle)> map;
        if (isoCode == "tr")
        {
            var json = await FetchTextSafeAsync(TrWikiListApiUrl, ct);
            map = ParseTurkishWikipediaStationListJson(json);
        }
        else
        {
            var slug = countryName.Trim().Replace(' ', '_');
            var primaryCatJson = await FetchTextSafeAsync($"{EnWikiCategoryApiBase}{Uri.EscapeDataString("Category:Radio_stations_in_" + slug)}", ct);
            map = ParseEnglishWikipediaCategoryJson(primaryCatJson);
            if (map.Count == 0 && !slug.StartsWith("the_", StringComparison.OrdinalIgnoreCase))
            {
                var altCatJson = await FetchTextSafeAsync($"{EnWikiCategoryApiBase}{Uri.EscapeDataString("Category:Radio_stations_in_the_" + slug)}", ct);
                map = ParseEnglishWikipediaCategoryJson(altCatJson);
            }
        }

        if (map.Count > 0)
            _wikiCache[isoCode] = (map, now + CacheTtlMs);
        return map;
    }

    internal static Dictionary<string, (string DisplayName, int Cities, bool HasWikiArticle)> ParseTurkishWikipediaStationListJson(string? json)
    {
        var result = new Dictionary<string, (string DisplayName, int Cities, bool HasWikiArticle)>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("parse", out var parse)
                || !parse.TryGetProperty("wikitext", out var wtElem)
                || !wtElem.TryGetProperty("*", out var star))
                return result;

            var wikitext = star.GetString() ?? "";
            var sections = Regex.Split(wikitext, @"^==\s*([^=]+?)\s*==", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            var citySets = new Dictionary<string, (string Name, HashSet<string> Cities, bool Linked)>(StringComparer.Ordinal);

            for (var i = 1; i + 1 < sections.Length; i += 2)
            {
                var city = sections[i].Trim();
                if (city is "Kaynakça" or "Dış bağlantılar" or "Ayrıca bakınız") continue;
                foreach (var row in sections[i + 1].Split("|-"))
                {
                    var cellLine = row.Split('\n')
                        .Select(l => l.Trim())
                        .FirstOrDefault(l => l.StartsWith('|') && !l.StartsWith("|}", StringComparison.Ordinal) && !l.Contains("MHz", StringComparison.OrdinalIgnoreCase));
                    if (cellLine is null) continue;

                    var rawCell = cellLine.TrimStart('|').Trim();
                    var linkMatch = Regex.Match(rawCell, @"\[\[(?:[^|\]]+\|)?([^\]]+)\]\]", RegexOptions.CultureInvariant);
                    var hasWiki = linkMatch.Success;
                    var clean = hasWiki ? linkMatch.Groups[1].Value.Trim() : rawCell.Replace("'", "").Trim();
                    if (clean.Length is 0 or > 45 || clean.Contains("Frekans", StringComparison.OrdinalIgnoreCase)) continue;

                    var key = CanonicalStationKey(clean);
                    if (key.Length == 0) continue;
                    if (!citySets.TryGetValue(key, out var entry))
                        entry = (clean, new HashSet<string>(StringComparer.OrdinalIgnoreCase), hasWiki);
                    entry.Cities.Add(city);
                    citySets[key] = (entry.Name, entry.Cities, entry.Linked || hasWiki);
                }
            }

            foreach (var (key, val) in citySets)
                result[key] = (val.Name, val.Cities.Count, val.Linked);
        }
        catch { }

        return result;
    }

    internal static Dictionary<string, (string DisplayName, int Cities, bool HasWikiArticle)> ParseEnglishWikipediaCategoryJson(string? json)
    {
        var result = new Dictionary<string, (string DisplayName, int Cities, bool HasWikiArticle)>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("query", out var q)
                || !q.TryGetProperty("categorymembers", out var members)
                || members.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var m in members.EnumerateArray())
            {
                var title = m.TryGetProperty("title", out var t) ? (t.GetString() ?? "").Trim() : "";
                if (title.Length == 0
                    || title.StartsWith("List of ", StringComparison.OrdinalIgnoreCase)
                    || title.StartsWith("Category:", StringComparison.OrdinalIgnoreCase)
                    || title.StartsWith("Template:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var cleanTitle = Regex.Replace(title, @"\s*\([^)]*\)\s*$", "", RegexOptions.CultureInvariant).Trim();
                var key = CanonicalStationKey(cleanTitle);
                if (key.Length >= 2)
                    result.TryAdd(key, (cleanTitle, 3, true));
            }
        }
        catch { }

        return result;
    }

    private async Task<IReadOnlyList<RadioBrowserEntry>> FetchRadioBrowserStationsAsync(
        string isoCode, string order, int limit, CancellationToken ct)
    {
        var cc = isoCode.ToUpperInvariant();
        var query = $"/json/stations/search?countrycode={Uri.EscapeDataString(cc)}&hidebroken=true&order={Uri.EscapeDataString(order)}&reverse=true&limit={limit}";

        foreach (var mirror in RadioBrowserMirrors)
        {
            var json = await FetchTextSafeAsync(mirror + query, ct);
            if (string.IsNullOrWhiteSpace(json)) continue;
            var parsed = ParseRadioBrowserStationsJson(json);
            if (parsed.Count > 0) return parsed;
        }
        return [];
    }

    internal static IReadOnlyList<RadioBrowserEntry> ParseRadioBrowserStationsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            var list = new List<RadioBrowserEntry>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var nProp) ? (nProp.GetString() ?? "").Trim() : "";
                var rawUrl = el.TryGetProperty("url_resolved", out var urProp) && !string.IsNullOrWhiteSpace(urProp.GetString())
                    ? urProp.GetString()!
                    : (el.TryGetProperty("url", out var uProp) ? uProp.GetString() ?? "" : "");
                var normalizedUrl = NormalizeStreamUrl(rawUrl);
                if (string.IsNullOrWhiteSpace(name) || normalizedUrl is null) continue;

                var lastOk = !el.TryGetProperty("lastcheckok", out var okProp) || okProp.ValueKind != JsonValueKind.Number || okProp.GetInt32() != 0;
                if (!lastOk) continue;

                var fav = el.TryGetProperty("favicon", out var fProp) ? fProp.GetString() : null;
                var tags = el.TryGetProperty("tags", out var tProp) ? tProp.GetString() : null;
                var votes = el.TryGetProperty("votes", out var vProp) && vProp.TryGetInt32(out var v) ? v : 0;
                var clicks = el.TryGetProperty("clickcount", out var cProp) && cProp.TryGetInt32(out var c) ? c : 0;
                var bitrate = el.TryGetProperty("bitrate", out var bProp) && bProp.TryGetInt32(out var br) ? br : 0;

                list.Add(new RadioBrowserEntry(name, normalizedUrl, fav, tags, votes, clicks, bitrate, lastOk));
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    private async Task<Dictionary<string, int>> FetchRadioBrowserCountriesAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mirror in RadioBrowserMirrors)
        {
            var json = await FetchTextSafeAsync(mirror + "/json/countrycodes?hidebroken=true", ct);
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var code = item.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim().ToLowerInvariant() : "";
                    var count = item.TryGetProperty("stationcount", out var sc) && sc.TryGetInt32(out var cnt) ? cnt : 0;
                    if (IsValidCountryCode(code) && count > 0)
                        map[code] = count;
                }
                if (map.Count > 0) return map;
            }
            catch { }
        }
        return map;
    }

    private async Task<IReadOnlyList<GitHubRadioCountry>> FetchM3uCountriesAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(7));
            using var resp = await _http.GetAsync(IndexUrl, cts.Token);
            if (!resp.IsSuccessStatusCode) return [];
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (!doc.RootElement.TryGetProperty("playlists", out var playlists) || playlists.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<GitHubRadioCountry>();
            foreach (var item in playlists.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var t) || t.GetString() != "country") continue;
                var code = item.TryGetProperty("id", out var idProp) ? (idProp.GetString() ?? "").Trim().ToLowerInvariant() : "";
                if (!IsValidCountryCode(code)) continue;
                var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? code.ToUpperInvariant() : code.ToUpperInvariant();
                var flag = item.TryGetProperty("flag", out var flagProp) && !string.IsNullOrWhiteSpace(flagProp.GetString())
                    ? flagProp.GetString()!
                    : Iso2ToFlag(code);
                var count = item.TryGetProperty("stations", out var stProp) && stProp.TryGetInt32(out var n) ? n : 0;
                list.Add(new GitHubRadioCountry(code, name, flag, count));
            }
            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return []; }
    }

    internal static string Iso2ToFlag(string code)
    {
        if (!IsValidCountryCode(code)) return "";
        return string.Concat(
            new Rune(0x1F1E6 + (code[0] - 'a')).ToString(),
            new Rune(0x1F1E6 + (code[1] - 'a')).ToString());
    }

    internal static IReadOnlyList<GitHubRadioStation> ParseM3u(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<GitHubRadioStation>();
        var lines = text.Split('\n');
        string? pendingName = null;
        string? pendingLogo = null;
        string? pendingGroup = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                pendingLogo = ExtractAttr(line, "tvg-logo");
                pendingGroup = ExtractAttr(line, "group-title");
                pendingName = ExtractExtInfTitle(line);
                continue;
            }
            if (line.StartsWith('#')) continue;

            var normalizedUrl = NormalizeStreamUrl(line);
            if (normalizedUrl is not null && !string.IsNullOrWhiteSpace(pendingName))
            {
                result.Add(new GitHubRadioStation(pendingName, normalizedUrl, pendingLogo, pendingGroup));
            }
            pendingName = null;
            pendingLogo = null;
            pendingGroup = null;
        }
        return result;
    }

    internal static string? NormalizeStreamUrl(string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        var path = uri.AbsolutePath;
        if (path.EndsWith(".asx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ram", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".wax", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            return null;
        if (path.Equals("/listen.pls", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri) { Path = "/", Query = "" };
            return builder.Uri.ToString();
        }
        if (path.EndsWith(".pls", StringComparison.OrdinalIgnoreCase))
            return null;
        return uri.ToString();
    }

    private static string? SanitizeLogo(string? logo)
    {
        if (string.IsNullOrWhiteSpace(logo)) return null;
        var trimmed = logo.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            return null;
        if (u.Host.Contains("tunein.com", StringComparison.OrdinalIgnoreCase))
            return null;
        return trimmed;
    }

    private static string? ExtractAttr(string extInf, string attrName)
    {
        var m = Regex.Match(extInf, attrName + "=\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[1].Value.Trim() : null;
    }

    private static string ExtractExtInfTitle(string extInf)
    {
        var inQuotes = false;
        for (var i = 0; i < extInf.Length; i++)
        {
            var ch = extInf[i];
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == ',' && !inQuotes)
                return extInf[(i + 1)..].Trim();
        }
        return "";
    }

    private async Task<string?> VerifyCandidateUrlsAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        foreach (var u in urls.Take(3))
        {
            if (await VerifyStreamAsync(u, ct))
                return u;
        }
        return null;
    }

    public async Task<bool> VerifyStreamAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5));
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent", "Lavf/60.16.100");
            req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return false;

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                return false;

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[4096];
            var total = 0;
            while (total < buf.Length)
            {
                var n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), cts.Token);
                if (n <= 0) break;
                total += n;
                if (total >= 128 && !IsHlsManifestUrl(uri) && !IsHlsManifestMediaType(mediaType))
                    break;
            }
            if (total == 0) return false;

            var headText = Encoding.UTF8.GetString(buf, 0, Math.Min(total, 1024)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (headText.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || headText.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                return false;

            if (IsHlsManifestUrl(uri) || IsHlsManifestMediaType(mediaType) || headText.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                using var ms = new MemoryStream();
                ms.Write(buf, 0, total);
                int readMore;
                while ((readMore = await stream.ReadAsync(buf, cts.Token)) > 0 && ms.Length < 65536)
                    ms.Write(buf, 0, readMore);
                var manifest = Encoding.UTF8.GetString(ms.ToArray());
                if (!manifest.Contains("#EXTM3U", StringComparison.Ordinal))
                    return false;
                var finalUri = resp.RequestMessage?.RequestUri ?? uri;
                var isMaster = manifest.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);
                var childUri = PickFirstHlsUri(manifest, finalUri);
                if (childUri is null) return false;

                using var childReq = new HttpRequestMessage(HttpMethod.Get, childUri);
                childReq.Headers.TryAddWithoutValidation("User-Agent", "Lavf/60.16.100");
                using var childResp = await _http.SendAsync(childReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!childResp.IsSuccessStatusCode) return false;
                var childMedia = childResp.Content.Headers.ContentType?.MediaType ?? "";
                if (childMedia.Equals("text/html", StringComparison.OrdinalIgnoreCase)) return false;
                if (isMaster)
                {
                    var childText = await childResp.Content.ReadAsStringAsync(cts.Token);
                    var childFinal = childResp.RequestMessage?.RequestUri ?? childUri;
                    return PickFirstHlsUri(childText, childFinal) is not null;
                }
                return true;
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return false;
        }
    }

    private static bool IsHlsManifestUrl(Uri uri) =>
        uri.Scheme is "http" or "https" && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static bool IsHlsManifestMediaType(string mediaType) =>
        mediaType.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("audio/mpegurl", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("audio/x-mpegurl", StringComparison.OrdinalIgnoreCase);

    private static Uri? PickFirstHlsUri(string manifest, Uri baseUri)
    {
        foreach (var raw in manifest.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (Uri.TryCreate(baseUri, line, out var resolved) && resolved.Scheme is "http" or "https")
                return resolved;
        }
        return null;
    }

    private async Task<string?> FetchTextSafeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(7));
            using var resp = await _http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.Content.Headers.ContentLength is > 5 * 1024 * 1024) return null;
            return await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    internal static bool IsValidCountryCode(string code) =>
        code.Length == 2 && code[0] is >= 'a' and <= 'z' && code[1] is >= 'a' and <= 'z';

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
