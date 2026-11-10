using System.ComponentModel;

namespace HuiYi.App;

public abstract class LibraryNode : INotifyPropertyChanged
{
    private bool expanded = true;
    private bool selected;
    public abstract bool IsGroup { get; }
    public bool IsExpanded
    {
        get => expanded;
        set
        {
            if (expanded == value) return;
            expanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
        }
    }
    public bool IsSelected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SessionRow(string id, string title, string meta) : LibraryNode
{
    public override bool IsGroup => false;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Meta { get; } = meta;
    public bool Archived { get; init; }
}

public sealed class SessionGroupNode(string id, string title, IReadOnlyList<SessionRow> children) : LibraryNode
{
    public override bool IsGroup => true;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public IReadOnlyList<SessionRow> Children { get; } = children;
    public int Count => Children.Count;
}
