using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Talks to a fake Home Assistant: states are filtered to the widgets' entities, switching maps to services.</summary>
public sealed class HomeAssistantTests : IDisposable
{
    private readonly HttpListener _server;
    private readonly string _url;
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"phonebox-test-{Guid.NewGuid():N}.json");
    private readonly List<string> _calls = [];
    private readonly Task _loop;

    public HomeAssistantTests()
    {
        _server = TestPorts.Listen();
        _url = _server.Prefixes.Single().TrimEnd('/');
        _loop = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _server.GetContextAsync(); } catch { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            string body;
            if (ctx.Request.Headers["Authorization"] != "Bearer secret") { ctx.Response.StatusCode = 401; body = "{}"; }
            else if (path == "/api/states")
                body = """
                [
                  {"entity_id":"light.desk","state":"on","attributes":{"friendly_name":"Desk lamp"},"last_changed":"2026-11-07T10:00:00+00:00"},
                  {"entity_id":"sensor.temp","state":"21.43","attributes":{"friendly_name":"Office","unit_of_measurement":"°C","device_class":"temperature"}},
                  {"entity_id":"lock.door","state":"locked","attributes":{"friendly_name":"Door"}},
                  {"entity_id":"switch.secret","state":"off","attributes":{"friendly_name":"Not in any widget"}}
                ]
                """;
            else
            {
                using var r = new StreamReader(ctx.Request.InputStream);
                var call = path + " " + await r.ReadToEndAsync();
                lock (_calls) _calls.Add(call);
                body = "[]";
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private HomeAssistant Client(bool control)
    {
        var cfg = new
        {
            homeAssistant = new { url = _url, token = "secret" },
            pages = new[]
            {
                new { id = "home", title = "Home", widgets = new object[]
                {
                    new { id = "ha", type = "ha", x = 0, y = 0, w = 6, h = 3, options = new { entities = "light.desk, sensor.temp, lock.door", control } },
                } },
            },
        };
        File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg));
        return new HomeAssistant(new ConfigStore(_configPath), NullLogger<HomeAssistant>.Instance);
    }

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task StatesAreFilteredToWidgetEntities()
    {
        var ha = Client(control: false);
        var r = Json(await ha.StatesAsync(["light.desk", "sensor.temp", "switch.secret"]));
        Assert.True(r.GetProperty("configured").GetBoolean());
        var ids = r.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(["light.desk", "sensor.temp"], ids); // switch.secret is not in any widget
        var temp = r.GetProperty("entities")[1];
        Assert.Equal("21.43", temp.GetProperty("state").GetString());
        Assert.Equal("°C", temp.GetProperty("unit").GetString());
        Assert.Equal("temperature", temp.GetProperty("deviceClass").GetString());
    }

    [Fact]
    public async Task SwitchingNeedsTheWidgetsPermission()
    {
        var ha = Client(control: false);
        Assert.NotNull(await ha.ToggleAsync("light.desk"));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task SwitchingCallsTheRightService()
    {
        var ha = Client(control: true);
        await ha.StatesAsync(["lock.door"]); // the lock's current state decides lock / unlock
        Assert.Null(await ha.ToggleAsync("light.desk"));
        Assert.Null(await ha.ToggleAsync("lock.door"));
        Assert.NotNull(await ha.ToggleAsync("sensor.temp"));    // sensors cannot be switched
        Assert.NotNull(await ha.ToggleAsync("switch.secret"));  // not in a widget
        Assert.Equal(["/api/services/homeassistant/toggle {\"entity_id\":\"light.desk\"}", "/api/services/lock/unlock {\"entity_id\":\"lock.door\"}"], _calls);
    }

    [Fact]
    public async Task TestReportsABadToken()
    {
        Assert.Equal("Connected", await HomeAssistant.TestAsync(_url, "secret"));
        Assert.Equal("The token was rejected", await HomeAssistant.TestAsync(_url, "wrong"));
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Close();
        try { File.Delete(_configPath); } catch { }
    }
}
