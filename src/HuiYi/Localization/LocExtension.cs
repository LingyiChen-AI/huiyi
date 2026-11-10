using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using HuiYi.Localization;

namespace HuiYi.App;

/// XAML strings remain bound, including strings inside templates and styles.
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding { Source = new UiText(() => L.T(Key)), Path = new PropertyPath(nameof(UiText.Value)), Mode = BindingMode.OneWay }
            .ProvideValue(serviceProvider);
}

public sealed class UiText : INotifyPropertyChanged
{
    private readonly Func<string> read;
    public string Value => read();
    public event PropertyChangedEventHandler? PropertyChanged;

    public UiText(Func<string> expression)
    {
        read = L.Capture(expression);
        PropertyChangedEventManager.AddHandler(L.Instance, Refresh, "Item[]");
    }

    private void Refresh(object? sender, PropertyChangedEventArgs e) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));

    public static void Set(DependencyObject target, string property, Func<string> expression)
    {
        var descriptor = DependencyPropertyDescriptor.FromName(property, target.GetType(), target.GetType());
        if (descriptor == null) throw new InvalidOperationException($"Unknown localizable property {target.GetType().Name}.{property}");
        BindingOperations.SetBinding(target, descriptor.DependencyProperty,
            new Binding(nameof(Value)) { Source = new UiText(expression), Mode = BindingMode.OneWay });
    }

    public static T Bind<T>(T target, params (string Property, Func<string> Expression)[] messages) where T : DependencyObject
    {
        foreach (var message in messages) Set(target, message.Property, message.Expression);
        return target;
    }
}
