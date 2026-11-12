using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HuiYi.Localization;

namespace HuiYi;

public static class WordExport
{
    public static byte[] Create(Session session)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            main.AddNewPart<StyleDefinitionsPart>().Styles = new Styles(
                new Style(new StyleName { Val = "Normal" },
                    new StyleParagraphProperties(new SpacingBetweenLines { After = "120", Line = "288", LineRule = LineSpacingRuleValues.Auto }),
                    new StyleRunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft YaHei" }, new FontSize { Val = "22" }))
                { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
                HeadingStyle("Title", "32", null), HeadingStyle("Heading1", "28", 0), HeadingStyle("Heading2", "24", 1));
            main.AddNewPart<NumberingDefinitionsPart>().Numbering = new Numbering(
                new AbstractNum(new Level(new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = NumberFormatValues.Bullet }, new LevelText { Val = "•" },
                    new ParagraphProperties(new Indentation { Left = "360", Hanging = "240" })) { LevelIndex = 0 }) { AbstractNumberId = 1 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });

            body.Append(Paragraph(session.DisplayTitle, "Title"));
            var start = session.StartedAt.ToLocalTime();
            string At(double seconds) => NotesPrompt.ClockAt(start, seconds);
            body.Append(Paragraph($"{start:yyyy-MM-dd HH:mm} · {L.T("时长")} {TranscriptExport.Clock(session.Duration.TotalSeconds)}"));
            if (session.Notes is { IsEmpty: false } written)
            {
                var notes = SpeakerLabels.WithNames(written, SpeakerLabels.Names(session));
                body.Append(Paragraph(L.T("纪要"), "Heading1"));
                if (notes.Summary.Length > 0) body.Append(Paragraph(notes.Summary));
                foreach (var topic in notes.Topics)
                {
                    body.Append(Paragraph($"{At(topic.Start)} {topic.Title}", "Heading2"));
                    if (!string.IsNullOrEmpty(topic.Summary)) body.Append(Paragraph(topic.Summary));
                    for (var i = 0; i < topic.Points.Count; i++)
                        body.Append(Bullet(topic.Points[i] + (topic.PointTime(i) is { } time ? $"（{At(time)}）" : "")));
                    if (topic.Fishbone is { } fishbone)
                    {
                        body.Append(Paragraph(L.T("原因分析") + " · " + fishbone.Question));
                        foreach (var branch in fishbone.Branches) body.Append(Bullet(branch.Label + " · " + branch.Detail));
                    }
                }
                foreach (var section in notes.Parts().Where(p => p.Items.Count > 0))
                {
                    body.Append(Paragraph(section.Title, "Heading2"));
                    if (section.Style == "todo") body.Append(TodoTable(section.Items));
                    else foreach (var item in section.Items)
                    {
                        var extra = new[] { item.Owner, item.Due }.Where(x => !string.IsNullOrEmpty(x)).ToList();
                        body.Append(Bullet(item.Text + (extra.Count > 0 ? $"（{string.Join("，", extra)}）" : "")));
                    }
                }
                if (notes.Terms.Count > 0)
                {
                    body.Append(Paragraph(L.T("人名与术语"), "Heading2"));
                    foreach (var term in NoteTerm.Ordered(notes.Terms))
                        body.Append(Bullet(term.Name + " · " + term.Note + (term.Heard.Count > 0 ? $"（{L.T("识别成")} {string.Join("、", term.Heard)}）" : "")));
                }
            }
            body.Append(Paragraph(L.T("逐字稿"), "Heading1"));
            var numbers = SpeakerLabels.Numbers(session.Lines);
            var names = SpeakerLabels.Names(session);
            foreach (var line in session.Lines)
            {
                var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Normal" }),
                    new Run(new RunProperties(new Bold()), Text($"{At(line.T)} {SpeakerLabels.Who(line, numbers, names)}  ")),
                    TextRun(line.Src));
                if (!string.IsNullOrEmpty(line.Dst)) paragraph.ParagraphProperties!.Append(new KeepNext());
                body.Append(paragraph);
                if (!string.IsNullOrEmpty(line.Dst)) body.Append(Paragraph(line.Dst));
            }
            body.Append(new SectionProperties(new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134, Header = 567, Footer = 567, Gutter = 0 }));
        }
        return stream.ToArray();
    }

    private static Style HeadingStyle(string id, string size, int? level)
    {
        var properties = new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "240", After = "120" });
        if (level is { } value) properties.Append(new OutlineLevel { Val = value });
        return new Style(new StyleName { Val = id }, new BasedOn { Val = "Normal" }, properties,
            new StyleRunProperties(new Bold(), new Color { Val = "1E2235" }, new FontSize { Val = size }))
        { Type = StyleValues.Paragraph, StyleId = id };
    }

    private static Text Text(string value) => new(value) { Space = SpaceProcessingModeValues.Preserve };

    private static Run TextRun(string value)
    {
        var run = new Run();
        var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) run.Append(new Break());
            run.Append(Text(lines[i]));
        }
        return run;
    }

    private static Paragraph Paragraph(string value, string style = "Normal") =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = style }), TextRun(value));

    private static Paragraph Bullet(string value)
    {
        var paragraph = Paragraph(value);
        paragraph.ParagraphProperties!.Append(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 }));
        return paragraph;
    }

    private static Table TodoTable(IReadOnlyList<NoteItem> items)
    {
        var table = new Table(new TableProperties(new TableWidth { Width = "9600", Type = TableWidthUnitValues.Dxa },
            new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "D5D9E3" })),
            new TableGrid(new GridColumn { Width = "5200" }, new GridColumn { Width = "1600" }, new GridColumn { Width = "2800" }));
        var header = TableRow([L.T("事项"), L.T("负责人"), L.T("截止时间")]);
        header.PrependChild(new TableRowProperties(new TableHeader()));
        foreach (var cell in header.Elements<TableCell>())
        {
            cell.TableCellProperties!.Append(new Shading { Fill = "ECEEFC", Val = ShadingPatternValues.Clear });
            cell.Descendants<Run>().First().PrependChild(new RunProperties(new Bold()));
        }
        table.Append(header);
        foreach (var item in items) table.Append(TableRow([item.Text, item.Owner ?? "—", item.Due ?? "—"]));
        return table;
    }

    private static TableRow TableRow(string[] values) => new(values.Select((value, index) =>
        new TableCell(new TableCellProperties(new TableCellWidth { Width = new[] { "5200", "1600", "2800" }[index], Type = TableWidthUnitValues.Dxa }), Paragraph(value))));
}
