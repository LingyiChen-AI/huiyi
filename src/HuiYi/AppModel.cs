using System.Windows;
using System.Windows.Threading;
using HuiYi.Audio;
using HuiYi.Speak;
using HuiYi.Speech;

namespace HuiYi.App;

/// The product name shown to users. Kept in one place: it is not "Cicada"/"知了", which belong to
/// the macOS project this one follows.
public static class Brand
{
    public const string Name = "会译";
    public const string Tagline = "会议实时字幕与纪要";
}

/// Wires the engines together and owns the windows. Engines raise events on worker threads; every
/// handler here hops to the UI thread first.
public sealed class AppModel
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public SessionStore Store { get; } = new();
    public AiClient Ai { get; } = new();
    public ModelStore Models { get; } = new();
    public TranslationService Translator { get; }
    public NotesEngine Notes { get; }
    public MeetingController? Meeting { get; private set; }
    public SpeakEngine? Speak { get; private set; }
    public Recognizer? Recognizer { get; private set; }
    /// The user's own notes templates (built-in ones are in `NoteTemplates.BuiltIn`).
    public List<NoteTemplate> MyTemplates { get; } = NoteTemplates.LoadMine();

    public IReadOnlyList<NoteTemplate> Templates => NoteTemplates.All(MyTemplates);

    /// The template a meeting's notes follow: its own pick, else the default.
    public NoteTemplate TemplateFor(Session? s) => NoteTemplates.Find(s?.TemplateId ?? Settings.DefaultTemplateId, MyTemplates);

    public void SaveTemplates()
    {
        NoteTemplates.SaveMine(MyTemplates);
        // A deleted default falls back to the general template.
        if (Templates.All(t => t.Id != Settings.DefaultTemplateId))
        {
            Settings.DefaultTemplateId = NoteTemplates.DefaultId;
            Settings.Save();
        }
        StateChanged?.Invoke();
    }

    public event Action? StateChanged;
    public event Action<MeetingController.Subtitle>? Subtitle;
    public event Action<string>? LineChanged;
    public event Action? NotesChanged;
    public event Action? SpeakChanged;
    public event Action<string>? Toast;
    public event Action<MeetingEndWatch.Pending?>? EndPending;

    private readonly Dispatcher ui = Application.Current.Dispatcher;
    private Task? loading;
    private Native.KeyWatch? keyWatch;

    public bool IsListening => Meeting?.IsListening == true;
    public string? LiveSessionId => Meeting?.SessionId;

    public static IReadOnlyList<string> SystemLanguages()
    {
        try { return Windows.System.UserProfile.GlobalizationPreferences.Languages.ToArray(); }
        catch { return [System.Globalization.CultureInfo.CurrentUICulture.Name]; }
    }

    public AppModel()
    {
        Ai.Provider = Settings.ActiveProvider;
        Ai.Refresh();
        Translator = new TranslationService(Ai);
        Notes = new NotesEngine(Ai, Store) { Template = TemplateFor };
        Ai.Changed += () => Ui(() => StateChanged?.Invoke());
        Notes.Changed += () => Ui(() => NotesChanged?.Invoke());
        Translator.GoogleLimited += () => Ui(() => Toast?.Invoke(Ai.Usable
            ? $"Google 翻译暂时被限流，整句译文改由 {Ai.Current?.Name} 提供"
            : "Google 翻译暂时被限流，字幕先只显示原文。在「设置 › AI 服务」接入一个大模型即可继续翻译"));
        if (Ai.State == AiClient.KeyState.Unchecked) _ = Ai.Check();
        if (Models.Ready) loading = Task.Run(Load);
        ApplySpeakKey();
    }

    private void Ui(Action a)
    {
        if (ui.CheckAccess()) a();
        else ui.BeginInvoke(a);
    }

    /// Loads the recognizer (~1 s for the 230 MB model) and builds the engines that need it.
    private void Load()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rec = new Recognizer(Models);
        var listen = new ListenEngine(rec, Models);
        // Voiceprints load on first use; without the model, lines just stay 对方.
        Voiceprints? prints = null;
        var printsFailed = false;
        var tagger = new SpeakerTagger(() =>
        {
            if (prints != null || printsFailed) return prints;
            try { prints = new Voiceprints(Models); }
            catch (Exception e)
            {
                printsFailed = true;
                Log.Debug($"voiceprint model failed to load: {e.Message}");
            }
            return prints;
        });
        var meeting = new MeetingController(Settings, Store, Translator, Notes, listen, SystemLanguages,
            tagger, new SpeakerNamer(Ai, Store));
        var speak = new SpeakEngine(Settings, rec, Translator, () => meeting.TheirLanguage, SystemLanguages, Ai);
        meeting.SubtitleChanged += s => Ui(() => Subtitle?.Invoke(s));
        meeting.LineChanged += id => Ui(() => LineChanged?.Invoke(id));
        meeting.StateChanged += () => Ui(() => StateChanged?.Invoke());
        meeting.Error += m => Ui(() => Toast?.Invoke(m));
        meeting.EndPending += p => Ui(() => EndPending?.Invoke(p));
        speak.StateChanged += () => Ui(() => SpeakChanged?.Invoke());
        speak.Problem += m => Ui(() => Toast?.Invoke(m));
        speak.Spoke += (src, sl, dst, dl) => meeting.AddMine(src, sl, dst, dl);
        Recognizer = rec;
        Meeting = meeting;
        Speak = speak;
        Log.Debug($"app: recognizer loaded in {watch.ElapsedMilliseconds} ms");
    }

    /// Makes sure the engines exist: false when the models still need downloading.
    public async Task<bool> EnsureEngines()
    {
        if (Meeting != null) return true;
        if (!Models.Ready) return false;
        loading ??= Task.Run(Load);
        try { await loading; }
        catch (Exception e)
        {
            loading = null;
            Log.Debug($"app: recognizer failed {e.Message}");
            Toast?.Invoke("识别模型加载失败：" + e.Message);
            return false;
        }
        return Meeting != null;
    }

    /// Developer switch (--feed): listen to this audio file instead of the PC, at real speed.
    public string? FeedFile { get; set; }

    public async Task<bool> StartListening()
    {
        if (IsListening) return true;
        if (!await EnsureEngines()) return false;
        Meeting!.Start(FeedFile != null ? new FileSource(FeedFile) : new LoopbackSource());
        StateChanged?.Invoke();
        return true;
    }

    public void StopListening()
    {
        Meeting?.Stop();
        StateChanged?.Invoke();
    }

    public void KeepGoing() => Meeting?.KeepGoing();

    /// The translation switch (panel, tray menu, settings): one place, so they all agree.
    public void SetTranslation(bool on)
    {
        if (Settings.TranslationOn == on) return;
        Settings.TranslationOn = on;
        Settings.Save();
        if (on) Meeting?.TranslationResumed();
        StateChanged?.Invoke();
    }

    /// Who said a line of the live meeting ("发言人 2", a name, or 对方) and their number, if shown.
    public (string Who, int? Number) SpeakerOf(string? lineId)
    {
        if (lineId == null || LiveSessionId is not { } id || Store.Snapshot(id) is not { } s) return ("对方", null);
        var line = s.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null) return ("对方", null);
        var numbers = SpeakerLabels.Numbers(s.Lines);
        return (SpeakerLabels.Who(line, numbers, SpeakerLabels.Names(s)), numbers.TryGetValue(lineId, out var n) ? n : null);
    }

    public void SaveSettings()
    {
        Settings.Save();
        ApplySpeakKey();
        StateChanged?.Invoke();
    }

    /// The push-to-talk key is watched only while speaking is on.
    private void ApplySpeakKey()
    {
        var key = Settings.SpeakKey == SpeakKey.RightAlt ? Native.VK_RMENU : Native.VK_RCONTROL;
        if (!Settings.SpeakEnabled)
        {
            keyWatch?.Dispose();
            keyWatch = null;
            return;
        }
        if (keyWatch == null)
        {
            keyWatch = new Native.KeyWatch(key);
            keyWatch.Down += () => _ = PressSpeak();
            keyWatch.Up += () => Speak?.Release();
        }
        keyWatch.Key = key;
    }

    private async Task PressSpeak()
    {
        if (!await EnsureEngines()) { Toast?.Invoke("请先完成识别模型下载"); return; }
        Speak?.Press();
    }

    public void Shutdown()
    {
        keyWatch?.Dispose();
        if (IsListening) Meeting!.Stop();
        Store.Flush();
    }
}
