using HuiYi.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HuiYi.App;

/// One transcript line as the windows show it.
public sealed class LineVM : INotifyPropertyChanged
{
    public string Id { get; }
    public string Time { get; }
    public bool IsMe { get; }
    private string src = "";
    private string? dst;
    private string who = L.T("发言人");
    private Brush whoBrush = Theme.Grey2;

    /// The voice behind the line and its speaker number, once it has one: then the name can be changed.
    public int? Voice { get; private set; }
    private int? number;
    public int? Number
    {
        get => number;
        private set
        {
            Set(ref number, value);
            OnPropertyChanged(nameof(Renamable));
        }
    }
    public bool Renamable => !IsMe && Voice != null && number != null;
    public bool HasName { get; private set; }

    public string Src { get => src; set => Set(ref src, value); }
    private IReadOnlyList<LineFix>? fixes;
    /// Words the glossary corrected in Src.
    public IReadOnlyList<LineFix>? Fixes { get => fixes; set => Set(ref fixes, value); }
    public string Who { get => who; set => Set(ref who, value); }
    public Brush WhoBrush { get => whoBrush; set => Set(ref whoBrush, value); }
    public string? Dst
    {
        get => dst;
        set
        {
            Set(ref dst, value);
            OnPropertyChanged(nameof(DstVisibility));
        }
    }
    public Visibility DstVisibility => string.IsNullOrEmpty(dst) ? Visibility.Collapsed : Visibility.Visible;

    /// `start`: the meeting's start, so the line shows the real time it was said.
    public LineVM(TranscriptLine l, DateTime start)
    {
        Id = l.Id;
        Time = NotesPrompt.ClockAt(start.ToLocalTime(), l.T);
        IsMe = l.Speaker == "me";
        src = l.Src;
        dst = l.Dst;
        fixes = l.Fixes?.ToList();
    }

    public void Update(TranscriptLine l, string label, int? number, bool showDst = true, bool hasName = false)
    {
        Src = l.Src;
        if (!SameFixes(fixes, l.Fixes)) Fixes = l.Fixes?.ToList();
        Dst = showDst ? l.Dst : null;
        // Speaker names and transcript text are user content. Only generated labels follow the UI.
        HasName = hasName;
        Who = IsMe ? L.T("我") : hasName ? label : number is { } speaker
            ? L.F("发言人 {0}", speaker) : L.T("发言人");
        Voice = l.Voice;
        Number = number;
        WhoBrush = IsMe ? Theme.Primary : number is { } n ? Theme.Speaker(n) : Theme.Grey2;
    }

    private static bool SameFixes(IReadOnlyList<LineFix>? a, IReadOnlyList<LineFix>? b) =>
        (a?.Count ?? 0) == (b?.Count ?? 0) && (a == null || a.Zip(b!).All(p => p.First.From == p.Second.From && p.First.To == p.Second.To));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    /// Brings `list` in step with a meeting (adds new lines, updates changed ones and every speaker
    /// label: a voice gets its number only after a few lines, and then its earlier lines show it too).
    /// `showDst`: false hides translations (the live panel with translation switched off).
    public static void Sync(ObservableCollection<LineVM> list, Session s, bool showDst = true)
    {
        var numbers = SpeakerLabels.Numbers(s.Lines);
        var names = SpeakerLabels.Names(s);
        var known = list.ToDictionary(v => v.Id);
        foreach (var l in s.Lines)
        {
            if (!known.TryGetValue(l.Id, out var vm))
            {
                vm = new LineVM(l, s.StartedAt);
                list.Add(vm);
            }
            var number = numbers.TryGetValue(l.Id, out var n) ? n : (int?)null;
            vm.Update(l, SpeakerLabels.Who(l, numbers, names), number, showDst, number is { } speaker && names.ContainsKey(speaker));
        }
    }
}

