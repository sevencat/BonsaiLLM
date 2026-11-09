namespace BonsaiLLM.Core.Chat;

/// <summary>一次流式增量：文本片段，或结束标记（带可选用量）。</summary>
public readonly record struct ChatDelta(string Text, bool IsDone, UsageInfo? Usage = null)
{
    public static ChatDelta Done(UsageInfo? usage = null) => new(string.Empty, true, usage);
}
