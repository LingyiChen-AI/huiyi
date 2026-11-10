using System.Text;

namespace HuiYi;

/// Bounded map/reduce over original evidence ids. Nothing is persisted until the root succeeds.
internal static class NotesBatch
{
    internal const int MaxPromptChars = 24000;
    private const int MaxResultChars = 6000;
    private const string Output = "\nKeep the entire JSON under 4500 characters (hard limit 6000), including all keys. Use short phrases and fewer items as needed, retaining every template section and original evidence ids.\n";
    private const string Merge = "\nOrdered partial notes (JSON), earliest first. Merge these into full meeting notes. Preserve original L evidence ids and seconds from the original meeting start, never rebase times. Preserve decisions, owners, deadlines and every template section.\n";

    internal static async Task<MeetingNotes> Generate(IReadOnlyList<TranscriptLine> lines, string language,
        IReadOnlyDictionary<int, string>? names, NoteTemplate template, DateTime start, Func<string, Task<string>> chat, Func<string, Task<string>>? finalChat = null)
    {
        var skeleton = NotesPrompt.Build(new MeetingNotes(), lines, 0, language, true, names, template, start, "") + Output;
        // Two maximum-size child replies must fit at every reduction level.
        var capacity = MaxPromptChars - skeleton.Length - Merge.Length;
        if (capacity < 2 * MaxResultChars + 2) throw new AiException(AiFailure.Parse);
        var numbers = SpeakerLabels.Numbers(lines);
        var chunks = new List<string>();
        var buffer = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var who = line.Speaker == "me" ? "你" : numbers.TryGetValue(line.Id, out var n)
                ? names != null && names.TryGetValue(n, out var name) ? name : $"发言人{n}"
                : numbers.Count == 0 ? "对方" : "未识别";
            var prefix = $"[L{i + 1} {TranscriptExport.Clock(line.T)} {who}] ";
            if (prefix.Length + 2 >= capacity) throw new AiException(AiFailure.Parse);
            var offset = 0;
            while (true)
            {
                var room = capacity - buffer.Length - prefix.Length - 1;
                if (room < 2)
                {
                    chunks.Add(buffer.ToString(0, buffer.Length - 1));
                    buffer.Clear();
                    continue;
                }
                var count = Math.Min(room, line.Src.Length - offset);
                // Keep UTF-16 surrogate pairs intact across requests.
                if (count > 0 && offset + count < line.Src.Length && char.IsHighSurrogate(line.Src[offset + count - 1])) count--;
                buffer.Append(prefix).Append(line.Src, offset, count).Append('\n');
                offset += count;
                if (offset < line.Src.Length)
                {
                    chunks.Add(buffer.ToString(0, buffer.Length - 1));
                    buffer.Clear();
                }
                if (offset >= line.Src.Length) break;
            }
        }
        if (buffer.Length > 0) chunks.Add(buffer.ToString(0, buffer.Length - 1));
        var times = lines.Select(l => l.T).ToList();
        var failed = 0;
        async Task<string> Request(string prompt, bool root)
        {
            if (prompt.Length > MaxPromptChars) throw new AiException(AiFailure.Parse);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (Volatile.Read(ref failed) != 0) throw new AiException(AiFailure.Parse);
                    var raw = await (root ? finalChat ?? chat : chat)(prompt);
                    var parsed = NotesPrompt.Parse(raw, times, lines[0].T, template);
                    if ((root || raw.Length <= MaxResultChars) && parsed != null && NotesPrompt.Written(parsed, language)) return raw;
                }
                catch (AiException e) when (attempt == 0 && e.Failure is AiFailure.Timeout or AiFailure.Network or AiFailure.Parse) { }
                if (attempt == 0) await Task.Delay(250);
            }
            throw new AiException(AiFailure.Parse);
        }
        async Task<List<string>> Run(IReadOnlyList<string> inputs, bool merge, bool root)
        {
            var results = new string[inputs.Count];
            // Fixed workers bound both active requests and task allocation for very long meetings.
            var index = -1;
            await Task.WhenAll(Enumerable.Range(0, Math.Min(2, inputs.Count)).Select(async _ =>
            {
                int i;
                while (Volatile.Read(ref failed) == 0 && (i = Interlocked.Increment(ref index)) < inputs.Count)
                {
                    var prompt = NotesPrompt.Build(new MeetingNotes(), lines, 0, language, true, names, template, start,
                        merge ? Merge + inputs[i] : inputs[i]);
                    try { results[i] = await Request(root ? prompt : prompt + Output, root); }
                    catch
                    {
                        Volatile.Write(ref failed, 1);
                        throw;
                    }
                }
            }));
            return results.ToList();
        }
        var level = await Run(chunks, false, chunks.Count == 1);
        while (level.Count > 1)
        {
            var groups = new List<string>();
            for (var i = 0; i < level.Count; i += 2)
                groups.Add(string.Join("\n", level.Skip(i).Take(2)));
            level = await Run(groups, true, groups.Count == 1);
        }
        return NotesPrompt.Parse(level[0], times, lines[0].T, template)!;
    }
}
