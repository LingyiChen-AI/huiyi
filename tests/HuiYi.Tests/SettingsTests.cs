using System.Text.Json;
using HuiYi;

namespace HuiYi.Tests;

public class SettingsTests
{
    public static TheoryData<string> Presets()
    {
        var data = new TheoryData<string>();
        foreach (var p in ProviderPreset.All) data.Add(p.Id);
        return data;
    }

    // Issue #7: a DeepSeek service made settings.json unwritable, so after a restart the services were gone.
    [Theory]
    [MemberData(nameof(Presets))]
    public void ServicesOfEveryPlatformSurviveARestart(string preset)
    {
        var info = ProviderPreset.Find(preset);
        var settings = new AppSettings
        {
            Providers = [new AiProvider { Preset = preset, Name = info.Name, BaseUrl = info.BaseUrl, Model = "m" }],
        };
        settings.ActiveProviderId = settings.Providers[0].Id;

        var json = JsonSerializer.Serialize(settings, Json.Options);
        var back = JsonSerializer.Deserialize<AppSettings>(json, Json.Options)!;

        var p = Assert.Single(back.Providers);
        Assert.Equal(settings.ActiveProviderId, back.ActiveProviderId);
        Assert.Equal(preset, p.Preset);
        Assert.Equal(info.BaseUrl, p.BaseUrl);
        Assert.DoesNotContain("\"info\"", json);
    }
}
