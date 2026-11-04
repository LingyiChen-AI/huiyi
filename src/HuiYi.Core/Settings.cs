using System.Text.Json;

namespace HuiYi;

public enum DisplayMode { Notes, Subtitles }
public enum SpeakKey { RightCtrl, RightAlt }

/// Every setting, saved as %APPDATA%\HuiYi\settings.json. Never holds an API key.
public sealed class AppSettings
{
    /// The AI services set up (notes, sentence translation, speaker names), and the one in use.
    public List<AiProvider> Providers { get; set; } = [];
    public string? ActiveProviderId { get; set; }

    public AiProvider? ActiveProvider() =>
        Providers.FirstOrDefault(p => p.Id == ActiveProviderId) ?? Providers.FirstOrDefault();

    /// "auto" follows the system languages; otherwise a target code ("zh-CN", "en"…).
    public string Target { get; set; } = "auto";
    public DisplayMode Display { get; set; } = DisplayMode.Notes;
    /// The notes window shows in screen shares (with a badge); off by default.
    public bool NotesCapturable { get; set; }
    /// Translate the subtitles. Off by default: many meetings only need the words and the notes,
    /// and with it off nothing is sent to a translator.
    public bool TranslationOn { get; set; }
    /// Tell the other side's voices apart (发言人 1、发言人 2…). On by default.
    public bool SpeakerVoices { get; set; } = true;
    public double SubtitleFontSize { get; set; } = 26;
    /// The notes template new meetings use (built-in id or one of the user's).
    public string DefaultTemplateId { get; set; } = NoteTemplates.DefaultId;
    /// "auto": the notes follow the subtitles' target language.
    public string NotesLanguage { get; set; } = "auto";

    public bool SpeakEnabled { get; set; }
    public SpeakKey SpeakKey { get; set; } = SpeakKey.RightCtrl;
    /// What I speak into the room: "auto" is the language the other side last spoke.
    public string SpeakTarget { get; set; } = "auto";
    /// The Windows voice for speak-back, per language code; empty picks the first that fits.
    public Dictionary<string, string> Voices { get; set; } = new();
    /// Also play what is spoken into the room on my own speakers.
    public bool SpeakMonitor { get; set; }

    public bool FollowMeetingApps { get; set; } = true;
    public MeetingEndWatch.Silence SilenceEnd { get; set; } = MeetingEndWatch.Silence.Prompt;
    public int SilenceMinutes { get; set; } = 10;

    public bool LaunchAtLogin { get; set; }
    public bool OnboardingDone { get; set; }

    private static string File => Path.Combine(Paths.Roaming, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(File), Json.Options) ?? new();
        }
        catch
        {
            Log.Debug("settings: unreadable, using defaults");
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Roaming);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Json.Options));
        }
        catch
        {
            Log.Debug("settings: save failed");
        }
    }
}
