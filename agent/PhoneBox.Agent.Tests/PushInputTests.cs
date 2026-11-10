using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class PushInputTests
{
    [Theory]
    [InlineData("\"later\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    public void NonNumericPayloadTtlRemainsOrdinaryData(string ttl)
    {
        var store = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json"));
        var data = JsonSerializer.Deserialize<JsonElement>("{\"ttl\":"+ttl+",\"value\":1}");
        Assert.Null(store.Set("input", data, null));
        Assert.Null(store.Get("input")!.ExpiresAt);
        Assert.Equal(data.GetRawText(), store.Get("input")!.Data.GetRawText());
        Assert.Null(store.Set("numeric", JsonSerializer.Deserialize<JsonElement>("{\"ttl\":60}"), null));
        Assert.NotNull(store.Get("numeric")!.ExpiresAt);
        Assert.Null(store.Set("explicit", data, 60));
        Assert.NotNull(store.Get("explicit")!.ExpiresAt);
        store.Clear();
    }

    [Theory]
    [InlineData("界", 30000, true)]
    [InlineData("a", 65536, false)]
    [InlineData("a", 65537, true)]
    [InlineData("界", 20000, false)]
    public async Task UnknownLengthRequestLimitCountsBytes(string symbol, int count, bool rejected)
    {
        var text=string.Concat(Enumerable.Repeat(symbol,count));
        var request=new DefaultHttpContext().Request;
        request.Body=new MemoryStream(Encoding.UTF8.GetBytes(text));
        Assert.Null(request.ContentLength);
        var result=await PushStore.ReadBody(request);
        Assert.Equal(rejected, result.Error is not null);
        if (!rejected) Assert.Equal(text, result.Data!.Value.GetString());
    }
}
