using System.Reflection;
using System.Text.Json;

namespace ToolBox.Core.Tools;

public class ToolRegistry
{
    private readonly Dictionary<string, ToolInfo> tools = [];
    private readonly Dictionary<string, MethodInfo> handlers = [];


    public ToolRegistry Register(Type type)
    {
        var methods = type.GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<ToolAttribute>();
            if (attr == null) continue;

            var toolInfo = new ToolInfo
            {
                Name = attr.Name,
                Description = attr.Description,
                Handler = method
            };

            foreach (var param in method.GetParameters())
            {
                var paramAttr = param.GetCustomAttribute<ToolParamAttribute>();
                toolInfo.Parameters.Add(new ToolParamInfo
                {
                    Name = param.Name ?? "",
                    Description = paramAttr?.Description ?? "",
                    Required = paramAttr?.Required ?? !param.HasDefaultValue,
                    JsonType = GetJsonType(param.ParameterType)
                });
            }

            tools[attr.Name] = toolInfo;
            handlers[attr.Name] = method;
        }
        return this;
    }

    public ToolRegistry Register(object instance)
    {
        var methods = instance.GetType().GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<ToolAttribute>();
            if (attr == null) continue;

            var toolInfo = new ToolInfo
            {
                Name = attr.Name,
                Description = attr.Description,
                Handler = method
            };

            foreach (var param in method.GetParameters())
            {
                var paramAttr = param.GetCustomAttribute<ToolParamAttribute>();
                toolInfo.Parameters.Add(new ToolParamInfo
                {
                    Name = param.Name ?? "",
                    Description = paramAttr?.Description ?? "",
                    Required = paramAttr?.Required ?? !param.HasDefaultValue,
                    JsonType = GetJsonType(param.ParameterType)
                });
            }

            tools[attr.Name] = toolInfo;
            handlers[attr.Name] = method;
        }
        return this;
    }

    public string Execute(string toolName, Dictionary<string, object> arguments)
    {
        if (!handlers.TryGetValue(toolName, out var method))
            throw new ToolNotFoundException(toolName);

        var paramInfos = method.GetParameters();
        var args = new object[paramInfos.Length];

        for (int i = 0; i < paramInfos.Length; i++)
        {
            if (arguments.TryGetValue(paramInfos[i].Name!, out var value))
            {
                args[i] = Convert.ChangeType(value, paramInfos[i].ParameterType);
            }
            else if (paramInfos[i].HasDefaultValue)
            {
                args[i] = paramInfos[i].DefaultValue!;
            }
            else
            {
                throw new MissingParameterException(paramInfos[i].Name!);
            }
        }

        var result = method.Invoke(null, args);
        return result?.ToString() ?? string.Empty;
    }

    public IReadOnlyList<ToolInfo> GetAllTools() => tools.Values.ToList().AsReadOnly();

    public bool TryGetTool(string name, out ToolInfo? tool) =>
        tools.TryGetValue(name, out tool);

    private static string GetJsonType(Type type)
    {
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(float))
            return "number";
        return "string";
    }
}

public class ToolNotFoundException : Exception
{
    public ToolNotFoundException(string name) : base($"Tool not found: {name}") { }
}

public class MissingParameterException : Exception
{
    public MissingParameterException(string name) : base($"Missing required parameter: {name}") { }
}
