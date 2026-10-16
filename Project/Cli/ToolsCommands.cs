using System.CommandLine;
using System.Text.Json;
using ToolBox.Core.Todo;

namespace ToolBox.Cli;

internal static partial class Program
{
    private static void AddToolsCommands(RootCommand root)
    {
        var tools = new Command("tools", "Inspect and invoke Assistant tools.");

        var list = new Command("list", "List registered Assistant tools and their schemas.");
        list.SetAction((_, cancellationToken) => SendAsync(
            new TaskRequest { Operation = "tools.list" }, true, cancellationToken));
        tools.Subcommands.Add(list);

        var get = new Command("get", "Read one tool schema.");
        var toolName = new Argument<string>("name") { Description = "Exact registered tool name." };
        get.Arguments.Add(toolName);
        get.SetAction((parseResult, cancellationToken) => SendAsync(
            new TaskRequest { Operation = "tools.get", Id = parseResult.GetValue(toolName) }, true, cancellationToken));
        tools.Subcommands.Add(get);

        var call = new Command("call", "Queue one tool call.");
        var callName = new Argument<string>("name") { Description = "Exact registered tool name." };
        var input = new Option<string>("--input")
        {
            Required = true,
            Description = "Read one flat JSON object from a file or stdin (-)."
        };
        var requestId = new Option<string>("--request-id")
        {
            Description = "GUID reused to retry the same tool call safely."
        };
        call.Arguments.Add(callName);
        call.Options.Add(input);
        call.Options.Add(requestId);
        call.SetAction((parseResult, cancellationToken) => SendToolsWithInputAsync(
            parseResult.GetValue(callName)!, parseResult.GetValue(input)!, parseResult.GetValue(requestId), cancellationToken));
        tools.Subcommands.Add(call);

        foreach (string operation in new[] { "result", "cancel" })
        {
            var command = new Command(operation, operation == "result" ? "Read one tool call result." : "Cancel one tool call.");
            var callId = new Argument<string>("callId") { Description = "D-format tool call GUID." };
            command.Arguments.Add(callId);
            command.SetAction((parseResult, cancellationToken) => SendAsync(
                new TaskRequest { Operation = "tools." + operation, Id = parseResult.GetValue(callId) }, true, cancellationToken));
            tools.Subcommands.Add(command);
        }

        root.Subcommands.Add(tools);
    }

    private static async Task<int> SendToolsWithInputAsync(string toolName, string inputPath, string? requestId, CancellationToken cancellationToken)
    {
        var request = new TaskRequest
        {
            Operation = "tools.call",
            Id = toolName,
            RequestId = requestId ?? Guid.NewGuid().ToString("D")
        };
        try
        {
            request.Payload = await ReadInputAsync(inputPath, "tools.call", cancellationToken).ConfigureAwait(false);
            TaskProtocol.Validate(request);
            return await SendAsync(request, true, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCommandException ex)
        {
            return WriteFailure(request, ex.Code, ex.Message, ex.Field);
        }
        catch (OperationCanceledException)
        {
            return WriteFailure(request, "TIMEOUT", "The command was canceled.");
        }
        catch (Exception)
        {
            return WriteFailure(request, "INTERNAL_ERROR", "The command could not be completed.");
        }
    }
}
