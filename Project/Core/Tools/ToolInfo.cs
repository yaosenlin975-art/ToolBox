using System.Text.Json;

namespace ToolBox.Core.Tools;

public class ToolInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<ToolParamInfo> Parameters { get; set; } = [];
    public object? Handler { get; set; }  // MethodInfo or delegate
    public bool IsAsync { get; set; }

    public string ToJsonSchema()
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var param in Parameters)
        {
            var schema = new Dictionary<string, object>
            {
                ["type"] = param.JsonType
            };
            if (!string.IsNullOrEmpty(param.Description))
                schema["description"] = param.Description;
            if (param.Required)
                required.Add(param.Name);
            properties[param.Name] = schema;
        }

        return JsonSerializer.Serialize(new
        {
            type = "object",
            properties,
            required
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}

public class ToolParamInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
    public string JsonType { get; set; } = "string";
}
