using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class AppearanceTests
{
    private static void WithConfig(string json, Action<ConfigStore> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "phonebox-appearance-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        try { check(new ConfigStore(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExistingLayoutsDoNotAcquireAppearanceOverrides() => WithConfig("{}", store =>
    {
        var config = store.Get();
        Assert.Null(config.Display.Appearance);
        Assert.All(config.Pages, p => { Assert.Null(p.Appearance); Assert.All(p.Widgets!, w => Assert.Null(w.Appearance)); });
    });

    [Fact]
    public void ImportedAppearanceValuesAreBoundedAndOnlyKnownStylesAreAccepted() => WithConfig("""
        {"display":{"appearance":{"style":"url(private)","radius":999,"gap":-5,"padding":99,"pagePadding":99}},
         "pages":[{"id":"p","title":"P","appearance":{"style":"flat","border":false},"widgets":[
          {"type":"note","w":6,"h":6,"appearance":{"radius":-5,"gap":99,"pagePadding":99,"shadow":false}}]}]}
        """, store =>
    {
        var config = store.Get();
        var global = config.Display.Appearance!;
        Assert.Null(global.Style); Assert.Equal(32, global.Radius); Assert.Equal(0, global.Gap);
        Assert.Equal(24, global.Padding); Assert.Equal(32, global.PagePadding);
        var page = config.Pages.Single(); Assert.Equal("flat", page.Appearance!.Style); Assert.False(page.Appearance.Border);
        var widget = page.Widgets!.Single(); Assert.Equal(0, widget.Appearance!.Radius);
        Assert.Null(widget.Appearance.Gap); Assert.Null(widget.Appearance.PagePadding); Assert.False(widget.Appearance.Shadow);
        var publicJson = JsonSerializer.SerializeToElement(config.ToPublic(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("flat", publicJson.GetProperty("pages")[0].GetProperty("appearance").GetProperty("style").GetString());
        Assert.Equal(0, publicJson.GetProperty("pages")[0].GetProperty("widgets")[0].GetProperty("appearance").GetProperty("radius").GetInt32());
    });

    [Fact]
    public void DuplicatedWidgetsHaveIndependentAppearance()
    {
        var original = new WidgetConfig { Type = "note", Appearance = new() { Style = "flat", Radius = 24 } };
        var clone = original.Clone();
        clone.Appearance!.Radius = 0;
        Assert.Equal(24, original.Appearance.Radius);
    }

    [Fact]
    public void InvalidAppearanceTypesDoNotOverwriteSettings() => WithConfig("{}", store =>
    {
        Assert.StartsWith("Invalid JSON:", store.Save("""{"display":{"appearance":{"radius":"invalid"}}}"""));
        Assert.Equal("{}", File.ReadAllText(store.Path));
    });
}
