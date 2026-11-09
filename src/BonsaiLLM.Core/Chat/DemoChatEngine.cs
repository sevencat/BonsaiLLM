using System.Runtime.CompilerServices;
using System.Text;
using BonsaiLLM.Core.Models;

namespace BonsaiLLM.Core.Chat;

/// <summary>
/// M1 占位引擎：不加载任何模型，把一段 Markdown 按小块吐出来，
/// 用来验证流式渲染、取消与布局。M2 由 BonsaiLLM.Llama 替换。
/// </summary>
public sealed class DemoChatEngine : IChatEngine
{
    private const string SampleReply = """
## 流式 Markdown 自检

段落、**粗体**、`行内代码`、[链接](https://example.com) 都应该正常。

| 项目 | 值 |
|---|---|
| 架构 | qwen3vl |
| 坐标 | 0–1000 归一化 |
| 后端 | CUDA12 / Vulkan |

```csharp
var layers = 99; // -1 或 99 都表示全部层进显存
```

行内公式 $E = mc^2$，块级公式：

$$
\int_0^1 x^2\,dx = \frac{1}{3}
$$

```mermaid
flowchart LR
  A[提问] --> B[回答]
```

- 列表项一
- 列表项二
- 列表项三
""";

    public string DisplayName => "Demo 引擎（M1 假数据）";

    public ModelDescriptor? Loaded { get; private set; }

    public bool IsLoaded => true;

    public Task LoadAsync(
        ModelDescriptor model,
        ModelLoadOptions options,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        Loaded = model;
        progress?.Report(1f);
        return Task.CompletedTask;
    }

    public Task UnloadAsync()
    {
        Loaded = null;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> history,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reply = BuildReply(history);

        foreach (var chunk in Chunk(reply, 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(12, cancellationToken).ConfigureAwait(false);
            yield return new ChatDelta(chunk, false);
        }

        yield return ChatDelta.Done(new UsageInfo(128, reply.Length / 2));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string BuildReply(IReadOnlyList<ChatMessage> history)
    {
        var lastUser = history.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(lastUser))
        {
            return SampleReply;
        }

        return $"你说的是：**{lastUser.Trim()}**\n\n{SampleReply}";
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        var buffer = new StringBuilder(size);
        foreach (var ch in text)
        {
            buffer.Append(ch);
            if (buffer.Length < size)
            {
                continue;
            }

            yield return buffer.ToString();
            buffer.Clear();
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }
}
