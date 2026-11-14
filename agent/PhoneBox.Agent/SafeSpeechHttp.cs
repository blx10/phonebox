using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>SSRF-hardened HTTP helpers for external cloud STT APIs and explicit loopback local LLM/STT servers.</summary>
internal static class SafeSpeechHttp
{
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    public const long MaxAudioBytes = 25L * 1024 * 1024;
    private const int MaxRedirects = 3;

    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            || ip.Equals(IPAddress.None) || ip.Equals(IPAddress.IPv6None) || ip.IsIPv6LinkLocal
            || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6Teredo || ip.IsIPv6UniqueLocal)
            return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return false;                                     // 0.0.0.0/8
            if (b[0] == 10) return false;                                    // 10.0.0.0/8 (RFC1918)
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;       // 100.64.0.0/10 (CGNAT)
            if (b[0] == 127) return false;                                   // 127.0.0.0/8 (loopback)
            if (b[0] == 169 && b[1] == 254) return false;                    // 169.254.0.0/16 (link-local / metadata)
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;        // 172.16.0.0/12 (RFC1918)
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;         // 192.0.0.0/24 (IETF protocol)
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) return false;         // 192.0.2.0/24 (TEST-NET-1)
            if (b[0] == 192 && b[1] == 168) return false;                    // 192.168.0.0/16 (RFC1918)
            if (b[0] == 198 && b[1] is 18 or 19) return false;               // 198.18.0.0/15 (benchmarking)
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false;      // 198.51.100.0/24 (TEST-NET-2)
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;       // 203.0.113.0/24 (TEST-NET-3)
            if (b[0] >= 224) return false;                                   // 224.0.0.0/4 multicast + reserved
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) return false;                         // fc00::/7 (ULA)
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // 2001:db8::/32 (docs)
            return true;
        }

        return false;
    }

    public static bool IsExplicitLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return IPAddress.IsLoopback(ip);
        }
        return false;
    }

    internal static SocketsHttpHandler CreateHandler(bool allowLoopback, IReadOnlyCollection<int> blockedPorts) =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = (ctx, ct) => ConnectValidatedAsync(ctx, allowLoopback, blockedPorts, ct),
        };

    public static HttpClient CreateClient(bool allowLoopback, IReadOnlyCollection<int> blockedPorts, TimeSpan timeout) =>
        new(CreateHandler(allowLoopback, blockedPorts), disposeHandler: true)
        {
            Timeout = timeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };

    private static async ValueTask<Stream> ConnectValidatedAsync(
        SocketsHttpConnectionContext ctx,
        bool allowLoopback,
        IReadOnlyCollection<int> blockedPorts,
        CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;

        if (blockedPorts.Contains(port))
            throw new HttpRequestException($"Connection to reserved PhoneBox port {port} is blocked.");

        if (allowLoopback && IsExplicitLoopbackHost(host))
        {
            IPAddress[] targets = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : [IPAddress.Loopback, IPAddress.IPv6Loopback];
            return await ConnectFirstAsync(targets, port, requirePublic: false, allowLoopback: true, ct);
        }

        IPAddress[] resolved = IPAddress.TryParse(host, out var directIp)
            ? [directIp]
            : await Dns.GetHostAddressesAsync(host, ct);

        return await ConnectFirstAsync(resolved, port, requirePublic: true, allowLoopback: false, ct);
    }

    private static async Task<Stream> ConnectFirstAsync(
        IPAddress[] addresses,
        int port,
        bool requirePublic,
        bool allowLoopback,
        CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var raw in addresses)
        {
            var ip = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
            if (requirePublic && !IsPublicAddress(ip)) continue;
            if (allowLoopback && !IPAddress.IsLoopback(ip)) continue;

            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.NoDelay = true;
                await socket.ConnectAsync(new IPEndPoint(ip, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException(
            $"Target host resolved to no permitted address ({addresses.Length} candidate(s)).",
            lastError);
    }

    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    public static bool CanAttachCredentials(Uri uri, bool allowLoopback, IReadOnlyCollection<string>? allowedCredentialHosts)
    {
        if (allowedCredentialHosts is { Count: > 0 })
        {
            return uri.Scheme == Uri.UriSchemeHttps
                && allowedCredentialHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase));
        }

        return uri.Scheme == Uri.UriSchemeHttps
            || (allowLoopback && uri.Scheme == Uri.UriSchemeHttp && IsExplicitLoopbackHost(uri.Host));
    }

    /// <summary>Executes a request with manual redirect validation, strict credential host scoping, and cross-origin write protection.</summary>
    public static async Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
        HttpClient client,
        Func<Uri, bool, HttpRequestMessage> requestFactory,
        Uri initialUri,
        bool allowLoopback,
        IReadOnlyCollection<int> blockedPorts,
        IReadOnlyCollection<string>? allowedCredentialHosts,
        CancellationToken ct)
    {
        var currentUri = initialUri;
        var includeAuth = true;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var sendAuth = includeAuth && CanAttachCredentials(currentUri, allowLoopback, allowedCredentialHosts);
            using var req = requestFactory(currentUri, sendAuth);
            var res = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            var code = (int)res.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308) || res.Headers.Location is null)
                return res;

            var nextUri = res.Headers.Location.IsAbsoluteUri
                ? res.Headers.Location
                : new Uri(currentUri, res.Headers.Location);
            res.Dispose();

            if (!IsPermittedRedirectTarget(currentUri, nextUri, allowLoopback, blockedPorts, out var reason))
                throw new HttpRequestException(reason);

            if (req.Method != HttpMethod.Get && req.Method != HttpMethod.Head && !SameOrigin(currentUri, nextUri))
                throw new HttpRequestException("Blocked cross-origin redirect for non-GET request.");

            if (!SameOrigin(currentUri, nextUri))
                includeAuth = false;

            currentUri = nextUri;
        }

        throw new HttpRequestException($"Too many HTTP redirects (>{MaxRedirects}).");
    }

    public static bool IsPermittedRedirectTarget(
        Uri from,
        Uri to,
        bool allowLoopback,
        IReadOnlyCollection<int> blockedPorts,
        out string reason)
    {
        reason = "";
        if (!string.IsNullOrEmpty(to.UserInfo))
        {
            reason = "Redirect target with credentials is blocked.";
            return false;
        }

        if (from.Scheme == Uri.UriSchemeHttps && to.Scheme != Uri.UriSchemeHttps)
        {
            reason = "Blocked unsafe HTTPS -> HTTP redirect.";
            return false;
        }

        if (to.Scheme != Uri.UriSchemeHttps && !(allowLoopback && to.Scheme == Uri.UriSchemeHttp && IsExplicitLoopbackHost(to.Host)))
        {
            reason = "Blocked redirect to non-HTTPS target.";
            return false;
        }

        if (blockedPorts.Contains(to.Port))
        {
            reason = "Blocked redirect to reserved PhoneBox port.";
            return false;
        }

        if (IsExplicitLoopbackHost(to.Host) && !allowLoopback)
        {
            reason = "Blocked redirect to loopback host.";
            return false;
        }

        return true;
    }

    public static async Task<byte[]> ReadBoundedAudioBytesAsync(string wavPath, CancellationToken ct)
    {
        var info = new FileInfo(wavPath);
        if (!info.Exists || info.Length <= 0)
            throw new InvalidOperationException("Captured audio file is missing or empty.");
        if (info.Length > MaxAudioBytes)
            throw new InvalidOperationException($"Captured audio exceeds maximum upload size ({MaxAudioBytes / (1024 * 1024)} MB).");
        return await File.ReadAllBytesAsync(wavPath, ct);
    }

    public static async Task<string> ReadBoundedStringAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        var limit = MaxResponseBytes + 1;
        while (ms.Length < limit)
        {
            var read = await stream.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, limit - (int)ms.Length)), ct);
            if (read == 0) break;
            ms.Write(buf, 0, read);
        }

        if (ms.Length > MaxResponseBytes)
            throw new InvalidOperationException($"Speech provider response exceeded maximum size ({MaxResponseBytes} bytes).");

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
