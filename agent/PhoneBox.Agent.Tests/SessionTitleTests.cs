using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// A Claude Code chat's title is drawn from the user's prompts, so it is off unless Settings › Claude Code turns it
/// on. These tests follow it along every path a session can take out of the agent.
/// </summary>
public sealed class SessionTitleTests : IDisposable
{
    private const string Title = "Quarterly invoice numbers for Acme";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-titles-" + Guid.NewGuid());

    private string Config(bool? shareTitles)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, shareTitles is null ? "{}" : "{\"claude\":{\"shareSessionTitles\":" + (shareTitles.Value ? "true" : "false") + "}}");
        return path;
    }

    private static AgentSession Session(string? title) =>
        new("claude:sess-1", "claude", "develop-int-61", "develop-int", "idle", null, 1, 1, Pid: 7, Title: title);

    /// <summary>A session file like Claude Code writes it, with a transcript that has a generated title in it.</summary>
    private string Chat(string name = "develop-int-61", string nameSource = "derived", string? transcript = null)
    {
        var sessions = Path.Combine(_root, ".claude", "sessions");
        var chats = Path.Combine(_root, ".claude", "projects", "-Users-test-develop-int");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(chats);
        File.WriteAllText(Path.Combine(sessions, "7.json"), JsonSerializer.Serialize(new
        { pid = 7, sessionId = "sess-1", cwd = "/Users/test/develop-int", name, nameSource, status = "idle" }));
        File.WriteAllText(Path.Combine(chats, "sess-1.jsonl"), transcript ?? $"{{\"type\":\"last-prompt\",\"lastPrompt\":\"PRIVATE PROMPT\"}}\n{{\"type\":\"ai-title\",\"aiTitle\":\"{Title}\"}}\n");
        return _root;
    }

    [Fact]
    public void TitlesAreOffByDefault() => Assert.False(new ClaudeConfig().ShareSessionTitles);

    [Fact]
    public void ADerivedNameIsReplacedByTheChatTitle_OnlyWhenTitlesAreOn()
    {
        Chat();
        var on = Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true));
        Assert.Equal(Title, on.Title);
        Assert.Equal("develop-int-61", on.Name); // what the status file says stays as it was
        Assert.Equal("develop-int", on.Project);

        var off = Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7));
        Assert.Null(off.Title);
    }

    [Fact]
    public void ANameTheUserGaveBeatsAGeneratedTitle_AndTheNewestGeneratedTitleWins()
    {
        Chat(name: "My own name", nameSource: "user");
        Assert.Equal("My own name", Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title);

        Chat(transcript: "{\"type\":\"ai-title\",\"aiTitle\":\"Old\"}\n{\"type\":\"last-prompt\",\"lastPrompt\":\"PRIVATE PROMPT\"}\n{\"type\":\"ai-title\",\"aiTitle\":\"Newer\"}\n");
        Assert.Equal("Newer", Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title);

        Chat(transcript: "{\"type\":\"ai-title\",\"aiTitle\":\"Generated\"}\n{\"type\":\"custom-title\",\"customTitle\":\"Renamed by me\"}\n");
        Assert.Equal("Renamed by me", Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title);
    }

    [Fact]
    public void NoTitleYetAndALongTitle()
    {
        Chat(transcript: "{\"type\":\"last-prompt\",\"lastPrompt\":\"PRIVATE PROMPT\"}\n");
        Assert.Null(Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title); // not the placeholder, not the prompt

        Chat(transcript: $"{{\"type\":\"ai-title\",\"aiTitle\":\"{new string('x', 500)}\"}}\n");
        Assert.Equal(200, Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title!.Length);
    }

    [Fact]
    public void ARewrittenOrShorterChatFileDoesNotKeepTheOldTitle()
    {
        Chat(transcript: "{\"type\":\"custom-title\",\"customTitle\":\"Old name I gave it\"}\n{\"type\":\"ai-title\",\"aiTitle\":\"Old generated\"}\n" + new string(' ', 200) + "\n");
        Assert.Equal("Old name I gave it", Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title);

        // the same file written again, shorter, with only a new generated title
        var file = Path.Combine(_root, ".claude", "projects", "-Users-test-develop-int", "sess-1.jsonl");
        File.WriteAllText(file, "{\"type\":\"ai-title\",\"aiTitle\":\"Brand new\"}\n");
        Assert.Equal("Brand new", Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title);
    }

    [Fact]
    public void AGrowingLargeFileKeepsATitleThatScrolledOutOfTheTail_ButNotAfterItShrinks()
    {
        var file = Path.Combine(_root, ".claude", "projects", "-Users-test-develop-int", "sess-1.jsonl");
        var filler = string.Join("\n", Enumerable.Repeat("{\"type\":\"last-prompt\",\"lastPrompt\":\"" + new string('x', 900) + "\"}", 1000)) + "\n"; // about 1 MB
        Chat(transcript: "{\"type\":\"ai-title\",\"aiTitle\":\"Early title\"}\n" + filler);
        // the title is outside the last 512 KB, so the first read cannot see it
        Assert.Null(Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title);

        File.WriteAllText(file, "{\"type\":\"ai-title\",\"aiTitle\":\"Seen at the start\"}\n" + new string(' ', 10) + "\n");
        Assert.Equal("Seen at the start", Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title);
        File.AppendAllText(file, filler + filler); // grows past the tail: the title is out of sight but was seen
        Assert.Equal("Seen at the start", Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title);

        File.WriteAllText(file, "{\"type\":\"last-prompt\",\"lastPrompt\":\"short\"}\n"); // replaced by a shorter file
        Assert.Null(Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title);
    }

    [Fact]
    public void ALongNameInTheStatusFileIsCutLikeATranscriptTitle()
    {
        Chat(name: new string('n', 500), nameSource: "user");
        Assert.Equal(SessionMonitor.MaxTitleChars, Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title!.Length);
        Chat(name: "   padded name   ", nameSource: "user");
        Assert.Equal("padded name", Assert.Single(SessionMonitor.ReadClaude(_root, pid => pid == 7, titles: true)).Title);
    }

    [Theory]
    [InlineData(null, false)]   // no setting saved: off
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void SnapshotIsTheOnePlaceTheSettingIsApplied(bool? setting, bool titleComesOut)
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance, new ConfigStore(Config(setting))) { Sessions = [Session(Title)] };
        Assert.Equal(titleComesOut ? Title : null, Assert.Single(monitor.Snapshot()).Title);
        Assert.Equal("develop-int-61", Assert.Single(monitor.Snapshot()).Name);
    }

    [Fact]
    public void WithoutAConfigNoTitleComesOut()
    {
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance) { Sessions = [Session(Title)] };
        Assert.Null(Assert.Single(monitor.Snapshot()).Title);
    }

    // the live stream and GET /api/work both send exactly this
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheWorkDataForThePhoneCarriesTheTitleOnlyWhenTitlesAreOn(bool on)
    {
        var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance, new ConfigStore(Config(on))) { Sessions = [Session(Title)] };
        var json = JsonSerializer.Serialize(WorkFeed.Build(sessions, new GitHubMonitor(NullLogger<GitHubMonitor>.Instance)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(on, json.Contains(Title));
        Assert.Contains("develop-int", json);
    }

    [Fact]
    public async Task ThePermissionCardNeverShowsTheTitle()
    {
        Directory.CreateDirectory(_root);
        var cfgPath = Path.Combine(_root, "hook-config.json");
        File.WriteAllText(cfgPath, """{"claude":{"phoneApprovals":true,"onlyWhenAway":false,"timeoutSec":30,"shareSessionTitles":true}}""");
        var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_root, "push.json"));
        var approvals = new ApprovalStore(push, NullLogger<ApprovalStore>.Instance);
        var cfg = new ConfigStore(cfgPath);
        var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance, cfg) { Sessions = [Session(Title)] };
        Interlocked.Exchange(ref ApprovalStore.PhoneLastSeen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var asking = ClaudeHook.HandleAsync(JsonDocument.Parse("""{"session_id":"sess-1","tool_name":"Bash","tool_input":{"command":"ls"}}""").RootElement,
            cfg, approvals, sessions, CancellationToken.None);
        var card = Assert.Single(approvals.Snapshot());
        Assert.DoesNotContain(Title, JsonSerializer.Serialize(card));
        Assert.Equal("develop-int-61", card.Project);
        approvals.Decide(card.Id, "deny");
        await asking;
    }

    [Theory]
    [InlineData(null, false)]   // no setting saved: off
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void OpenCodeTitlesFollowTheirOwnSwitch(bool? setting, bool titleComesOut)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config-oc-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, setting is null ? "{}" : "{\"opencode\":{\"shareSessionTitles\":" + (setting.Value ? "true" : "false") + "}}");
        var oc = new AgentSession("opencode:ses_1", "opencode", "ses_1", "develop-int", "idle", null, 1, 1, Title: Title);
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance, new ConfigStore(path)) { Sessions = [oc] };
        Assert.Equal(titleComesOut ? Title : null, Assert.Single(monitor.Snapshot()).Title);
    }

    [Fact]
    public void EachToolKeepsItsOwnTitleSwitch()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config-mix-" + Guid.NewGuid().ToString("N") + ".json");
        // Claude's titles are shared, OpenCode's are not: only the claude row keeps its title
        File.WriteAllText(path, """{"claude":{"shareSessionTitles":true}}""");
        var claude = new AgentSession("claude:sess-1", "claude", "develop-int-61", "develop-int", "idle", null, 1, 1, Pid: 7, Title: Title);
        var opencode = new AgentSession("opencode:ses_1", "opencode", "ses_1", "develop-int", "idle", null, 1, 1, Title: Title);
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance, new ConfigStore(path)) { Sessions = [claude, opencode] };
        var (first, second) = (Assert.Single(monitor.Snapshot(), s => s.Tool == "claude"), Assert.Single(monitor.Snapshot(), s => s.Tool == "opencode"));
        Assert.Equal(Title, first.Title);
        Assert.Null(second.Title);

        // the other way round: OpenCode shared, Claude not
        File.WriteAllText(path, """{"opencode":{"shareSessionTitles":true}}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)); // two quick writes can share a time stamp
        var (first2, second2) = (Assert.Single(monitor.Snapshot(), s => s.Tool == "claude"), Assert.Single(monitor.Snapshot(), s => s.Tool == "opencode"));
        Assert.Null(first2.Title);
        Assert.Equal(Title, second2.Title);

        // OpenCode unshared again (the row still carries the stale title in Name until the refresh): the title is
        // stripped from both Title and Name; Claude's switch is untouched by this config (off by default here)
        File.WriteAllText(path, """{"opencode":{"shareSessionTitles":false}}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
        var (first3, second3) = (Assert.Single(monitor.Snapshot(), s => s.Tool == "claude"), Assert.Single(monitor.Snapshot(), s => s.Tool == "opencode"));
        Assert.Null(first3.Title);
        Assert.Null(second3.Title);
        Assert.Equal("ses_1", second3.Name); // the short id the reader uses, never the title
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void AntigravityTitlesFollowTheirOwnSwitch(bool? setting, bool titleComesOut)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config-agy-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, setting is null ? "{}" : "{\"antigravity\":{\"shareSessionTitles\":" + (setting.Value ? "true" : "false") + "}}");
        var agy = new AgentSession("antigravity:conv-1", "antigravity", "antigravity-conv", "Antigravity", "idle", null, 1, 1, Title: Title);
        var monitor = new SessionMonitor(NullLogger<SessionMonitor>.Instance, new ConfigStore(path)) { Sessions = [agy] };
        Assert.Equal(titleComesOut ? Title : null, Assert.Single(monitor.Snapshot()).Title);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
