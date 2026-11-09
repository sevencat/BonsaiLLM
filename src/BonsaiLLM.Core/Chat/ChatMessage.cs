namespace BonsaiLLM.Core.Chat;

public sealed record ChatMessage(ChatRole Role, string Text);
