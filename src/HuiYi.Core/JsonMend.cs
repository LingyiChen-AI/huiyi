using System.Text;
using System.Text.Json.Nodes;

namespace HuiYi;

/// JSON an AI wrote that does not parse, mended without asking again (each new request costs 8–30 s
/// for notes). Two passes: our own for the way a long notes reply goes wrong (issue #4: DeepSeek
/// closed the whole reply after the third topic, then wrote five more topics and the conclusions after
/// it), then the jsonrepair library (JsonRepairSharp, MIT) for the small slips: a missing comma,
/// quote or bracket, a trailing comma, a comment. Whatever comes out still goes through the caller's
/// checks; a reply cut off at its token limit never gets here (`AiClient` rejects it).
public static class JsonMend
{
    /// The reply as one JSON object, mended if need be; null when it cannot be read as one.
    public static JsonObject? Object(string text, out string? how)
    {
        how = null;
        if (Parse(text) is { } plain) return plain;
        var reopened = Reopen(text);
        if (reopened != text && Parse(reopened) is { } r)
        {
            how = "reopened";
            return r;
        }
        // The library on the reply as written first (a topic left open before the next one is its kind
        // of slip, and our pass would close it in the wrong place), then on our pass's result.
        if (Repaired(text) is { } fixedUp)
        {
            how = "repaired";
            return fixedUp;
        }
        if (reopened != text && Repaired(reopened) is { } both)
        {
            how = "reopened, repaired";
            return both;
        }
        return null;
    }

    private static JsonObject? Repaired(string text)
    {
        // InputType.Other: the LLM mode drops whatever follows a closed object, which here is most
        // of the notes.
        try { return Parse(JsonRepairSharp.JsonRepair.RepairJson(text, JsonRepairSharp.JsonRepair.InputType.Other, true)); }
        catch (Exception) { return null; }
    }

    private static JsonObject? Parse(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (Exception) { return null; }
    }

    /// Brackets put right while keeping every key and value: a closing bracket that does not match
    /// gets the closers it skipped, and when the outermost object closes with more to come
    /// (`…]}], "junk": null}, {"start": 6600, …}], "terms": […]}`), the last array closed is opened
    /// again, what was written between its end and the object's end is dropped, and what follows
    /// carries on inside that array, as was meant. Brackets still open at the end are closed.
    public static string Reopen(string text)
    {
        var outp = new StringBuilder(text.Length + 16);
        var stack = new List<char>();
        // Where the last array was closed: the text before its ']' and the brackets open then.
        int lastArrayAt = -1;
        List<char>? lastArrayStack = null;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                outp.Append(ch);
                if (ch == '\\' && i + 1 < text.Length) outp.Append(text[++i]);
                else if (ch == '"') inString = false;
                continue;
            }
            switch (ch)
            {
                case '"':
                    inString = true;
                    outp.Append(ch);
                    break;
                case '{':
                case '[':
                    stack.Add(ch);
                    outp.Append(ch);
                    break;
                case '}':
                case ']':
                {
                    var open = ch == '}' ? '{' : '[';
                    if (!stack.Contains(open)) break;   // nothing to close: a stray bracket, dropped
                    while (stack[^1] != open)
                    {
                        outp.Append(stack[^1] == '{' ? '}' : ']');
                        stack.RemoveAt(stack.Count - 1);
                    }
                    if (ch == ']')
                    {
                        lastArrayAt = outp.Length;
                        lastArrayStack = [.. stack];
                    }
                    outp.Append(ch);
                    stack.RemoveAt(stack.Count - 1);
                    if (stack.Count == 0)
                    {
                        var rest = i + 1;
                        while (rest < text.Length && char.IsWhiteSpace(text[rest])) rest++;
                        if (rest < text.Length && text[rest] == ',' && lastArrayStack != null)
                        {
                            // Closed too early: back to just inside the last array closed.
                            outp.Length = lastArrayAt;
                            stack = [.. lastArrayStack];
                            lastArrayStack = null;
                            continue;
                        }
                        if (rest >= text.Length || text[rest] != ',') return outp.ToString();
                    }
                    break;
                }
                default:
                    outp.Append(ch);
                    break;
            }
        }
        for (var k = stack.Count - 1; k >= 0; k--) outp.Append(stack[k] == '{' ? '}' : ']');
        return outp.ToString();
    }
}
