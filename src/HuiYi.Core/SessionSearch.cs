namespace HuiYi;

/// Searches displayed content, including legacy notes and custom template sections.
public static class SessionSearch
{
    public static bool Matches(Session session, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        bool Contains(string? text) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        if (Contains(session.DisplayTitle) || session.Lines.Any(l => Contains(l.Src) || Contains(l.Dst))) return true;
        if (session.Notes is not { } written) return false;
        var notes = SpeakerLabels.WithNames(written, SpeakerLabels.Names(session));
        return Contains(notes.Title) || Contains(notes.Summary)
            || notes.Topics.Any(t => Contains(t.Title) || Contains(t.Summary) || t.Points.Any(Contains)
                || (t.Fishbone is { } f && (Contains(f.Question) || f.Branches.Any(b => Contains(b.Label) || Contains(b.Detail)))))
            || notes.Parts().Any(p => Contains(p.Title) || p.Items.Any(i => Contains(i.Text) || Contains(i.Owner) || Contains(i.Due)))
            || notes.Terms.Any(t => Contains(t.Name) || Contains(t.Note) || t.Heard.Any(Contains));
    }
}
