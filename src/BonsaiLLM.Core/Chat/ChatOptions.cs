namespace BonsaiLLM.Core.Chat;

public sealed record ChatOptions
{
    public double Temperature { get; init; } = 0.7;
    public double TopP { get; init; } = 0.9;
    public int TopK { get; init; } = 40;
    public double RepeatPenalty { get; init; } = 1.1;
    public int MaxTokens { get; init; } = -1;

    /// <summary>本轮随消息发送的图片（已预处理成 JPEG 字节）；null 表示纯文本。</summary>
    public byte[]? ImageBytes { get; init; }
}
