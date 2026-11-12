using System.Text.Json.Nodes;

namespace HuiYi;

/// One AI service the user set up: a platform (preset), its OpenAI-compatible base URL and the model
/// to use. The key is not here: it lives in its own DPAPI file (`AIKey`), never in settings.
public sealed class AiProvider
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    /// The preset's id ("ling", "bailian"…), "custom" for anything else.
    public string Preset { get; set; } = "custom";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";

    public ProviderPreset Info => ProviderPreset.Find(Preset);
}

/// A platform we know: where its OpenAI-compatible API lives, models worth suggesting, and the few
/// request details that differ (how to turn deep thinking off, how fast requests may start).
/// Suggested models are a starting point; 获取模型列表 asks the platform itself.
public sealed record ProviderPreset(
    string Id,
    string Name,
    string BaseUrl,
    string[] Models,
    string KeyUrl,
    Func<JsonObject>? Extras = null,
    double Spacing = 0.25,
    bool KeyOptional = false,
    string? Hint = null,
    string? DefaultApiPath = null)
{
    /// Ling's gateway answers fact-like prompts with a search essay; its replies are checked for that.
    public bool ChecksHijack => Id == "ling";
    /// Ming's voices (natural speech for speak-back) come with an Ant Ling key only.
    public bool HasMingVoices => Id == "ling";

    /// Accept a server address as well as a full compatible API URL. Explicit paths stay intact.
    public string NormalizeBaseUrl(string value)
    {
        var url = value.Trim().TrimEnd('/');
        if (DefaultApiPath != null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0)
            return url + DefaultApiPath;
        return url;
    }

    private static JsonObject ThinkingOff() => new() { ["thinking"] = new JsonObject { ["type"] = "disabled" } };

    // Addresses checked against each platform (all answer /models, 401 without a key); models taken
    // from the platforms' current lists (UCloud's aggregated list mirrors the vendors' names).
    public static readonly ProviderPreset[] All =
    [
        new("ling", "蚂蚁百灵 Ling", "https://api.ant-ling.com/v1", ["Ling-3.1-flash", "Ling-3.0-flash"],
            "https://chat.ant-ling.com/platform/api", ThinkingOff, Spacing: 0.6,
            Hint: "额外支持 Ming 自然语音（同声传译播报）"),
        new("bailian", "阿里云百炼（通义千问）", "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ["qwen-flash", "qwen-plus", "qwen3.7-plus", "qwen3.8-max"],
            "https://bailian.console.aliyun.com/",
            () => new JsonObject { ["enable_thinking"] = false }),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", ["deepseek-chat"],
            "https://platform.deepseek.com/api_keys"),
        new("ark", "火山方舟（豆包）", "https://ark.cn-beijing.volces.com/api/v3",
            ["doubao-seed-2-1-lite-260915", "doubao-seed-2-1-turbo-260628", "doubao-seed-2-1-pro-260628"],
            "https://console.volcengine.com/ark", ThinkingOff,
            Hint: "也可以填方舟控制台里以 ep- 开头的接入点 ID"),
        new("ucloud", "UCloud 模型服务（聚合）", "https://api.modelverse.cn/v1",
            ["deepseek-v4.1-flash", "qwen3.7-plus", "glm-5.3-flash", "doubao-seed-2-1-lite-260915", "kimi-k3.1-pro", "MiniMax-M3"],
            "https://console.ucloud.cn/",
            Hint: "一个 key 可用 DeepSeek、千问、GLM、豆包、Kimi 等上百个模型"),
        new("moonshot", "Kimi（月之暗面）", "https://api.moonshot.cn/v1", ["kimi-k3", "kimi-k2.6"],
            "https://platform.moonshot.cn/console/api-keys"),
        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", ["glm-5.3-flash", "glm-5.3"],
            "https://open.bigmodel.cn/usercenter/apikeys", ThinkingOff),
        new("minimax", "MiniMax", "https://api.minimaxi.com/v1", ["MiniMax-M3", "MiniMax-M2.7"],
            "https://platform.minimaxi.com/"),
        new("hunyuan", "腾讯混元", "https://api.hunyuan.cloud.tencent.com/v1", ["hunyuan-turbos-latest"],
            "https://console.cloud.tencent.com/hunyuan"),
        new("qianfan", "百度千帆（文心）", "https://qianfan.baidubce.com/v2", [],
            "https://console.bce.baidu.com/qianfan/", Hint: "填好 key 后点「获取模型列表」"),
        new("siliconflow", "硅基流动", "https://api.siliconflow.cn/v1", [],
            "https://cloud.siliconflow.cn/account/ak", Hint: "填好 key 后点「获取模型列表」"),
        new("openai", "OpenAI", "https://api.openai.com/v1", [], "https://platform.openai.com/api-keys",
            Hint: "填好 key 后点「获取模型列表」"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", [], "https://openrouter.ai/keys",
            Hint: "填好 key 后点「获取模型列表」"),
        new("ollama", "本地 Ollama", "http://localhost:11434/v1", [], "https://ollama.com/download",
            KeyOptional: true, DefaultApiPath: "/v1",
            Hint: "支持本机或局域网 Ollama。填写 http://localhost:11434 或自己的服务 URL，自动补全 /v1；点「获取模型列表」选择已下载的模型。"),
        new("custom", "自定义（OpenAI 兼容）", "", [], "",
            Hint: "任何兼容 OpenAI Chat Completions 的服务：填 Base URL、key 和模型名"),
    ];

    public static ProviderPreset Find(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[^1];

    /// A new service set up from this preset, with its first suggested model.
    public AiProvider Create() => new() { Preset = Id, Name = Name, BaseUrl = BaseUrl, Model = Models.FirstOrDefault() ?? "" };
}
