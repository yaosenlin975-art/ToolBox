using System.CommandLine;
using System.Text.Json;
using ToolBox.Core.Todo;

namespace ToolBox.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var root = CreateRootCommand();
            var parseResult = root.Parse(args);
            if (parseResult.Errors.Count > 0)
                return WriteFailure(null, "INVALID_ARGUMENT", string.Join(" ", parseResult.Errors.Select(error => error.Message)));

            return await parseResult.InvokeAsync(
                new InvocationConfiguration { EnableDefaultExceptionHandler = false },
                CancellationToken.None);
        }
        catch (Exception)
        {
            return WriteFailure(null, "INTERNAL_ERROR", "The command could not be completed.");
        }
    }

    private static RootCommand CreateRootCommand()
    {
        var root = new RootCommand("Discover and invoke ToolBox capabilities for local agents.");
        root.SetAction(_ => WriteFailure(null, "INVALID_ARGUMENT", "A command is required."));

        var task = new Command("task", "Manage tasks.");
        task.Subcommands.Add(CreateInputCommand("create", "Create a task.", "task.create", null));

        var list = new Command("list", "List tasks.");
        var status = new Option<string>("--status") { Description = "Filter: all, pending, completed, trashed, or repeating." };
        list.Options.Add(status);
        list.SetAction((parseResult, cancellationToken) => SendAsync(
            new TaskRequest { Operation = "task.list", Status = parseResult.GetValue(status) }, true, cancellationToken));
        task.Subcommands.Add(list);

        task.Subcommands.Add(CreateIdCommand("get", "Read a task.", "task.get"));
        task.Subcommands.Add(CreateInputCommand("update", "Update a task.", "task.update", new Argument<string>("id")));
        task.Subcommands.Add(CreateIdCommand("complete", "Complete a task.", "task.complete"));
        task.Subcommands.Add(CreateIdCommand("reopen", "Reopen a task.", "task.reopen"));
        task.Subcommands.Add(CreateIdCommand("delete", "Move a task to the recycle bin.", "task.delete"));
        task.Subcommands.Add(CreateIdCommand("restore", "Restore a task from the recycle bin.", "task.restore"));
        root.Subcommands.Add(task);

        var host = new Command("host", "Manage the background task host.");
        var stop = new Command("stop", "Stop the background task host.");
        stop.SetAction((_, cancellationToken) => SendAsync(
            new TaskRequest { Operation = "host.stop" }, false, cancellationToken));
        host.Subcommands.Add(stop);
        root.Subcommands.Add(host);

        var capabilities = new Command("capabilities", "Discover locally allowed agent capabilities.");
        var capabilityList = new Command("list", "List the static capability allow-list.");
        capabilityList.SetAction(_ => WriteCapabilitiesList());
        capabilities.Subcommands.Add(capabilityList);

        var capabilityGet = new Command("get", "Get one capability by exact name.");
        var capabilityName = new Argument<string>("name") { Description = "Exact capability name." };
        capabilityGet.Arguments.Add(capabilityName);
        capabilityGet.SetAction(parseResult => WriteCapability(parseResult.GetValue(capabilityName)!));
        capabilities.Subcommands.Add(capabilityGet);
        root.Subcommands.Add(capabilities);

        var ocr = new Command("ocr", "Run local image OCR.");
        ocr.Subcommands.Add(CreateInputCommand("file", "Recognize text in one local image.", "ocr.file", null));
        root.Subcommands.Add(ocr);

        var screenshot = new Command("screenshot", "Search existing screenshot metadata.");
        screenshot.Subcommands.Add(CreateInputCommand("search", "Search screenshots by read-only metadata.", "screenshot.search", null));
        root.Subcommands.Add(screenshot);
        return root;
    }

    private static int WriteCapabilitiesList()
    {
        var request = new TaskRequest { Operation = "capabilities.list" };
        var response = TaskResponse.Success(request, AgentCapabilities.All);
        Console.Out.WriteLine(JsonSerializer.Serialize(response, TaskProtocol.Options));
        return response.ExitCode;
    }

    private static int WriteCapability(string name)
    {
        var request = new TaskRequest { Operation = "capabilities.get", Id = name };
        if (!AgentCapabilities.TryGet(name, out var capability))
            return WriteFailure(request, "NOT_FOUND", "The requested capability was not found.", "name");

        var response = TaskResponse.Success(request, capability);
        Console.Out.WriteLine(JsonSerializer.Serialize(response, TaskProtocol.Options));
        return response.ExitCode;
    }

    private static Command CreateIdCommand(string name, string description, string operation)
    {
        var command = new Command(name, description);
        var id = new Argument<string>("id") { Description = "Exact 12-character task ID." };
        command.Arguments.Add(id);
        command.SetAction((parseResult, cancellationToken) => SendAsync(
            new TaskRequest { Operation = operation, Id = parseResult.GetValue(id) }, true, cancellationToken));
        return command;
    }

    private static Command CreateInputCommand(string name, string description, string operation, Argument<string>? id)
    {
        var command = new Command(name, description);
        if (id is not null) command.Arguments.Add(id);
        var input = new Option<string>("--input")
        {
            Required = true,
            Description = "Read one JSON object from a file or stdin (-)."
        };
        command.Options.Add(input);
        command.SetAction((parseResult, cancellationToken) => SendWithInputAsync(
            operation,
            id is null ? null : parseResult.GetValue(id),
            parseResult.GetValue(input)!,
            cancellationToken));
        return command;
    }

    private static async Task<int> SendWithInputAsync(string operation, string? id, string inputPath, CancellationToken cancellationToken)
    {
        var request = new TaskRequest { Operation = operation, Id = id };
        try
        {
            request.Payload = await ReadInputAsync(inputPath, operation, cancellationToken).ConfigureAwait(false);
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

    private static async Task<JsonElement> ReadInputAsync(string path, string operation, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            if (path == "-")
                bytes = await ReadBoundedAsync(Console.OpenStandardInput(), cancellationToken).ConfigureAwait(false);
            else
            {
                await using var stream = File.OpenRead(path);
                bytes = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TaskCommandException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new TaskCommandException("IO_ERROR", "The JSON input could not be read.");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new TaskCommandException("INVALID_ARGUMENT", "Input must be one JSON object.", "payload");
            TaskProtocol.RejectDuplicateProperties(document.RootElement);
            var payload = document.RootElement.Clone();
            TaskProtocol.ValidatePayload(operation, payload);
            return payload;
        }
        catch (JsonException)
        {
            throw new TaskCommandException("INVALID_ARGUMENT", "Input must be valid UTF-8 JSON.", "payload");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > TaskProtocol.MaxBytes)
                throw new TaskCommandException("INVALID_ARGUMENT", "JSON input exceeds 4 MiB.", "payload");
            output.Write(buffer, 0, read);
        }
    }

    private static async Task<int> SendAsync(TaskRequest request, bool startHost, CancellationToken cancellationToken)
    {
        try
        {
            var response = await TaskIpc.SendAsync(request, startHost, cancellationToken).ConfigureAwait(false);
            Console.Out.WriteLine(JsonSerializer.Serialize(response, TaskProtocol.Options));
            return response.ExitCode;
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

    private static int WriteFailure(TaskRequest? request, string code, string message, string? field = null)
    {
        var response = TaskResponse.Failure(request, code, message, field);
        Console.Out.WriteLine(JsonSerializer.Serialize(response, TaskProtocol.Options));
        return response.ExitCode;
    }
}
