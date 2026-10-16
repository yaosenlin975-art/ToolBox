using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace ToolBox.Core.Tools;

public class ToolRegistry
{
    private readonly Dictionary<string, ToolInfo> tools = [];
    private readonly Dictionary<string, RegisteredTool> handlers = [];

    public ToolRegistry Register(Type type)
    {
        RegisterMethods(type, null);
        return this;
    }

    public ToolRegistry Register(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        RegisterMethods(instance.GetType(), instance);
        return this;
    }

    public string Execute(string toolName, Dictionary<string, object> arguments)
    {
        var registered = GetHandler(toolName);
        var result = Invoke(registered, GetArguments(registered.Method, arguments, default));
        if (result is Task<string> task)
            return task.GetAwaiter().GetResult();
        return result?.ToString() ?? string.Empty;
    }

    public async Task<string> ExecuteAsync(string toolName, Dictionary<string, object> arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registered = GetHandler(toolName);
        var result = Invoke(registered, GetArguments(registered.Method, arguments, cancellationToken));
        if (result is Task<string> task)
            return await task.ConfigureAwait(false);
        return result?.ToString() ?? string.Empty;
    }

    public bool IsAsync(string toolName) =>
        tools.TryGetValue(toolName, out var tool) && tool.IsAsync;

    public IReadOnlyList<ToolInfo> GetAllTools() => tools.Values.ToList().AsReadOnly();

    public bool TryGetTool(string name, out ToolInfo? tool) =>
        tools.TryGetValue(name, out tool);

    private void RegisterMethods(Type type, object? target)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
        {
            var attr = method.GetCustomAttribute<ToolAttribute>();
            if (attr == null || (!method.IsStatic && target == null)) continue;

            var parameters = method.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken))
                .Select(p => new ToolParamInfo
                {
                    Name = p.Name ?? string.Empty,
                    Description = p.GetCustomAttribute<ToolParamAttribute>()?.Description ?? string.Empty,
                    Required = !p.HasDefaultValue && p.GetCustomAttribute<ToolParamAttribute>()?.Required != false,
                    JsonType = GetJsonType(p.ParameterType)
                }).ToList();

            tools[attr.Name] = new ToolInfo
            {
                Name = attr.Name,
                Description = attr.Description,
                Parameters = parameters,
                Handler = method,
                IsAsync = method.ReturnType == typeof(Task<string>)
            };
            handlers[attr.Name] = new RegisteredTool(method, method.IsStatic ? null : target);
        }
    }

    private RegisteredTool GetHandler(string toolName) =>
        handlers.TryGetValue(toolName, out var registered) ? registered : throw new ToolNotFoundException(toolName);

    private static object?[] GetArguments(MethodInfo method, Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var parameters = method.GetParameters();
        var names = parameters.Where(p => p.ParameterType != typeof(CancellationToken))
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = arguments.Keys.FirstOrDefault(name => !names.Contains(name));
        if (unknown != null) throw new UnknownParameterException(unknown);

        var values = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                values[i] = cancellationToken;
                continue;
            }

            if (arguments.TryGetValue(parameter.Name!, out var value))
                values[i] = ConvertArgument(parameter, value);
            else if (parameter.HasDefaultValue)
                values[i] = parameter.DefaultValue;
            else if (parameter.GetCustomAttribute<ToolParamAttribute>()?.Required == false)
                values[i] = parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
            else
                throw new MissingParameterException(parameter.Name!);
        }
        return values;
    }

    private static object ConvertArgument(ParameterInfo parameter, object? value)
    {
        var type = parameter.ParameterType;
        var name = parameter.Name!;
        var source = value;
        if (source is JsonElement json)
        {
            try
            {
                source = JsonSerializer.Deserialize(json.GetRawText(), type);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException or OverflowException)
            {
                throw new InvalidToolParameterException(name, GetJsonType(type), json.ValueKind.ToString());
            }
        }

        if (source != null && type.IsInstanceOfType(source)) return source;
        if (source != null && IsNumber(type) && IsNumber(source.GetType()))
        {
            try { return Convert.ChangeType(source, type, CultureInfo.InvariantCulture)!; }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                throw new InvalidToolParameterException(name, GetJsonType(type), source.GetType().Name);
            }
        }
        throw new InvalidToolParameterException(name, GetJsonType(type), source?.GetType().Name ?? "null");
    }

    private static object? Invoke(RegisteredTool registered, object?[] arguments)
    {
        try { return registered.Method.Invoke(registered.Target, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static bool IsNumber(Type type) =>
        type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    private static string GetJsonType(Type type)
    {
        if (type == typeof(bool)) return "boolean";
        if (IsNumber(type)) return "number";
        return "string";
    }

    private sealed record RegisteredTool(MethodInfo Method, object? Target);
}

public class ToolNotFoundException : Exception
{
    public ToolNotFoundException(string name) : base($"Tool not found: {name}") { }
}

public class MissingParameterException : Exception
{
    public MissingParameterException(string name) : base($"Missing required parameter: {name}") { }
}

public class UnknownParameterException : Exception
{
    public UnknownParameterException(string name) : base($"Unknown tool parameter: {name}") { }
}

public class InvalidToolParameterException : Exception
{
    public InvalidToolParameterException(string name, string expected, string actual)
        : base($"Invalid tool parameter '{name}': expected {expected}, got {actual}") { }
}
