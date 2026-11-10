using Xunit;

public sealed class TestPortsTests
{
    [Fact]
    public async Task ConcurrentFixturesKeepDistinctBoundListeners()
    {
        var listeners = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(TestPorts.Listen)));
        try
        {
            Assert.Equal(16, listeners.Select(l => l.Prefixes.Single()).Distinct().Count());
            Assert.All(listeners, l => Assert.True(l.IsListening));
        }
        finally { foreach (var listener in listeners) listener.Close(); }
    }
}
