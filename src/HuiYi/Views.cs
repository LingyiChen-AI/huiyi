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
    private string who = "对方";
    private Brush whoBrush = Theme.Grey2;

    public string Src { get => src; set => Set(ref src, value); }
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
    }

    public void Update(TranscriptLine l, string label, int? number, bool showDst = true)
    {
        Src = l.Src;
        Dst = showDst ? l.Dst : null;
        Who = label;
        WhoBrush = IsMe ? Theme.Primary : number is { } n ? Theme.Speaker(n) : Theme.Grey2;
    }

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
            vm.Update(l, SpeakerLabels.Who(l, numbers, names), numbers.TryGetValue(l.Id, out var n) ? n : null, showDst);
        }
    }
}

