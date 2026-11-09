using System.Text.Json;
using System.Text.Json.Serialization;

namespace BonsaiLLM.Server;

/// <summary>
/// OpenAI <c>/v1/chat/completions</c> 请求的最小可用子集：
/// 只认 messages / stream / 采样参数，其余字段忽略，够客户端接上就行。
/// </summary>
public sealed class OpenAiChatRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("messages")]
    public List<OpenAiMessage>? Messages { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }

    /// <summary>content 既可能是字符串，也可能是 text / image_url 片段数组。</summary>
    public sealed class OpenAiMessage
    {
        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public JsonElement Content { get; set; }
    }
}

public sealed record OpenAiModelList(IReadOnlyList<OpenAiModel> Data)
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "list";
}

public sealed record OpenAiModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy)
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "model";
}

public sealed record OpenAiUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens)
{
    [JsonPropertyName("total_tokens")]
    public int TotalTokens => PromptTokens + CompletionTokens;
}

public sealed record OpenAiChatResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChoice> Choices,
    [property: JsonPropertyName("usage")] OpenAiUsage Usage)
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "chat.completion";
}

public sealed record OpenAiChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] OpenAiResponseMessage Message,
    [property: JsonPropertyName("finish_reason")] string FinishReason);

public sealed record OpenAiResponseMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed record OpenAiChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChunkChoice> Choices)
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "chat.completion.chunk";
}

public sealed record OpenAiChunkChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] OpenAiDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

public sealed record OpenAiDelta(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("role")] string? Role = null);

public sealed record OpenAiErrorResponse(
    [property: JsonPropertyName("error")] OpenAiError Error);

public sealed record OpenAiError(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("code")] string? Code = null);
