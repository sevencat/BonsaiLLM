namespace BonsaiLLM.App.ViewModels;

/// <summary>
/// 待发送的文本附件：只保留文件名、大小与解析出的文本，
/// 发送时按「【附件：名字】+ 代码块」拼进消息正文（本地模型读不了二进制）。
/// </summary>
public sealed class PendingFileViewModel(string name, string content, long sizeInBytes)
{
    public string Name { get; } = name;

    public string Content { get; } = content;

    public string SizeText { get; } = FormatSize(sizeInBytes);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}
