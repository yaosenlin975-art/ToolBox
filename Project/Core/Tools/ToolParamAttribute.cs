namespace ToolBox.Core.Tools;

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class ToolParamAttribute : Attribute
{
    public string Description { get; }
    public bool Required { get; set; } = true;

    public ToolParamAttribute(string description)
    {
        Description = description;
    }
}
