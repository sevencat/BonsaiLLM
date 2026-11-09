namespace BonsaiLLM.Core.Chat;

public sealed record UsageInfo(int PromptTokens, int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}
