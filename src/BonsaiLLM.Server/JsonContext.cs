using System.Text.Json.Serialization;

namespace BonsaiLLM.Server;

/// <summary>
/// Native AOT 下 System.Text.Json 不开反射，所有进出的 DTO 都得在这里登记；
/// 命名策略与 Web 默认值保持一致（camelCase），显式 <c>[JsonPropertyName]</c> 不受影响。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiModelList))]
[JsonSerializable(typeof(OpenAiModel))]
[JsonSerializable(typeof(OpenAiChatResponse))]
[JsonSerializable(typeof(OpenAiChunk))]
[JsonSerializable(typeof(OpenAiErrorResponse))]
[JsonSerializable(typeof(StateDto))]
[JsonSerializable(typeof(LoadRequest))]
[JsonSerializable(typeof(StopDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(List<LogLineDto>))]
internal sealed partial class AppJson : JsonSerializerContext;
