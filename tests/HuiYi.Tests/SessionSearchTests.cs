namespace HuiYi.Tests;

public class SessionSearchTests
{
    [Fact]
    public void SearchesNotesAndBothTranscriptLanguages()
    {
        var session = new Session
        {
            Title = "Weekly",
            Lines = [new() { Src = "Original words", Dst = "译文" }],
            Notes = new()
            {
                Summary = "会议概要",
                Topics = [new() { Title = "议题", Points = ["关键观点"], Fishbone = new() { Question = "原因", Branches = [new() { Detail = "分析详情" }] } }],
                Sections = [new() { Title = "自定义", Items = [new() { Text = "行动", Owner = "张三", Due = "星期五" }] }],
                Terms = [new() { Name = "术语", Note = "说明", Heard = ["误识别"] }]
            }
        };
        foreach (var query in new[] { "weekly", "ORIGINAL", "译文", "概要", "关键观点", "原因", "分析详情", "自定义", "行动", "张三", "星期五", "说明", "误识别" })
            Assert.True(SessionSearch.Matches(session, query), query);
        Assert.False(SessionSearch.Matches(session, "不存在"));
        Assert.True(SessionSearch.Matches(new Session(), "  "));
        Assert.False(SessionSearch.Matches(new Session(), "missing"));
    }
}
