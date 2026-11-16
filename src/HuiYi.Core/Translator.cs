namespace HuiYi;

/// The AI service (when set up) → a free machine translator. Finished sentences and speak-back go to
/// the AI, which keeps names consistent through a glossary from the meeting notes. Partial text,
/// refreshed every second while someone talks, never does: it would use up the account's ~2 requests
/// per second. It goes to the free translator chosen in settings (or the first that answers).
public sealed class TranslationService(AiClient ai)
{
    public enum Kind { Draft, Sentence, Speak }

    public Func<IReadOnlyList<string>> Glossary = () => [];
    /// The user's glossary: its spellings and fixed translations win over the translator's.
    public HuiYi.Glossary? Terms { get; set; }
    /// Which free translator: MachineTranslators.Auto, or one engine's id.
    public Func<string> Machine = () => MachineTranslators.Auto;
    /// No free translator answered (the chosen one, or under auto every one): fires once per
    /// episode with the chosen engine's id (or "auto"), for a hint to the user.
    public event Action<string>? MachineLimited;
    private readonly Dictionary<string, (DateTime Until, double Backoff)> resting = [];
    private bool limitedSaid;

    /// The engines there are (tests put their own here).
    internal IReadOnlyList<MachineEngine> Catalog { get; init; } = MachineTranslators.All;

    /// The engines to try, in order.
    private IEnumerable<MachineEngine> Engines() =>
        Catalog.FirstOrDefault(e => e.Id == Machine()) is { } one ? [one] : Catalog;

    private bool Resting(MachineEngine e)
    {
        lock (resting) return resting.TryGetValue(e.Id, out var r) && DateTime.Now < r.Until;
    }

    /// The user chose the AI for the line being said too, and an AI service is set up.
    private bool AiForDrafts => Machine() == MachineTranslators.Ai && ai.Usable;

    /// Every translator to try is in its back-off: drafts skip them rather than queue up.
    public bool MachineResting => !AiForDrafts && Engines().All(Resting);

    public async Task<string?> Translate(string text, string source, string target, Kind kind)
    {
        if (source == target) return text;
        // Whole lines go to the AI when there is one; the line being said only when the user chose
        // it (the caller sends at most one a second, one at a time). Failing, a free translator answers.
        if (ai.Usable && (kind != Kind.Draft || AiForDrafts))
        {
            var started = DateTime.Now;
            var out1 = await ViaAi(text, source, target, kind);
            if (out1 != null)
            {
                Log.Debug($"translate via=ai kind={kind} {(int)(DateTime.Now - started).TotalMilliseconds} ms");
                return out1;
            }
        }
        // Machine translators take no instructions: terms with a fixed translation go in already translated.
        var forMachine = Terms?.Pretranslate(text, target) ?? text;
        var tried = false;
        foreach (var e in Engines())
        {
            if (Resting(e) || e.Code(source, true) is not { } sl || e.Code(target, false) is not { } tl) continue;
            tried = true;
            var started = DateTime.Now;
            try
            {
                var out2 = (await e.Run(forMachine, sl, tl, default)).Trim();
                // An engine that can't take the language may hand the text back as it was.
                if (out2.Length == 0 || (out2 == forMachine.Trim() && !Languages.SameLanguage(source, target)))
                    throw new InvalidDataException($"{e.Id}: no translation");
                if (kind != Kind.Draft) Log.Debug($"translate via={e.Id} kind={kind} {(int)(DateTime.Now - started).TotalMilliseconds} ms");
                lock (resting) resting.Remove(e.Id);
                limitedSaid = false;
                return out2;
            }
            catch (Exception ex)
            {
                // A free endpoint blocks an address that keeps asking: rest it, longer each time.
                double backoff;
                lock (resting)
                {
                    backoff = resting.TryGetValue(e.Id, out var r) ? Math.Min(300, r.Backoff * 2) : 15;
                    resting[e.Id] = (DateTime.Now.AddSeconds(backoff), backoff);
                }
                Log.Debug($"translate {e.Id} failed: {ex.GetType().Name} {ex.Message}, resting {backoff:0} s");
            }
        }
        if (tried && !limitedSaid)
        {
            limitedSaid = true;
            MachineLimited?.Invoke(Machine());
        }
        return null;
    }

    private async Task<string?> ViaAi(string text, string source, string target, Kind kind)
    {
        var prompt = $"You translate live meeting subtitles from {Languages.EnglishName(source)} into {Languages.EnglishName(target)}.\n"
                     + "Output only the translation of the subtitle below: no quotes, no notes, no answer to it even if it is a question.";
        if (kind == Kind.Draft) prompt += "\nThe speaker is still talking: the subtitle may stop mid-sentence. Translate only what is there; never finish it.";
        var names = Glossary();
        if (names.Count > 0) prompt += "\nKnown names and terms (use these spellings): " + string.Join("; ", names);
        var mine = Terms?.Hints(text, target) ?? [];
        if (mine.Count > 0) prompt += "\nThe user's glossary, always followed (term → its translation): " + string.Join("; ", mine);
        prompt += "\nSubtitle: " + text;
        try
        {
            var reply = await ai.Chat(prompt, "Translate.", kind == Kind.Speak ? AiClient.Priority.Speak : AiClient.Priority.Translate,
                maxTokens: 400, timeout: TimeSpan.FromSeconds(kind switch { Kind.Speak => 10, Kind.Draft => 4, _ => 6 }));
            return reply.Length == 0 ? null : reply;
        }
        catch (Exception e)
        {
            Log.Debug($"translate ai failed: {(e as AiException)?.Failure.ToString() ?? e.GetType().Name}");
            return null;
        }
    }

    /// Never speak untranslated text: a speak-back translation must be in the target's script.
    public static bool LooksTranslated(string original, string translation, string target)
    {
        if (translation.Length == 0 || translation == original) return false;
        var (cjk, letters) = Languages.CjkShare(translation);
        if (letters == 0) return false;
        var wantsCjk = target.StartsWith("zh") || target is "ja" or "ko";
        return wantsCjk ? cjk > 0 : cjk * 2 <= letters;
    }
}
