namespace ToolBox.Core.Llm;

public interface ISystemPromptBuilder
{
    string Build();
    string BuildWithMemory(IReadOnlyList<string> memories);
}
