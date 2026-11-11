using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Regression tests for the agent core defects found in the 0.9.4 audit. Every test works on its own temporary files.</summary>
public sealed class CoreAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-core-audit-" + Guid.NewGuid().ToString("N"));
    public CoreAuditTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---------------------------------------------------------------- C1: unreadable config.json + X-Config-Version

    /// <summary>config.json is unreadable when the agent starts: the settings page (GET, then PUT with the version it got) must be able to save.</summary>
    [Fact]
    public void C1_ASettingsPageCanSaveOverAConfigFileThatWasUnreadableAtStart()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{ \"night\": { \"start\": \"01:00\" }, }, oops");
        var s = new ConfigStore(path);
        var loaded = s.Version;                                                   // GET /api/settings -> X-Config-Version
        var page = JsonSerializer.Serialize(s.Get(), ConfigStore.WriteOpts);      // ... and its body
        Assert.Null(s.Save(page, loaded));                                        // PUT /api/settings
    }

    /// <summary>A hand edit breaks config.json while the agent runs: same dead end, and reloading the page does not help.</summary>
    [Fact]
    public void C1_ASettingsPageCanSaveAfterAHandEditBrokeTheFile()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        var s = new ConfigStore(path);
        _ = s.Get();
        Thread.Sleep(20);
        File.WriteAllText(path, "{ \"display\": { \"theme\": \"amoled\", ");          // unfinished edit, saved
        for (var reload = 0; reload < 3; reload++)
        {
            var loaded = s.Version;
            var page = JsonSerializer.Serialize(s.Get(), ConfigStore.WriteOpts);
            Assert.Null(s.Save(page, loaded));
        }
    }

    // ---------------------------------------------------------------- C2: pushed values are never written while something pushes often

    [Fact]
    public void C2_ValuesReachDataJsonWhileAScriptKeepsPushing()
    {
        var data = Path.Combine(_dir, "data.json");
        var s = new PushStore(NullLogger<PushStore>.Instance, data);
        s.Set("door", JsonSerializer.SerializeToElement("open"), null);           // set once, must survive a restart
        var until = DateTime.UtcNow.AddSeconds(7);
        for (var i = 0; DateTime.UtcNow < until; i++)                             // e.g. a sensor script, once a second
        {
            s.Set("cpu-temp", JsonSerializer.SerializeToElement(40 + i), null);
            Thread.Sleep(1000);
        }
        Assert.True(File.Exists(data), "data.json was never written in 7 s of one push per second");
        var restarted = new PushStore(NullLogger<PushStore>.Instance, data);
        Assert.NotNull(restarted.Get("door"));
    }

    // ---------------------------------------------------------------- C3: adb exit codes

    private string FakeAdb(bool pmPathFailsLikeAndroid)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, pmPathFailsLikeAndroid ? "adb-real.cmd" : "adb-zero.cmd");
            File.WriteAllText(cmd, string.Join("\r\n",
                "@echo off",
                "if \"%1\"==\"devices\" (echo List of devices attached& echo FAKE123\tdevice& exit /b 0)",
                "if \"%4\"==\"pm\" exit /b " + (pmPathFailsLikeAndroid ? "1" : "0"),   // pm path <missing package>: no output
                "exit /b 0", ""));
            return cmd;
        }
        var sh = Path.Combine(_dir, pmPathFailsLikeAndroid ? "adb-real" : "adb-zero");
        File.WriteAllText(sh, "#!/bin/sh\nif [ \"$1\" = devices ]; then printf 'List of devices attached\\nFAKE123\\tdevice\\n'; exit 0; fi\n"
            + "if [ \"$4\" = pm ]; then exit " + (pmPathFailsLikeAndroid ? "1" : "0") + "; fi\nexit 0\n");
        File.SetUnixFileMode(sh, (UnixFileMode)0b111_101_101);
        return sh;
    }

    private async Task<string[]> MissingAppAfterAFewRounds(bool pmPathFailsLikeAndroid)
    {
        var cfgPath = Path.Combine(_dir, $"config-{pmPathFailsLikeAndroid}.json");
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(new { adbPath = FakeAdb(pmPathFailsLikeAndroid), display = new { homeScreen = false, keepAwake = false } }));
        var adb = new AdbReverse(new ConfigStore(cfgPath), NullLogger<AdbReverse>.Instance);
        using var cts = new CancellationTokenSource();
        await adb.StartAsync(cts.Token);
        try
        {
            for (var i = 0; i < 300 && adb.MissingApp.Length == 0; i++) await Task.Delay(100);
            return adb.MissingApp;
        }
        finally { cts.Cancel(); try { await adb.StopAsync(CancellationToken.None); } catch { } }
    }

    /// <summary>Android's "pm path" exits 1 for a package that is not installed, and adb shell passes that on.</summary>
    [Fact]
    public async Task C3_APhoneWithoutTheAppIsOfferedTheInstall() =>
        Assert.Equal(["FAKE123"], await MissingAppAfterAFewRounds(pmPathFailsLikeAndroid: true));

    // ---------------------------------------------------------------- C4: null entries in the settings JSON

    [Theory]
    [InlineData("{\"shortcuts\":[null]}")]
    [InlineData("{\"shortcuts\":[{\"label\":\"x\",\"cmd\":\"notepad\",\"steps\":[null]}]}")]
    [InlineData("{\"night\":{\"start\":null}}")]
    [InlineData("{\"pages\":[{\"id\":\"p\",\"widgets\":[{\"type\":null}]}]}")]
    [InlineData("{\"controls\":{\"profiles\":[{\"id\":\"p\",\"actions\":[null]}]}}")]
    public void C4_SaveAnswersNullEntriesWithAnErrorMessageNotAnException(string json)
    {
        var s = new ConfigStore(Path.Combine(_dir, "config.json"));
        var ex = Record.Exception(() => s.Save(json));
        Assert.Null(ex);
    }
}
