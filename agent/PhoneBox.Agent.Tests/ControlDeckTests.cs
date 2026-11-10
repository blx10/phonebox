using PhoneBox.Agent;
using Xunit;

public sealed class ControlDeckTests
{
    [Fact]
    public void PushToTalkExpiresAndRestoresTheOriginalState()
    {
        long time = 0; var muted = true;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => time);
        var id = leases.Begin("microphone");
        Assert.NotNull(id); Assert.False(muted);
        time = 2999; leases.Sweep(); Assert.False(muted);
        time = 3000; leases.Sweep(); Assert.True(muted);
        Assert.False(leases.Renew(id!));
    }
    [Fact]
    public void OverlappingHoldsDoNotRestoreTheMicrophoneUntilTheLastRelease()
    {
        var muted = true;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => 0);
        var a = leases.Begin("microphone"); var b = leases.Begin("microphone");
        Assert.True(leases.End(a!)); Assert.False(muted);
        Assert.True(leases.End(b!)); Assert.True(muted);
    }
    [Fact]
    public void ADisconnectDoesNotMuteAPreviouslyUnmutedMicrophone()
    {
        var muted = false;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => 0);
        leases.Begin("microphone"); leases.Sweep(all: true); Assert.False(muted);
    }
    [Fact]
    public void RenewalsCannotKeepAMicrophoneOpenIndefinitely()
    {
        long time = 0; var muted = true;
        var leases = new MicrophoneLeases(_ => muted, (_, value) => { muted = value; return true; }, () => time);
        var id = leases.Begin("microphone")!;
        for (time = 1000; time < 60_000; time += 1000) Assert.True(leases.Renew(id));
        Assert.False(leases.Renew(id)); Assert.True(muted);
    }
    [Fact]
    public void PowerConfirmationIsDelayedBoundToTheActionAndSingleUse()
    {
        long now = 0; var power = new PowerConfirmations(() => now);
        var nonce = power.Arm("restart");
        Assert.False(power.Consume(nonce, "restart"));
        now = 2000; Assert.False(power.Consume(nonce, "shutdown"));
        Assert.True(power.Consume(nonce, "restart")); Assert.False(power.Consume(nonce, "restart"));
        nonce = power.Arm("sleep"); now += 6001; Assert.False(power.Consume(nonce, "sleep"));
    }
    [Fact]
    public void OscUsesPaddedStringsAndNetworkByteOrder()
    {
        Assert.Equal(new byte[] { 47, 102, 0, 0, 44, 102, 0, 0, 63, 128, 0, 0 }, ControlDeck.OscPacket("/f", 1));
        Assert.Throws<ArgumentException>(() => ControlDeck.OscPacket("not-an-address", 1));
    }
    [Fact]
    public void MidiBytesUseWireOrderAndRejectSystemOrOutOfRangeMessages()
    {
        Assert.Equal(0x6407B0, DeckMidi.Parse("B0 07 {value}",100));
        Assert.Equal(0x003CC0, DeckMidi.Parse("C0 3C",0));
        Assert.Throws<ArgumentException>(()=>DeckMidi.Parse("F0 00 00",0));
        Assert.Throws<ArgumentException>(()=>DeckMidi.Parse("90 FF 10",0));
        Assert.Throws<ArgumentException>(()=>DeckMidi.Parse("90 3C",0));
        Assert.Throws<ArgumentOutOfRangeException>(()=>DeckMidi.Parse("B0 07 {value}",128));
    }
    [Fact]
    public void PhoneConfigurationDoesNotExposeControlSecretsOrCommands()
    {
        var cfg = new AgentConfig { Controls = new() { SpotifyToken = "private-token", Actions = [new() { Id = "sound", Target = "private-file-path" }] } };
        var json = System.Text.Json.JsonSerializer.Serialize(cfg.ToPublic());
        Assert.DoesNotContain("private-token", json); Assert.DoesNotContain("private-file-path", json);
        Assert.True(Lan.AllowedOverLan("/api/controls/state"));
        Assert.False(Lan.AllowedOverLan("/api/controls/capture"));
    }
}
