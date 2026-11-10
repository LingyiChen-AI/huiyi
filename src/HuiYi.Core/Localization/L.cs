#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace HuiYi.Localization;

/// Shared by the desktop client and installer. Resource keys are the original UI copy.
public sealed class L : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new ResourceManager("HuiYi.Localization.Strings", typeof(L).Assembly);
    private static readonly string SystemLanguage = CultureInfo.CurrentUICulture.Name;
    public static L Instance { get; } = new L();
    public static string Language { get; private set; } = "zh-CN";
    public event PropertyChangedEventHandler? PropertyChanged;
    public static event Action? Changed;

    public string this[string text] => T(text);

    public static string Resolve(string? selected, string? system = null)
    {
        var code = string.IsNullOrEmpty(selected) || selected == "auto"
            ? system ?? SystemLanguage : selected;
        code = (code ?? "en").ToLowerInvariant();
        if (code.StartsWith("zh"))
            return code.Contains("hant") || code.EndsWith("tw") || code.EndsWith("hk") || code.EndsWith("mo") ? "zh-TW" : "zh-CN";
        if (code.StartsWith("ja")) return "ja";
        if (code.StartsWith("ko")) return "ko";
        return "en";
    }

    public static void SetLanguage(string? selected)
    {
        var next = Resolve(selected);
        if (next == Language) return;
        Language = next;
        // UI culture only: changing the interface must not change meeting timestamps or AI output language.
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(next);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(next);
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    public static string For(string language, string text, params object?[] args)
    {
        string? translated = null;
        if (language != "zh-CN")
#if NETFRAMEWORK
            translated = InstallerResources.GetOrAdd(language, code =>
                new ResourceSet(typeof(L).Assembly.GetManifestResourceStream("HuiYi.Localization.Strings." + code + ".resources")!)).GetString(text);
#else
            translated = Resources.GetString(text, CultureInfo.GetCultureInfo(language));
#endif
        var format = translated ?? text;
        return args.Length == 0 ? format : string.Format(CultureInfo.CurrentCulture, format, args);
    }

    public static string T(string text) => F(text);

    public static string F(string text, params object?[] args)
    {
        if (capture == null) return For(Language, text, args);
        var token = "\uE000" + capture.Id + ":" + capture.Messages.Count + "\uE001";
        capture.Messages.Add(new Message(text, args, token));
        return token;
    }

#if NETFRAMEWORK
    // The downloadable installer is one EXE. Its language resources are embedded in that EXE,
    // whereas the installed desktop app uses normal .NET satellite resource assemblies.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ResourceSet> InstallerResources = new();
#endif

    // A code-created label captures its message and argument values once. Re-evaluating arbitrary
    // UI expressions on a language change would read mutated loop variables or discard draft edits.
    [ThreadStatic] private static CaptureContext? capture;
    private sealed class CaptureContext
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly List<Message> Messages = new();
    }
    private sealed class Message
    {
        public readonly string Key;
        public readonly object?[] Args;
        public readonly string Value;
        public Message(string key, object?[] args, string value) { Key = key; Args = args; Value = value; }
    }

    public static Func<string> Capture(Func<string> expression)
    {
        var previous = capture;
        var context = new CaptureContext();
        capture = context;
        string value;
        try { value = expression(); }
        finally { capture = previous; }
        string Render(string template)
        {
            var result = template;
            foreach (var message in context.Messages)
            {
                if (!result.Contains(message.Value)) continue;
                var arguments = message.Args.Select(arg => arg is string text ? Render(text) : arg).ToArray();
                result = result.Replace(message.Value, For(Language, Render(message.Key), arguments));
            }
            return result;
        }
        return () => Render(value);
    }
}
