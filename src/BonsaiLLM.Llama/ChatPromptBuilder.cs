using System.Text;
using BonsaiLLM.Core.Chat;

namespace BonsaiLLM.Llama;

/// <summary>
/// 把消息序列成 ChatML 提示词（Qwen / SmolLM2 / WeVisOne 都用这套标记）。
/// GGUF 内嵌的 Jinja 模板暂不执行，先固定 ChatML，见 misc.md。
/// </summary>
internal static class ChatPromptBuilder
{
    public static string BuildPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append("<|im_start|>")
                .Append(RoleName(message.Role))
                .Append('\n')
                .Append(message.Text.Trim())
                .Append("<|im_end|>\n");
        }

        builder.Append("<|im_start|>assistant\n");
        return builder.ToString();
    }

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        _ => "assistant",
    };
}
