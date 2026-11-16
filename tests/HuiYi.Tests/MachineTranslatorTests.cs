using Xunit;

namespace HuiYi.Tests;

/// The free translators: language codes per service, and how auto moves past one that fails.
public class MachineTranslatorTests
{
    private static string? Code(string engine, string code, bool source = false) => MachineTranslators.ById(engine)!.Code(code, source);

    [Fact]
    public void EachServiceGetsItsOwnLanguageCodes()
    {
        Assert.Equal("zh", Code("transmart", "zh-CN"));
        Assert.Equal("zh-TW", Code("transmart", "zh-TW"));
        Assert.Equal("zh-Hant", Code("volcengine", "zh-TW"));
        Assert.Equal("zh-Hans", Code("bing", "zh-CN"));
        Assert.Equal("zh-CN", Code("google", "zh-CN"));
        Assert.Equal("ja", Code("volcengine", "ja"));
    }

    [Fact]
    public void ServicesThatCantTakeALanguageAreSkippedForIt()
    {
        // Yandex writes no Traditional Chinese; TranSmart misreads Cantonese.
        Assert.Null(Code("yandex", "zh-TW"));
        Assert.Equal("zh", Code("yandex", "zh-TW", source: true));
        Assert.Null(Code("transmart", "yue", source: true));
        Assert.Equal("", Code("volcengine", "yue", source: true));
    }

    [Fact]
    public void EveryTargetLanguageHasAServiceUnderAuto()
    {
        foreach (var target in Languages.Target)
            Assert.Contains(MachineTranslators.All, e => e.Code(target.Code, false) != null);
    }

    private static MachineEngine Fake(string id, Func<string, string> run, List<string>? calls = null) =>
        new(id, id, (c, _) => c, (text, _, _, _) =>
        {
            calls?.Add(id);
            return Task.FromResult(run(text));
        });

    [Fact]
    public async Task AutoMovesOnWhenOneFailsAndRestsIt()
    {
        var calls = new List<string>();
        var t = new TranslationService(new AiClient())
        {
            Catalog = [Fake("a", _ => throw new HttpRequestException("429"), calls), Fake("b", _ => "translated", calls)],
        };
        Assert.Equal("translated", await t.Translate("hello", "en", "zh-CN", TranslationService.Kind.Draft));
        Assert.Equal("translated", await t.Translate("hello", "en", "zh-CN", TranslationService.Kind.Draft));
        // The one that failed rests: the second time only b is asked.
        Assert.Equal(["a", "b", "b"], calls);
        Assert.False(t.MachineResting);
    }

    [Fact]
    public async Task AiChosenWithoutAnAiServiceFallsBackToTheFreeOnes()
    {
        var t = new TranslationService(new AiClient()) { Catalog = [Fake("a", _ => "translated")], Machine = () => MachineTranslators.Ai };
        Assert.Equal("translated", await t.Translate("hello", "en", "zh-CN", TranslationService.Kind.Draft));
        Assert.False(t.MachineResting);
    }

    [Fact]
    public async Task TextHandedBackUntranslatedCountsAsAFailure()
    {
        var t = new TranslationService(new AiClient()) { Catalog = [Fake("echo", text => text), Fake("b", _ => "你好")] };
        Assert.Equal("你好", await t.Translate("hello", "en", "zh-CN", TranslationService.Kind.Draft));
    }

    [Fact]
    public async Task AChosenServiceIsTheOnlyOneAskedAndAFailureIsSaidOnce()
    {
        var calls = new List<string>();
        var said = new List<string>();
        var t = new TranslationService(new AiClient())
        {
            Catalog = [Fake("a", _ => throw new HttpRequestException("429"), calls), Fake("b", _ => "translated", calls)],
            Machine = () => "a",
        };
        t.MachineLimited += said.Add;
        Assert.Null(await t.Translate("hello", "en", "zh-CN", TranslationService.Kind.Draft));
        Assert.True(t.MachineResting);
        Assert.Null(await t.Translate("hello", "en", "zh-CN", TranslationService.Kind.Draft));
        Assert.Equal(["a"], calls);
        Assert.Equal(["a"], said);
    }
}
