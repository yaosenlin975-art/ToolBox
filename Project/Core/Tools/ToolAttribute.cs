namespace ToolBox.Core.Tools;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ToolAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }

    public ToolAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }
}
