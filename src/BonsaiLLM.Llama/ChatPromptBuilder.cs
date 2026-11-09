using System.Text;
using BonsaiLLM.Core.Chat;

namespace BonsaiLLM.Llama;

/// <summary>
/// 把消息序列成 ChatML 提示词（Qwen / SmolLM2 / WeVisOne 都用这套标记）。
/// GGUF 内嵌的 Jinja 模板暂不执行，先固定 ChatML，见 misc.md。
/// </summary>
internal static class ChatPromptBuilder
{
    /// <summary>mtmd 的媒体占位符：提示词里必须出现它，图片才会被编码进上下文。</summary>
    public const string MediaMarker = "<__media__>";

    /// <param name="withMedia">
    /// true 时在最后一条 user 消息前插入 <see cref="MediaMarker"/>；
    /// 引擎必须已经 <c>LoadMedia</c>，否则该标记会被当成普通文本。
    /// </param>
    public static string BuildPrompt(IReadOnlyList<ChatMessage> messages, bool withMedia = false)
    {
        var builder = new StringBuilder();
        var mediaAttached = false;

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            builder.Append("<|im_start|>")
                .Append(RoleName(message.Role))
                .Append('\n');

            if (withMedia
                && !mediaAttached
                && message.Role == ChatRole.User
                && IsLastUser(messages, index))
            {
                builder.Append(MediaMarker).Append('\n');
                mediaAttached = true;
            }

            builder.Append(message.Text.Trim()).Append("<|im_end|>\n");
        }

        builder.Append("<|im_start|>assistant\n");
        return builder.ToString();
    }

    private static bool IsLastUser(IReadOnlyList<ChatMessage> messages, int index)
    {
        for (var i = messages.Count - 1; i > index; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                return false;
            }
        }

        return true;
    }

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        _ => "assistant",
    };
}
