using BonsaiLLM.Core.Chat;
using BonsaiLLM.Core.Settings;

namespace BonsaiLLM.Core.Sessions;

/// <summary>落盘的一条消息；只存必要字段，流式状态不进文件。</summary>
public sealed class StoredMessage
{
    public ChatRole Role { get; set; }

    public string Text { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>随消息保存的图片文件名（位于 images 目录）；null 表示纯文本。</summary>
    public string? ImageFile { get; set; }

    /// <summary>这条助手回复是否来自特化检测，加载时据此恢复坐标叠加。</summary>
    public bool IsDetection { get; set; }
}

/// <summary>
/// 一个会话。文件在 %AppData%\BonsaiLLM\sessions\{id}.json，
/// 包含消息、所用模型、上下文模式与采样参数快照。
/// </summary>
public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "新会话";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>会话创建时使用的模型，切模型不影响已有会话。</summary>
    public string ModelPath { get; set; } = "";

    public string ModelName { get; set; } = "";

    public ContextMode ContextMode { get; set; } = ContextMode.Fresh;

    public List<StoredMessage> Messages { get; set; } = [];
}
