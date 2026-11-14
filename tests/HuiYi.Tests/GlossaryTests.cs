using HuiYi;
using Xunit;

namespace HuiYi.Tests;

public class GlossaryTests
{
    private static Glossary Make(params GlossaryTerm[] terms)
    {
        var g = new Glossary(Path.Combine(Path.GetTempPath(), "huiyi-glossary-" + Guid.NewGuid().ToString("N") + ".json"));
        foreach (var t in terms) g.Save(t);
        return g;
    }

    [Fact]
    public void AMisheardSpellingTheUserListedIsAlwaysReplaced()
    {
        var g = Make(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静", "伊利静"] });
        Assert.Equal("我想我和伊丽静都没想到", g.Fix("我想我和毕丽静都没想到"));
        Assert.Equal("伊丽静说", g.Fix("伊利静说"));
    }

    [Fact]
    public void ASoundAlikeOfALongerTermIsCorrected()
    {
        // 惜败 heard as 洗败 / 犯船 for 帆船: three characters or more may differ by the usual blurs.
        var g = Make(new GlossaryTerm { Text = "陈佩娜" }, new GlossaryTerm { Text = "帆船赛" });
        Assert.Equal("叫陈佩娜那个女孩", g.Fix("叫陈佩纳那个女孩"));
        Assert.Equal("参加帆船赛", g.Fix("参加犯船赛"));
    }

    [Fact]
    public void ATwoCharacterTermNeedsTheSameSound()
    {
        var g = Make(new GlossaryTerm { Text = "网关" });
        Assert.Equal("网关配置", g.Fix("网管配置"));       // wang guan = wang guan
        Assert.Equal("网站很慢", g.Fix("网站很慢"));       // not alike
        Assert.Equal("往来的人", g.Fix("往来的人"));       // wang lai: no
    }

    [Fact]
    public void ExactOnlyTermsAreNeverGuessedFromTheirSound()
    {
        var g = Make(new GlossaryTerm { Text = "网关", ExactOnly = true, Misheard = ["往关"] });
        Assert.Equal("网管配置", g.Fix("网管配置"));
        Assert.Equal("网关配置", g.Fix("往关配置"));
    }

    [Fact]
    public void LatinTermsMatchWholeWordsInAnyCase()
    {
        var g = Make(new GlossaryTerm { Text = "Kubernetes", Misheard = ["酷伯奈提斯"] },
                     new GlossaryTerm { Text = "API" });
        Assert.Equal("我们用 Kubernetes 部署", g.Fix("我们用 kubernetes 部署"));
        Assert.Equal("部署到Kubernetes上", g.Fix("部署到酷伯奈提斯上"));
        Assert.Equal("the API is done", g.Fix("the api is done"));
        Assert.Equal("a rapid fix", g.Fix("a rapid fix"));
    }

    [Fact]
    public void DisabledTermsDoNothing()
    {
        var g = Make(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"], Enabled = false });
        Assert.Equal("毕丽静", g.Fix("毕丽静"));
    }

    [Fact]
    public void FixesSayWhatChanged()
    {
        var g = Make(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"] });
        var (text, fixes) = g.FixWithDetails("毕丽静你好");
        Assert.Equal("伊丽静你好", text);
        var f = Assert.Single(fixes);
        Assert.Equal(("毕丽静", "伊丽静", 0), (f.From, f.To, f.At));
    }

    [Fact]
    public void EverydayTextIsLeftAlone()
    {
        // A small glossary over ordinary meeting talk: nothing may change.
        var g = Make(new GlossaryTerm { Text = "网关" }, new GlossaryTerm { Text = "伊丽静" }, new GlossaryTerm { Text = "压测" },
                     new GlossaryTerm { Text = "回归测试" }, new GlossaryTerm { Text = "陈佩娜" });
        foreach (var s in new[] { "大家早，先说登录模块，昨天回归测试全部通过了。", "支付接口联调完成了大约八成，退款流程还没接上。",
                                  "我周五前改好网关配置，周末跑一轮压测。", "好，上线日期维持下周一，压测报告我来整理。",
                                  "这在一年前对我来说都是不可想象的。", "人生很多时候都不重要，过去了就过去了。" })
            Assert.Equal(s, g.Fix(s));
    }

    [Fact]
    public void TranslationsFollowTheGlossary()
    {
        var g = Make(new GlossaryTerm { Text = "网关", Translations = GlossaryTerm.ParseTranslations("gateway；ja=ゲートウェイ") },
                     new GlossaryTerm { Text = "伊丽静" });
        Assert.Equal("改好 gateway 配置", g.Pretranslate("改好 网关 配置", "en"));
        Assert.Equal("改好ゲートウェイ配置", g.Pretranslate("改好网关配置", "ja"));
        Assert.Equal(["伊丽静 (keep this spelling)", "网关 → gateway"], g.Hints("伊丽静说网关要改", "en-US"));
        Assert.Equal("gateway; ja=ゲートウェイ", GlossaryTerm.FormatTranslations(g.Terms[0].Translations));
    }

    [Fact]
    public void ImportAddsNewTermsAndMergesKnownOnes()
    {
        var g = Make(new GlossaryTerm { Text = "网关", Misheard = ["网管"] });
        var (added, updated) = g.Import("# 注释\n网关\t往关|网管\tgateway\t支付请求的入口\n伊丽静=毕丽静|伊利静\nKubernetes\n\n");
        Assert.Equal((2, 1), (added, updated));
        var gateway = g.Terms.Single(t => t.Text == "网关");
        Assert.Equal(["网管", "往关"], gateway.Misheard);
        Assert.Equal("gateway", gateway.Translations["en"]);
        Assert.Equal("支付请求的入口", gateway.Note);
        Assert.Equal(["毕丽静", "伊利静"], g.Terms.Single(t => t.Text == "伊丽静").Misheard);

        var again = Make();
        again.Import(g.Export());
        Assert.Equal(g.Terms.Select(t => (t.Text, string.Join("|", t.Misheard), t.Note)), again.Terms.Select(t => (t.Text, string.Join("|", t.Misheard), t.Note)));
    }

    [Fact]
    public void TheGlossaryIsKeptOnDisk()
    {
        var file = Path.Combine(Path.GetTempPath(), "huiyi-glossary-" + Guid.NewGuid().ToString("N") + ".json");
        new Glossary(file).Save(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"] });
        Assert.Equal("伊丽静", new Glossary(file).Fix("毕丽静"));
        File.Delete(file);
    }

    // ── phase two: learning from the transcript and the notes, marking what was corrected ──

    [Fact]
    public void LearningAddsATermOrItsMisheardSpelling()
    {
        var g = Make(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"], Enabled = false });
        g.Learn("伊丽静", ["P丽静", "毕丽静"]);
        var t = g.Terms.Single();
        Assert.Equal(["毕丽静", "P丽静"], t.Misheard);
        Assert.True(t.Enabled);
        Assert.Equal("我和伊丽静", g.Fix("我和P丽静"));

        g.Learn("陈佩娜", ["陈佩纳"], "帆船世界冠军");
        Assert.Equal("帆船世界冠军", g.Terms.Single(x => x.Text == "陈佩娜").Note);
        Assert.True(g.Knows("陈佩娜", ["陈佩纳"]));
        Assert.False(g.Knows("陈佩娜", ["陈配娜"]));
        Assert.False(g.Knows("江充", []));
    }

    [Fact]
    public void ASpellingLearnedAsRightIsNoLongerMisheard()
    {
        // Added 毕丽静 as misheard by mistake, then learned it is a real name: it stops being replaced.
        var g = Make(new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"] });
        g.Learn("毕丽静", []);
        Assert.Equal("毕丽静", g.Fix("毕丽静"));
    }

    [Fact]
    public void ALineKeepsTheCorrectionsStillInIt()
    {
        var g = Make(new GlossaryTerm { Text = "惜败", Misheard = [] }, new GlossaryTerm { Text = "伊丽静", Misheard = ["毕丽静"] });
        var (text, fixes) = g.FixWithDetails("毕丽静说帆船一米洗败，毕丽静笑了");
        var kept = LineFix.Kept(fixes, text)!;
        Assert.Equal([("毕丽静", "伊丽静"), ("洗败", "惜败")], kept.Select(f => (f.From, f.To)));
        // Cleanup dropped the second sentence: its corrections go with it.
        Assert.Equal([("毕丽静", "伊丽静")], LineFix.Kept(fixes, "伊丽静说帆船一米")!.Select(f => (f.From, f.To)));
        Assert.Null(LineFix.Kept(fixes, "没有了"));
        var merged = LineFix.Merge(kept, [new LineFix { From = "洗败", To = "惜败" }, new LineFix { From = "犯船", To = "帆船" }])!;
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void CountingTheOccurrencesToCorrect()
    {
        Assert.Equal(2, Glossary.CountOf("毕丽静说，毕丽静笑", "毕丽静"));
        Assert.Equal(0, Glossary.CountOf("伊丽静", "毕丽静"));
        Assert.Equal(2, Glossary.CountOf("aaaa", "aa"));
    }
}
