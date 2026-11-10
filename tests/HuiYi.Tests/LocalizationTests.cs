using HuiYi.Localization;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace HuiYi.Tests;

[CollectionDefinition("Interface language", DisableParallelization = true)]
public class InterfaceLanguageCollection { }

[Collection("Interface language")]
public class LocalizationTests
{
    [Theory]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("en-GB", "en")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ko-KR", "ko")]
    [InlineData("de-DE", "en")]
    public void ResolvesSystemLanguage(string system, string expected) =>
        Assert.Equal(expected, L.Resolve("auto", system));

    [Fact]
    public void FormatsTranslatedMessagesWithoutChangingArguments()
    {
        Assert.Equal("Speaker 12", L.For("en", "发言人 {0}", 12));
        Assert.Equal("発言者 12", L.For("ja", "发言人 {0}", 12));
        Assert.Equal("발언자 12", L.For("ko", "发言人 {0}", 12));
        Assert.Equal("發言人 12", L.For("zh-TW", "发言人 {0}", 12));
    }

    [Fact]
    public void UnknownTextIsPreserved() =>
        Assert.Equal("Priya's notes", L.For("ja", "Priya's notes"));

    [Fact]
    public void SwitchingLanguagePreservesContentInsideCapturedMessages()
    {
        var before = L.Language;
        const string content = "原因分析 · 用户写的会议正文";
        try
        {
            L.SetLanguage("zh-CN");
            var read = L.Capture(() => L.T("原因分析 · ") + content);
            var formatted = L.Capture(() => L.F("给「发言人 {0}」起个名字", content));
            foreach (var language in new[] { "en", "ja", "ko", "zh-TW", "zh-CN" })
            {
                L.SetLanguage(language);
                Assert.Equal(L.For(language, "原因分析 · ") + content, read());
                Assert.Equal(L.For(language, "给「发言人 {0}」起个名字", content), formatted());
                Assert.Equal("灵小雅", HuiYi.Speak.SpeakEngine.MingVoice("zh-CN")!.Value.Voice);
            }
        }
        finally { L.SetLanguage(before); }
    }

    [Fact]
    public void ExistingSettingsFollowSystemAndKeepOutputLanguage()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal("auto", settings.UiLanguage);
        Assert.Equal("auto", settings.Target);
        Assert.Equal("auto", settings.NotesLanguage);
    }

    [Fact]
    public void EveryLocaleHasAllMessagesAndTheSameFormatArguments()
    {
        var resources = new ResourceManager("HuiYi.Localization.Strings", typeof(L).Assembly);
        var source = resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var keys = source.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
        foreach (var language in new[] { "en", "ja", "ko", "zh-TW" })
        {
            var translated = resources.GetResourceSet(CultureInfo.GetCultureInfo(language), true, false)!;
            Assert.NotNull(translated);
            Assert.True(keys.SetEquals(translated.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key)), language);
            foreach (var key in keys)
            {
                var text = translated.GetString(key);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: {key}");
                if (!Regex.IsMatch(key, @"\{\d")) continue;
                CompositeFormat.Parse(text!);
                string[] Arguments(string s) => Regex.Matches(s, @"(?<!\{)\{(\d+)(?:[^{}]*)\}")
                    .Select(m => m.Groups[1].Value).Order().ToArray();
                Assert.Equal(Arguments(key), Arguments(text!));
            }
        }
    }
}
