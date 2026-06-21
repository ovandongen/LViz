using LViz.App.Services;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// Verifies <see cref="KeymapStateService.Load"/> routes by file extension and
/// gates <c>.json</c> on a Moergo profile being active.
/// </summary>
public class KeymapStateServiceDispatchTests
{
    private static string WriteTemp(string extension, string content)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"lviz-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Json_OnNonMoergoProfile_Fails()
    {
        // The gate trips on extension + profile before any file read, so the
        // path needn't even exist.
        var result = new KeymapStateService().Load("does-not-exist.json", "Corne");
        var failure = Assert.IsType<KeymapLoadResult.Failure>(result);
        Assert.Contains("Moergo", failure.Error.Message);
    }

    [Fact]
    public void Json_OnMoergoProfile_LoadsViaJsonLoader()
    {
        var path = WriteTemp(".json", KeymapFixtures.Read("moergo-glove80.json"));
        try
        {
            var result = new KeymapStateService().Load(path, "Glove80");
            var success = Assert.IsType<KeymapLoadResult.Success>(result);
            Assert.Equal(8, success.LayerCount);
            Assert.Equal(80, success.FirstLayerBindingCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Keymap_StillRoutesToDevicetreeLoader()
    {
        var path = WriteTemp(".keymap", KeymapFixtures.Read("corne.keymap"));
        try
        {
            var result = new KeymapStateService().Load(path, "Corne");
            Assert.IsType<KeymapLoadResult.Success>(result);
        }
        finally { File.Delete(path); }
    }
}
