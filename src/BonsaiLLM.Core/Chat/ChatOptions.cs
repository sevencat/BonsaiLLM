namespace BonsaiLLM.Core.Chat;

public sealed record ChatOptions
{
    public double Temperature { get; init; } = 0.7;
    public double TopP { get; init; } = 0.9;
    public int TopK { get; init; } = 40;
    public double RepeatPenalty { get; init; } = 1.1;
    public int MaxTokens { get; init; } = -1;
}
