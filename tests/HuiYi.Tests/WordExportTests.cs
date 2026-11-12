using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;

namespace HuiYi.Tests;

public sealed class WordExportTests
{
    [Fact]
    public void WordDocumentPreservesNotesTranscriptNamesAndTodoTable()
    {
        var session = new Session
        {
            Title = "项目评审 <&> 日本語 한국어", Renamed = true, StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow.AddMinutes(5), VoiceNames = new() { ["7"] = "王敏" },
            Lines = [new() { Id = "a", Src = "请在周五前完成检查。", Voice = 7 },
                new() { Id = "b", Src = "收到，今天开始测试。", Dst = "Testing starts today.", Voice = 7 }],
            Notes = new MeetingNotes
            {
                Summary = "发言人1确认本周完成。",
                Topics = [new() { Title = "发布安排", Summary = "检查进度", Points = ["周五完成"],
                    Fishbone = new() { Question = "为什么延期", Branches = [new() { Label = "依赖", Detail = "等待确认" }] } }],
                Sections = [new() { Title = "待办事项", Style = "todo", Items = [new() { Text = "执行测试", Owner = "王敏", Due = "11月13日" }] }],
                Terms = [new() { Name = "HuiYi", Note = "会议助手", Heard = ["会意"] }]
            }
        };
        using var stream = new MemoryStream(WordExport.Create(session));
        using var document = WordprocessingDocument.Open(stream, false);
        AssertValid(document);
        var body = document.MainDocumentPart!.Document!.Body!;
        Assert.Contains(session.Title, body.InnerText);
        Assert.Contains("王敏确认本周完成", body.InnerText);
        Assert.Contains("Testing starts today.", body.InnerText);
        Assert.Contains("等待确认", body.InnerText);
        Assert.Contains("会意", body.InnerText);
        Assert.Contains("王敏", body.Elements<Table>().Single().InnerText);
        Assert.Equal(2, body.Elements<Table>().Single().Elements<TableRow>().Count());
        Assert.NotEmpty(body.Descendants<NumberingProperties>());
        Assert.Equal("Title", body.Elements<Paragraph>().First().ParagraphProperties!.ParagraphStyleId!.Val);
    }

    [Fact]
    public void OldMeetingWithoutNotesOrVoiceDataExports()
    {
        var session = new Session { Title = "旧会议", StartedAt = DateTime.UtcNow, Lines = [new() { Src = "原文\n下一行" }] };
        using var stream = new MemoryStream(WordExport.Create(session));
        using var document = WordprocessingDocument.Open(stream, false);
        AssertValid(document);
        Assert.Contains("原文", document.MainDocumentPart!.Document!.InnerText);
        Assert.Single(document.MainDocumentPart.Document.Descendants<Break>());
    }

    private static void AssertValid(WordprocessingDocument document)
    {
        var errors = new OpenXmlValidator().Validate(document).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => e.Description + " " + e.Path?.XPath)));
    }
}
