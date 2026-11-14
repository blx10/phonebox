using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using PhoneBox.Agent;
using Xunit;

public sealed class CloudPhotoTests
{
    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class TempPhotoEnv : IDisposable
    {
        public string RootDir { get; } = Path.Combine(Path.GetTempPath(), "phonebox-photos-test-" + Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(RootDir, "config.json");
        public string CacheDir => Path.Combine(RootDir, "cache");
        public string PhotoDir => Path.Combine(RootDir, "photos");

        public TempPhotoEnv()
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(PhotoDir);
        }

        public ConfigStore WriteConfig(object config)
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, ConfigStore.WriteOpts));
            return new ConfigStore(ConfigPath);
        }

        public void Dispose()
        {
            try { Directory.Delete(RootDir, recursive: true); } catch { }
        }
    }

    // Minimal valid 1x1 PNG
    private static readonly byte[] MinimalPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    private static readonly byte[] MinimalJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];

    private static HttpResponseMessage JsonResp(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ---------------------------------------------------------------- SafePhotoHttp security tests

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.5.4", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.1.2.3", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:0000::1", false)]
    [InlineData("64:ff9b::127.0.0.1", false)]
    [InlineData("2002:7f00:0001::", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void IsPublicAddressRejectsPrivateAndSpecialRanges(string rawIp, bool expectedPublic)
    {
        var ip = IPAddress.Parse(rawIp);
        Assert.Equal(expectedPublic, SafePhotoHttp.IsPublicAddress(ip));
    }

    [Theory]
    [InlineData("http://example.com/photo.jpg", false)]
    [InlineData("https://user:pass@example.com/photo.jpg", false)]
    [InlineData("https://localhost/photo.jpg", false)]
    [InlineData("https://nas.local/s/abc", false)]
    [InlineData("https://192.168.1.50/s/abc", false)]
    [InlineData("https://cloud.example.com/s/abc", true)]
    public void TryValidateHttpsUriEnforcesHttpsAndPublicHostRules(string rawUrl, bool expectedValid)
    {
        var ok = SafePhotoHttp.TryValidateHttpsUri(rawUrl, out _, out _);
        Assert.Equal(expectedValid, ok);
    }

    [Fact]
    public async Task RedirectHandlerBlocksHttpDowngradeAndStripsCrossHostSecrets()
    {
        using var downgradeHttp = new HttpClient(new StubHttpHandler((req, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Found);
            resp.Headers.Location = new Uri("http://cdn.example.com/file.jpg");
            return Task.FromResult(resp);
        }));

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/file");
            await SafePhotoHttp.SendFollowingRedirectsAsync(downgradeHttp, req, default);
        });

        var capturedHeaders = new List<(string Host, bool HasAuth, bool HasCookie, bool HasDropboxArg, bool HasCustom)>();
        using var stripHttp = new HttpClient(new StubHttpHandler((req, _) =>
        {
            capturedHeaders.Add((
                req.RequestUri!.Host,
                req.Headers.Contains("Authorization"),
                req.Headers.Contains("Cookie"),
                req.Headers.Contains("Dropbox-API-Arg"),
                req.Headers.Contains("X-Custom-Trace")
            ));

            if (req.RequestUri.Host == "api.example.com" && req.RequestUri.AbsolutePath == "/start")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://api.example.com/same-host");
                return Task.FromResult(r);
            }
            if (req.RequestUri.Host == "api.example.com" && req.RequestUri.AbsolutePath == "/same-host")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://cdn.other.example/final.jpg");
                return Task.FromResult(r);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MinimalPng) });
        }));

        using var initial = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/start");
        initial.Headers.Add("Authorization", "Bearer top-secret");
        initial.Headers.Add("Cookie", "sid=123");
        initial.Headers.Add("Dropbox-API-Arg", """{"path":"/a.jpg"}""");
        initial.Headers.Add("X-Custom-Trace", "trace-1");

        using var finalResp = await SafePhotoHttp.SendFollowingRedirectsAsync(stripHttp, initial, default);
        Assert.Equal(HttpStatusCode.OK, finalResp.StatusCode);
        Assert.Equal(3, capturedHeaders.Count);
        Assert.True(capturedHeaders[1].HasAuth); // same host keeps auth
        Assert.False(capturedHeaders[2].HasAuth); // cross host strips auth
        Assert.False(capturedHeaders[2].HasCookie);
        Assert.False(capturedHeaders[2].HasDropboxArg);
        Assert.True(capturedHeaders[2].HasCustom);
    }

    [Fact]
    public void DetectRasterMimeValidatesMagicBytesAndRejectsActiveMarkup()
    {
        Assert.Equal("image/png", SafePhotoHttp.DetectRasterMime(MinimalPng));
        Assert.Equal("image/jpeg", SafePhotoHttp.DetectRasterMime(MinimalJpeg));
        Assert.Equal("image/gif", SafePhotoHttp.DetectRasterMime("GIF89a..."u8));
        Assert.Equal("image/webp", SafePhotoHttp.DetectRasterMime("RIFF\x00\x00\x00\x00WEBPVP8 "u8));
        Assert.Equal("image/bmp", SafePhotoHttp.DetectRasterMime("BM\x00\x00"u8));

        Assert.Null(SafePhotoHttp.DetectRasterMime("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8));
        Assert.Null(SafePhotoHttp.DetectRasterMime("<!DOCTYPE html><html></html>"u8));
        Assert.Null(SafePhotoHttp.DetectRasterMime("hello world"u8));
    }

    // ---------------------------------------------------------------- CloudPhotoAuth PKCE tests

    [Theory]
    [InlineData("onedrive", "login.microsoftonline.com")]
    [InlineData("google", "oauth2.googleapis.com")]
    [InlineData("dropbox", "api.dropboxapi.com")]
    public async Task PkceAuthStoresTokensExternallyRefreshesAndPersistsAcrossRestart(string provider, string expectedTokenHost)
    {
        using var env = new TempPhotoEnv();
        var store = env.WriteConfig(new
        {
            cloudPhotos = new
            {
                oneDriveClientId = "od-client-1",
                googleClientId = "gd-client-1",
                googleClientSecret = "gd-secret-optional",
                dropboxClientId = "db-client-1",
            }
        });

        var calls = 0;
        var lastBody = "";
        using var mockHttp = new HttpClient(new StubHttpHandler(async (req, ct) =>
        {
            calls++;
            if (req.Method == HttpMethod.Post && req.RequestUri!.Host == expectedTokenHost && req.RequestUri.AbsolutePath.Contains("token"))
            {
                lastBody = await req.Content!.ReadAsStringAsync(ct);
                var refresh = lastBody.Contains("grant_type=refresh_token") ? "refreshed-tok-2" : "access-tok-1";
                return JsonResp($$"""{"access_token":"{{refresh}}","refresh_token":"refresh-secret-9","expires_in":3600}""");
            }
            // Account lookup
            return JsonResp("""{"email":"user@example.com","mail":"user@example.com"}""");
        }));

        string state;
        using (var auth = new CloudPhotoAuth(store, new AgentEndpoint(19991), mockHttp))
        {
            var (url, redirect) = auth.Begin(provider);
            Assert.Equal($"http://127.0.0.1:19991/api/photos/oauth/callback/{provider}", redirect);
            var qs = QueryHelpers.ParseQuery(new Uri(url).Query);
            Assert.Equal("S256", qs["code_challenge_method"]);
            Assert.Equal(43, qs["code_challenge"].ToString().Length);
            state = qs["state"].ToString();

            Assert.True(auth.HasPendingState(provider, state));
            var (okWrong, _) = await auth.CompleteAsync(provider, "bad-state", "auth-code", default);
            Assert.False(okWrong);

            var (ok, err) = await auth.CompleteAsync(provider, state, "auth-code-123", default);
            Assert.True(ok, err);
            Assert.Contains("code_verifier=", lastBody);
            if (provider == "google")
                Assert.Contains("client_secret=gd-secret-optional", lastBody);
            Assert.False(auth.HasPendingState(provider, state));

            // CancelPending removes unused state
            var (url2, _) = auth.Begin(provider);
            var state2 = QueryHelpers.ParseQuery(new Uri(url2).Query)["state"].ToString();
            Assert.True(auth.HasPendingState(provider, state2));
            auth.CancelPending(provider, state2);
            Assert.False(auth.HasPendingState(provider, state2));
        }

        // Verify secrets are only in config.json.photos.json (with 0600 on Unix) and never config.json or ToPublic()
        var credPath = store.Path + ".photos.json";
        Assert.True(File.Exists(credPath));
        Assert.DoesNotContain("refresh-secret-9", File.ReadAllText(store.Path));
        Assert.DoesNotContain("refresh-secret-9", JsonSerializer.Serialize(store.Get().ToPublic()));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(credPath));

        // Restart persistence + force refresh
        using (var restarted = new CloudPhotoAuth(store, new AgentEndpoint(19991), mockHttp))
        {
            Assert.True(restarted.IsConnected(provider));
            Assert.Equal("user@example.com", restarted.GetAccount(provider));
            Assert.Equal("access-tok-1", await restarted.GetAccessTokenAsync(provider, forceRefresh: false, default));
            Assert.Equal("refreshed-tok-2", await restarted.GetAccessTokenAsync(provider, forceRefresh: true, default));
            await restarted.DisconnectAsync(provider, default);
            Assert.False(restarted.IsConnected(provider));
        }
        Assert.True(calls >= 3);
    }

    // ---------------------------------------------------------------- Provider tests (iCloud, OneDrive, Google, Dropbox, Nextcloud, Local, Stack)

    [Fact]
    public async Task ICloudSharedAlbumHandles330ShardValidationAndCachesRasterBytes()
    {
        using var env = new TempPhotoEnv();
        var store = env.WriteConfig(new
        {
            pages = new[]
            {
                new
                {
                    id = "main", title = "Main", enabled = true,
                    widgets = new[]
                    {
                        new
                        {
                            id = "ic1", type = "photos", x = 0, y = 0, w = 6, h = 4,
                            options = new { provider = "icloud", shareUrl = "https://www.icloud.com/sharedalbum/#B0zSampleToken1" }
                        }
                    }
                }
            }
        });

        Assert.Equal("p61-sharedstreams.icloud.com", CloudPhotoService.ICloudInitialHost("B0zSampleToken1"));
        Assert.False(CloudPhotoService.IsValidICloudStreamHost("evil.example.com"));
        Assert.True(CloudPhotoService.IsValidICloudStreamHost("p42-sharedstreams.icloud.com"));
        Assert.True(CloudPhotoService.IsValidICloudAssetHost("cvws.icloud-content.com"));
        Assert.False(CloudPhotoService.IsValidICloudAssetHost("127.0.0.1"));

        var imageHits = 0;
        var failUpstream = false;
        using var mockHttp = new HttpClient(new StubHttpHandler((req, _) =>
        {
            if (failUpstream)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            var host = req.RequestUri!.Host;
            var path = req.RequestUri.AbsolutePath;
            if (host == "p61-sharedstreams.icloud.com" && path.EndsWith("/webstream"))
            {
                var r = new HttpResponseMessage((HttpStatusCode)330);
                r.Headers.TryAddWithoutValidation("X-Apple-MMe-Host", "p42-sharedstreams.icloud.com");
                return Task.FromResult(r);
            }
            if (host == "p42-sharedstreams.icloud.com" && path.EndsWith("/webstream"))
            {
                return Task.FromResult(JsonResp("""
                {
                  "streamName": "Summer Trip",
                  "photos": [
                    {
                      "photoGuid": "vid-1",
                      "mediaAssetType": "video",
                      "derivatives": { "thumb": { "checksum": "cs-vid", "width": "100", "height": "100" } }
                    },
                    {
                      "photoGuid": "img-1",
                      "caption": "Sunset pier",
                      "dateCreated": "2025-07-14T09:15:00Z",
                      "derivatives": {
                        "small": { "checksum": "cs-sm", "width": "320", "height": "240" },
                        "large": { "checksum": "cs-lg", "width": "1600", "height": "1200" }
                      }
                    }
                  ]
                }
                """));
            }
            if (host == "p42-sharedstreams.icloud.com" && path.EndsWith("/webasseturls"))
            {
                return Task.FromResult(JsonResp("""
                {
                  "items": {
                    "cs-lg": { "url_location": "cvws.icloud-content.com", "url_path": "/B/sunset.jpg?t=1" },
                    "cs-evil": { "url_location": "127.0.0.1", "url_path": "/secret" }
                  }
                }
                """));
            }
            if (host == "cvws.icloud-content.com")
            {
                imageHits++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MinimalJpeg) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

        using var auth = new CloudPhotoAuth(store, new AgentEndpoint(19992), mockHttp);
        using var service = new CloudPhotoService(store, auth, mockHttp, env.CacheDir);

        var feed = await service.GetWidgetFeedAsync("ic1", refresh: false, default);
        Assert.True(feed.Configured);
        Assert.Equal("Summer Trip", feed.SourceLabel);
        var only = Assert.Single(feed.Photos);
        Assert.Equal("Sunset pier", only.Caption);
        Assert.StartsWith("/api/photos/image/", only.Url);

        // Fetch bytes twice: second read comes from local disk cache
        var firstImg = await service.GetPhotoBytesAsync(only.Id, default);
        Assert.NotNull(firstImg);
        Assert.Equal("image/jpeg", firstImg!.Value.Mime);

        var secondImg = await service.GetPhotoBytesAsync(only.Id, default);
        Assert.NotNull(secondImg);
        Assert.Equal(1, imageHits);

        // Refresh fails -> stale fallback preserves last photos
        failUpstream = true;
        var stale = await service.GetWidgetFeedAsync("ic1", refresh: true, default);
        Assert.True(stale.Stale);
        Assert.Single(stale.Photos);

        // Spoofed 330 X-Apple-MMe-Host must be blocked
        failUpstream = false;
        using var evilHttp = new HttpClient(new StubHttpHandler((_, _) =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)330);
            r.Headers.TryAddWithoutValidation("X-Apple-MMe-Host", "127.0.0.1:8080");
            return Task.FromResult(r);
        }));
        using var evilService = new CloudPhotoService(store, auth, evilHttp, env.CacheDir);
        var spoofResp = await evilService.GetWidgetFeedAsync("ic1", refresh: true, default);
        Assert.Empty(spoofResp.Photos);
        Assert.Contains("Blocked invalid iCloud shard host", spoofResp.Error ?? "");
    }

    [Fact]
    public async Task OneDriveGoogleDriveAndDropboxBrowseAndFetchFeedsCorrectly()
    {
        using var env = new TempPhotoEnv();
        var store = env.WriteConfig(new
        {
            cloudPhotos = new
            {
                oneDriveClientId = "od-client",
                googleClientId = "gd-client",
                dropboxClientId = "db-client",
            }
        });
        File.WriteAllText(store.Path + ".photos.json", JsonSerializer.Serialize(new
        {
            providers = new Dictionary<string, object>
            {
                ["onedrive"] = new { clientId = "od-client", accessToken = "expired-od", refreshToken = "rf-od", expiresAtMs = 9999999999999L },
                ["google"] = new { clientId = "gd-client", accessToken = "ok-gd", refreshToken = "rf-gd", expiresAtMs = 9999999999999L },
                ["dropbox"] = new { clientId = "db-client", accessToken = "ok-db", refreshToken = "rf-db", expiresAtMs = 9999999999999L },
            }
        }));

        var odCalls = 0;
        var sawDropboxArg = false;
        using var mockHttp = new HttpClient(new StubHttpHandler((req, _) =>
        {
            var url = req.RequestUri!.AbsoluteUri;

            // OAuth token refresh for OneDrive after 401
            if (req.RequestUri.Host == "login.microsoftonline.com")
                return Task.FromResult(JsonResp("""{"access_token":"fresh-od","refresh_token":"rf-od-2","expires_in":3600}"""));

            if (req.RequestUri.Host == "graph.microsoft.com")
            {
                if (++odCalls == 1 && req.Headers.Authorization?.Parameter == "expired-od")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                if (url.Contains("/bundles"))
                    return Task.FromResult(JsonResp("""{"value":[{"id":"alb-1","name":"Holidays","bundle":{"album":{},"childCount":5}}]}"""));
                if (url.Contains("/items/alb-1/children"))
                {
                    return Task.FromResult(JsonResp("""
                    {
                      "value": [
                        {
                          "id": "od-heic",
                          "name": "IMG_001.heic",
                          "file": { "mimeType": "image/heic" },
                          "image": {},
                          "photo": { "takenDateTime": "2025-05-10T12:00:00Z" },
                          "thumbnails": [{ "large": { "url": "https://cdn.onedrive.example/thumb.jpg" } }]
                        }
                      ]
                    }
                    """));
                }
            }

            if (req.RequestUri.Host == "www.googleapis.com")
            {
                if (url.Contains("mimeType+%3D+%27application%2Fvnd.google-apps.folder%27") || Uri.UnescapeDataString(url).Contains("application/vnd.google-apps.folder"))
                {
                    return Task.FromResult(JsonResp("""{"files":[{"id":"gd-folder-1","name":"Roadtrip"}]}"""));
                }
                return Task.FromResult(JsonResp("""
                {
                  "files": [
                    {
                      "id": "gd-file-1",
                      "name": "canyon.jpg",
                      "mimeType": "image/jpeg",
                      "description": "Grand Canyon",
                      "imageMediaMetadata": { "time": "2024:09:21 15:30:00" }
                    }
                  ]
                }
                """));
            }

            if (req.RequestUri.Host == "api.dropboxapi.com")
            {
                if (url.EndsWith("/list_folder"))
                {
                    return Task.FromResult(JsonResp("""
                    {
                      "entries": [
                        { ".tag": "folder", "name": "Albums", "path_display": "/Albums" },
                        { ".tag": "file", "name": "lake.png", "path_lower": "/lake.png", "rev": "r1",
                          "media_info": { "metadata": { "time_taken": "2025-01-03T10:00:00Z" } } }
                      ],
                      "has_more": true,
                      "cursor": "cur-1"
                    }
                    """));
                }
                if (url.EndsWith("/list_folder/continue"))
                {
                    return Task.FromResult(JsonResp("""
                    {
                      "entries": [
                        { ".tag": "file", "name": "mountain.png", "path_lower": "/mountain.png", "rev": "r2" }
                      ],
                      "has_more": false
                    }
                    """));
                }
            }

            if (req.RequestUri.Host == "content.dropboxapi.com")
            {
                sawDropboxArg = req.Headers.TryGetValues("Dropbox-API-Arg", out var vals)
                    && vals.Any(v => v.Contains("/lake.png"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MinimalPng) });
            }

            if (req.RequestUri.Host == "cdn.onedrive.example")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MinimalJpeg) });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

        using var auth = new CloudPhotoAuth(store, new AgentEndpoint(19993), mockHttp);
        using var service = new CloudPhotoService(store, auth, mockHttp, env.CacheDir);

        // OneDrive albums browse + 401 refresh + feed
        var odBrowse = await service.BrowseAsync(new PhotoBrowseRequest("onedrive", "albums", null, null, null), default);
        Assert.True(odBrowse.Ok);
        Assert.Equal("Holidays", Assert.Single(odBrowse.Entries).Name);

        var odPreview = await service.GetPreviewFeedAsync(new PhotoPreviewRequest(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"provider":"onedrive","albumId":"alb-1"}""")), default);
        var odItem = Assert.Single(odPreview.Photos);
        Assert.Equal("IMG_001", odItem.Caption);
        var odImg = await service.GetPhotoBytesAsync(odItem.Id, default);
        Assert.Equal("image/jpeg", odImg!.Value.Mime);

        // Google Drive browse + feed
        var gdBrowse = await service.BrowseAsync(new PhotoBrowseRequest("google", null, null, null, null), default);
        Assert.True(gdBrowse.Ok);
        Assert.Equal("Roadtrip", Assert.Single(gdBrowse.Entries).Name);

        var gdFeed = await service.GetPreviewFeedAsync(new PhotoPreviewRequest(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"provider":"google","albumId":"gd-folder-1"}""")), default);
        var gdItem = Assert.Single(gdFeed.Photos);
        Assert.Equal("Grand Canyon", gdItem.Caption);
        Assert.NotNull(gdItem.TakenAt);

        // Dropbox pagination + Dropbox-API-Arg download
        var dbBrowse = await service.BrowseAsync(new PhotoBrowseRequest("dropbox", null, "", null, null), default);
        Assert.True(dbBrowse.Ok);
        Assert.Single(dbBrowse.Entries);

        var dbFeed = await service.GetPreviewFeedAsync(new PhotoPreviewRequest(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"provider":"dropbox","folder":"/"}""")), default);
        Assert.Equal(2, dbFeed.Photos.Count);
        var dbBytes = await service.GetPhotoBytesAsync(dbFeed.Photos[0].Id, default);
        Assert.NotNull(dbBytes);
        Assert.Equal("image/png", dbBytes!.Value.Mime);
        Assert.True(sawDropboxArg);
    }

    [Fact]
    public async Task NextcloudSupportsWebDavFallbackAndRejectsXxe()
    {
        using var env = new TempPhotoEnv();
        var store = env.WriteConfig(new { });

        // XXE rejection test directly on parser and via HTTP
        const string xxeXml = """
            <?xml version="1.0"?>
            <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <d:multistatus xmlns:d="DAV:"><d:response><d:href>/public.php/webdav/a.jpg</d:href></d:response></d:multistatus>
            """;
        Assert.Throws<XmlException>(() => CloudPhotoService.ParseWebDavResponses(xxeXml, "/public.php/webdav", false));

        using var mockHttp = new HttpClient(new StubHttpHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains("/public.php/dav/files/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            if (path.EndsWith("/public.php/webdav/"))
            {
                const string davXml = """
                <?xml version="1.0" encoding="utf-8"?>
                <d:multistatus xmlns:d="DAV:">
                  <d:response>
                    <d:href>/public.php/webdav/</d:href>
                    <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat>
                  </d:response>
                  <d:response>
                    <d:href>/public.php/webdav/cabin.png</d:href>
                    <d:propstat><d:prop><d:resourcetype/><d:getcontenttype>image/png</d:getcontenttype><d:getlastmodified>Mon, 14 Jul 2025 10:00:00 GMT</d:getlastmodified></d:prop></d:propstat>
                  </d:response>
                </d:multistatus>
                """;
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207)
                {
                    Content = new StringContent(davXml, Encoding.UTF8, "application/xml")
                });
            }

            if (path.EndsWith("/public.php/webdav/cabin.png"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MinimalPng) });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }));

        using var auth = new CloudPhotoAuth(store, new AgentEndpoint(19994), mockHttp);
        using var service = new CloudPhotoService(store, auth, mockHttp, env.CacheDir);

        var feed = await service.GetPreviewFeedAsync(new PhotoPreviewRequest(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"provider":"nextcloud","shareUrl":"https://cloud.example.com/s/Token99","sharePassword":"pw"}""")), default);
        var photo = Assert.Single(feed.Photos);
        Assert.Equal("cabin", photo.Caption);
        var bytes = await service.GetPhotoBytesAsync(photo.Id, default);
        Assert.Equal("image/png", bytes!.Value.Mime);
    }

    [Fact]
    public async Task LocalFolderRespectsSubfoldersSkipsSymlinksAndSupportsSmartStackChild()
    {
        using var env = new TempPhotoEnv();
        var subDir = Directory.CreateDirectory(Path.Combine(env.PhotoDir, "Day1")).FullName;
        var outsideFile = Path.Combine(env.RootDir, "outside.png");
        File.WriteAllBytes(Path.Combine(env.PhotoDir, "top.png"), MinimalPng);
        File.WriteAllBytes(Path.Combine(env.PhotoDir, "disguised.jpg"), "<svg><script>alert(1)</script></svg>"u8.ToArray());
        File.WriteAllBytes(Path.Combine(subDir, "nested.jpg"), MinimalJpeg);
        File.WriteAllBytes(outsideFile, MinimalPng);

        var symlinkCreated = false;
        try
        {
            File.CreateSymbolicLink(Path.Combine(env.PhotoDir, "escape.png"), outsideFile);
            symlinkCreated = true;
        }
        catch { /* Windows without Developer Mode may forbid symlinks */ }

        var store = env.WriteConfig(new
        {
            pages = new[]
            {
                new
                {
                    id = "home", title = "Home", enabled = true,
                    widgets = new object[]
                    {
                        new
                        {
                            id = "stack-1", type = "stack", x = 0, y = 0, w = 6, h = 4,
                            options = new
                            {
                                widgets = new object[]
                                {
                                    new
                                    {
                                        id = "photo-card", type = "photos", enabled = true,
                                        options = new { provider = "local", folder = env.PhotoDir, subfolders = true }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });

        using var auth = new CloudPhotoAuth(store, new AgentEndpoint(19995));
        using var service = new CloudPhotoService(store, auth, new HttpClient(), env.CacheDir);

        // Smart Stack child lookup (stack-1:photo-card)
        var stackFeed = await service.GetWidgetFeedAsync("stack-1:photo-card", refresh: false, default);
        Assert.True(stackFeed.Configured);
        var captions = stackFeed.Photos.Select(p => p.Caption).ToList();
        Assert.Contains("top", captions);
        Assert.Contains("nested", captions);
        if (symlinkCreated) Assert.DoesNotContain("escape", captions);

        // Disguised SVG named .jpg must never be served
        var disguised = stackFeed.Photos.First(p => p.Caption == "disguised");
        Assert.Null(await service.GetPhotoBytesAsync(disguised.Id, default));

        // subfolders = false excludes nested.jpg
        var flatFeed = await service.GetPreviewFeedAsync(new PhotoPreviewRequest(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
            {
                provider = "local",
                folder = env.PhotoDir,
                subfolders = false
            }))), default);
        Assert.DoesNotContain(flatFeed.Photos, p => p.Caption == "nested");
    }

    // ---------------------------------------------------------------- Config normalization, scrubbing & LAN guard tests

    [Fact]
    public void ConfigNormalizationClampsScrubsPrivateOptionsAndRestrictsLan()
    {
        using var env = new TempPhotoEnv();
        var store = new ConfigStore(env.ConfigPath);
        var raw = JsonSerializer.Serialize(new
        {
            cloudPhotos = new { oneDriveClientId = "  od-1 ", cacheMaxMb = 99999 },
            pages = new[]
            {
                new
                {
                    id = "p1", title = "Photos", enabled = true,
                    widgets = new object[]
                    {
                        new
                        {
                            id = "ph1", type = "photos", x = 0, y = 0, w = 6, h = 4,
                            options = new
                            {
                                provider = "evil-provider",
                                shareUrl = "https://www.icloud.com/sharedalbum/#B0zSecret",
                                sharePassword = "secret-pw",
                                folder = "/Users/alice/Private",
                                albumId = "album-99",
                                interval = 1,
                                fit = "<script>",
                                transition = "explode",
                                order = "bad",
                                info = "all-hacked",
                                tapAction = "delete",
                            }
                        },
                        new
                        {
                            id = "st1", type = "stack", x = 6, y = 0, w = 6, h = 4,
                            options = new
                            {
                                widgets = new[]
                                {
                                    new
                                    {
                                        id = "c1", type = "photos", enabled = true,
                                        options = new
                                        {
                                            provider = "nextcloud",
                                            shareUrl = "https://nc.example.com/s/Tok",
                                            sharePassword = "pw2",
                                            interval = 999999,
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });

        Assert.Null(store.Save(raw));
        var loaded = store.Get();
        Assert.Equal("od-1", loaded.CloudPhotos.OneDriveClientId);
        Assert.Equal(2048, loaded.CloudPhotos.CacheMaxMb);

        var parsedTop = PhotoWidgetOptions.Parse(loaded.Pages[0].Widgets![0].Options);
        Assert.Equal("local", parsedTop.Provider);
        Assert.Equal(5, parsedTop.IntervalSec);
        Assert.Equal("cover", parsedTop.Fit);
        Assert.Equal("fade", parsedTop.Transition);
        Assert.Equal("shuffle", parsedTop.Order);
        Assert.Equal("caption-date", parsedTop.Info);
        Assert.Equal("next", parsedTop.TapAction);

        var parsedChild = PhotoWidgetOptions.Parse(StackWidgets.Children(loaded.Pages[0].Widgets![1])[0].Options);
        Assert.Equal("nextcloud", parsedChild.Provider);
        Assert.Equal(86400, parsedChild.IntervalSec);

        // Public serialization scrubs all private photo fields & CloudPhotos config
        var pubJson = JsonSerializer.Serialize(loaded.ToPublic());
        Assert.DoesNotContain("B0zSecret", pubJson);
        Assert.DoesNotContain("secret-pw", pubJson);
        Assert.DoesNotContain("/Users/alice/Private", pubJson);
        Assert.DoesNotContain("album-99", pubJson);
        Assert.DoesNotContain("nc.example.com", pubJson);
        Assert.DoesNotContain("pw2", pubJson);
        Assert.DoesNotContain("od-1", pubJson);

        // LAN allowlist: feed & images allowed; OAuth / browse / test / local-roots blocked
        Assert.True(Lan.AllowedOverLan(new PathString("/api/photos/feed")));
        Assert.True(Lan.AllowedOverLan(new PathString("/api/photos/image/abc123")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/photos/status")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/photos/local-roots")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/photos/browse")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/photos/test")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/photos/preview-feed")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/photos/oauth/connect/onedrive")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/photos/oauth/callback/onedrive")));
    }
}
