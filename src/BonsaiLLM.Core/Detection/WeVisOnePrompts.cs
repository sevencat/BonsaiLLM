namespace BonsaiLLM.Core.Detection;

/// <summary>WeVisOne-VL 官方 prompts.py 的提示词模板（referring 检测）。</summary>
public static class WeVisOnePrompts
{
    public const string System = "You are a helpful assistant.";

    /// <summary>Locate the instances that match the following description: xxx.</summary>
    public static string Referring(string phrase)
    {
        var target = (phrase ?? string.Empty).Trim().TrimEnd('.', '?', '!', '。', '？', '！');
        return "Locate the instances that match the following description: "
            + (target.Length == 0 ? "object" : target)
            + ".";
    }
}
