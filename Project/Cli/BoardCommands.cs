using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using ToolBox.Core.Todo;

namespace ToolBox.Cli;

internal static partial class Program
{
    private sealed record BoardField(
        string Name,
        Option Option,
        Func<ParseResult, object?> GetValue,
        bool IncludeNull = false,
        Func<ParseResult, bool>? IsActive = null);

    private static void AddBoardCommands(RootCommand root)
    {
        var project = new Command("project", "Manage registered project boards.");
        project.SetAction(_ => WriteFailure(new TaskRequest { Operation = "project" }, "INVALID_ARGUMENT", "A project command is required."));
        AddBoardCommand(project, "register", "Register a project directory.", "project.register",
            required: ["path"], fields:
            [
                (command => AddStringField(command, "path", "--path", "Project directory path.")),
                (command => AddBooleanField(command, "init", "--init", "Initialize a board when one does not exist.")),
                (command => AddBooleanField(command, "replacePath", "--replace-path", "Replace the registered path for this board."))
            ]);
        AddBoardCommand(project, "list", "List registered projects.", "project.list", includeCwd: false);
        AddBoardCommand(project, "get", "Get one registered project.", "project.get",
            fields: [(command => AddStringField(command, "project", "--project", "Project board ID or path."))]);
        AddBoardCommand(project, "unregister", "Unregister a project without deleting its board data.", "project.unregister",
            fields: [(command => AddStringField(command, "project", "--project", "Project board ID or path."))]);
        root.Subcommands.Add(project);

        var board = new Command("board", "Manage project boards.");
        board.SetAction(_ => WriteFailure(new TaskRequest { Operation = "board" }, "INVALID_ARGUMENT", "A board command is required."));
        AddBoardCommand(board, "get", "Get a project's board.", "board.get",
            fields: [(command => AddStringField(command, "project", "--project", "Project board ID or path."))]);

        var columns = new Command("column", "Manage board columns.");
        columns.SetAction(_ => WriteFailure(new TaskRequest { Operation = "board.column" }, "INVALID_ARGUMENT", "A column command is required."));
        AddBoardCommand(columns, "list", "List board columns.", "board.column.list",
            fields: [(command => AddStringField(command, "project", "--project", "Project board ID or path."))]);
        AddBoardCommand(columns, "create", "Create a board column.", "board.column.create",
            required: ["title"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "title", "--title", "Column title.")),
                (command => AddIntegerField(command, "order", "--order", "Column position."))
            ]);
        AddBoardCommand(columns, "update", "Update a board column.", "board.column.update",
            required: ["id"], oneOf: ["title", "order"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Column ID.")),
                (command => AddStringField(command, "title", "--title", "New column title.")),
                (command => AddIntegerField(command, "order", "--order", "New column position."))
            ]);
        AddBoardCommand(columns, "delete", "Delete a board column.", "board.column.delete",
            required: ["id"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Column ID.")),
                (command => AddStringField(command, "targetColumnId", "--target-column-id", "Column to receive its cards."))
            ]);
        AddBoardCommand(columns, "move", "Move a board column.", "board.column.move",
            required: ["id", "order"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Column ID.")),
                (command => AddIntegerField(command, "order", "--order", "New column position."))
            ]);
        board.Subcommands.Add(columns);

        var cards = new Command("card", "Manage board cards.");
        cards.SetAction(_ => WriteFailure(new TaskRequest { Operation = "board.card" }, "INVALID_ARGUMENT", "A card command is required."));
        AddBoardCommand(cards, "list", "List board cards.", "board.card.list",
            fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                (command => AddStringField(command, "columnId", "--column-id", "Filter by column ID.")),
                (command => AddBooleanField(command, "includeDeleted", "--include-deleted", "Include soft-deleted cards."))
            ]);
        AddBoardCommand(cards, "get", "Get a board card, including soft-deleted cards.", "board.card.get",
            required: ["id"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                (command => AddStringField(command, "id", "--id", "Card ID."))
            ]);
        AddBoardCommand(cards, "create", "Create a board card.", "board.card.create",
            required: ["title"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "title", "--title", "Card title.")),
                (command => AddStringField(command, "description", "--description", "Card description.")),
                (command => AddIntegerField(command, "priority", "--priority", "Priority from 0 to 2.")),
                (command => AddStringArrayField(command, "tags", "--tags", "Card tags; provide zero or more values.")),
                (command => AddStringField(command, "dueDate", "--due-date", "ISO 8601 due date with timezone.")),
                (command => AddStringField(command, "columnId", "--column-id", "Target column ID.")),
                (command => AddIntegerField(command, "order", "--order", "Card position."))
            ]);
        AddBoardCommand(cards, "update", "Update a board card.", "board.card.update",
            required: ["id"], oneOf: ["title", "description", "priority", "tags", "dueDate", "columnId", "order"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Card ID.")),
                (command => AddStringField(command, "title", "--title", "New card title.")),
                (command => AddStringField(command, "description", "--description", "New card description.")),
                (command => AddIntegerField(command, "priority", "--priority", "Priority from 0 to 2.")),
                (command => AddStringArrayField(command, "tags", "--tags", "Replacement tags; provide zero or more values.")),
                (command => AddStringField(command, "dueDate", "--due-date", "ISO 8601 due date with timezone.")),
                (command => AddBooleanField(command, "dueDate", "--clear-due-date", "Clear the due date.", includeNull: true)),
                (command => AddStringField(command, "columnId", "--column-id", "New column ID.")),
                (command => AddIntegerField(command, "order", "--order", "New card position."))
            ]);
        AddBoardCommand(cards, "move", "Move a board card to a column.", "board.card.move",
            required: ["id", "targetColumnId"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Card ID.")),
                (command => AddStringField(command, "targetColumnId", "--target-column-id", "Destination column ID.")),
                (command => AddIntegerField(command, "order", "--order", "Destination card position."))
            ]);
        AddBoardCommand(cards, "delete", "Soft-delete a board card.", "board.card.delete",
            required: ["id"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Card ID."))
            ]);
        AddBoardCommand(cards, "restore", "Restore a soft-deleted board card.", "board.card.restore",
            required: ["id"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Card ID."))
            ]);
        AddBoardCommand(cards, "add-to-todo", "Copy a board card to personal tasks.", "board.card.add-to-todo",
            required: ["id"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Card ID."))
            ]);
        board.Subcommands.Add(cards);
        root.Subcommands.Add(board);
    }

    private static Command AddBoardCommand(
        Command parent,
        string name,
        string description,
        string operation,
        string[]? required = null,
        string[]? oneOf = null,
        Func<Command, BoardField>[]? fields = null,
        bool includeCwd = true)
    {
        var command = new Command(name, description);
        var input = new Option<string?>("--input")
        {
            Description = "Read one JSON object from a file or stdin (-)."
        };
        command.Options.Add(input);
        command.Options.Add(new Option<bool>("--json") { Description = "Emit the structured JSON response (default)." });

        var boardFields = new List<BoardField>();
        if (includeCwd)
            boardFields.Add(AddStringField(command, "cwd", "--cwd", "Directory used to resolve the project; defaults to the current directory."));
        if (fields is not null)
            foreach (var addField in fields)
                boardFields.Add(addField(command));

        command.SetAction((parseResult, cancellationToken) => SendBoardCommandAsync(
            operation, parseResult, input, boardFields, required, oneOf, cancellationToken));
        parent.Subcommands.Add(command);
        return command;
    }

    private static BoardField AddStringField(Command command, string name, string alias, string description)
    {
        var option = new Option<string?>(alias) { Description = description };
        command.Options.Add(option);
        return new BoardField(name, option, parseResult => parseResult.GetValue(option));
    }

    private static BoardField AddIntegerField(Command command, string name, string alias, string description)
    {
        var option = new Option<int?>(alias) { Description = description };
        command.Options.Add(option);
        return new BoardField(name, option, parseResult => parseResult.GetValue(option));
    }

    private static BoardField AddRevisionField(Command command)
    {
        const string description = "Expected board revision.";
        var option = new Option<long?>("--revision") { Description = description };
        command.Options.Add(option);
        return new BoardField("revision", option, parseResult => parseResult.GetValue(option));
    }

    private static BoardField AddBooleanField(Command command, string name, string alias, string description, bool includeNull = false)
    {
        var option = new Option<bool>(alias) { Description = description };
        command.Options.Add(option);
        return new BoardField(name, option,
            parseResult => parseResult.GetValue(option) ? (includeNull ? null : true) : null,
            includeNull,
            parseResult => parseResult.GetValue(option));
    }

    private static BoardField AddStringArrayField(Command command, string name, string alias, string description)
    {
        var option = new Option<string[]>(alias)
        {
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
            Description = description
        };
        command.Options.Add(option);
        return new BoardField(name, option, parseResult => parseResult.GetValue(option) ?? []);
    }

    private static async Task<int> SendBoardCommandAsync(
        string operation,
        ParseResult parseResult,
        Option<string?> input,
        IReadOnlyList<BoardField> fields,
        string[]? required,
        string[]? oneOf,
        CancellationToken cancellationToken)
    {
        var request = new TaskRequest { Operation = operation };
        try
        {
            string? inputPath = parseResult.GetValue(input);
            if (inputPath is not null)
            {
                request.Payload = MergeBoardInput(
                    parseResult,
                    await ReadInputAsync(inputPath, operation, cancellationToken, validatePayload: false).ConfigureAwait(false),
                    operation,
                    fields);
            }
            else
            {
                foreach (string field in required ?? [])
                    if (!fields.Any(candidate => candidate.Name == field && WasSpecified(parseResult, candidate)))
                        return WriteFailure(request, "INVALID_ARGUMENT", $"--{ToOptionName(field)} is required unless --input is used.", field);

                if (oneOf is { Length: > 0 } && !oneOf.Any(field => fields.Any(candidate => candidate.Name == field && WasSpecified(parseResult, candidate))))
                    return WriteFailure(request, "INVALID_ARGUMENT", "At least one update field is required.", "payload");

                if (fields.Where(field => field.Name == "dueDate").Count(field => WasSpecified(parseResult, field)) > 1)
                    return WriteFailure(request, "INVALID_ARGUMENT", "Use either --due-date or --clear-due-date.", "dueDate");

                request.Payload = BuildBoardPayload(parseResult, operation, fields);
            }

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

    private static bool WasSpecified(ParseResult parseResult, BoardField field)
    {
        if (parseResult.GetResult(field.Option) is null) return false;
        if (field.IsActive is not null) return field.IsActive(parseResult);
        if (field.GetValue(parseResult) is bool value) return value;
        return true;
    }

    private static JsonElement BuildBoardPayload(ParseResult parseResult, string operation, IReadOnlyList<BoardField> fields)
    {
        string cwd = fields.FirstOrDefault(field => field.Name == "cwd")?.GetValue(parseResult) as string ?? Directory.GetCurrentDirectory();
        var payload = new JsonObject();
        if (operation is not ("project.list" or "project.register"))
            payload["cwd"] = cwd;
        foreach (var field in fields)
        {
            if (!WasSpecified(parseResult, field)) continue;
            var value = field.GetValue(parseResult);
            if (value is null && !field.IncludeNull) continue;
            if (operation == "project.register" && field.Name == "cwd") continue;
            payload[field.Name] = value is null ? null : JsonSerializer.SerializeToNode(value, TaskProtocol.Options);
        }
        NormalizeBoardPaths(payload, operation, cwd);
        return JsonSerializer.SerializeToElement(payload, TaskProtocol.Options);
    }

    private static JsonElement MergeBoardInput(ParseResult parseResult, JsonElement input, string operation, IReadOnlyList<BoardField> fields)
    {
        var node = JsonNode.Parse(input.GetRawText())!.AsObject();
        string cwd = fields.FirstOrDefault(field => field.Name == "cwd")?.GetValue(parseResult) as string ?? Directory.GetCurrentDirectory();
        foreach (var field in fields)
        {
            if (!WasSpecified(parseResult, field) || (operation == "project.register" && field.Name == "cwd")) continue;
            if (node.ContainsKey(field.Name))
                throw new TaskCommandException("INVALID_ARGUMENT", $"Field '{field.Name}' was provided in both JSON and an option.", field.Name);
            var value = field.GetValue(parseResult);
            if (value is null && !field.IncludeNull) continue;
            node[field.Name] = value is null ? null : JsonSerializer.SerializeToNode(value, TaskProtocol.Options);
        }

        NormalizeBoardPaths(node, operation, cwd);
        return JsonSerializer.SerializeToElement(node, TaskProtocol.Options);
    }

    private static void NormalizeBoardPaths(JsonObject payload, string operation, string fallbackCwd)
    {
        if (operation == "project.list") return;
        if (operation == "project.register")
        {
            if (payload["path"] is JsonValue pathValue && pathValue.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
                payload["path"] = GetAbsolutePath(path, fallbackCwd, "path");
            return;
        }

        if (!payload.TryGetPropertyValue("cwd", out JsonNode? cwdNode))
        {
            payload["cwd"] = GetAbsolutePath(fallbackCwd, Directory.GetCurrentDirectory(), "cwd");
            cwdNode = payload["cwd"];
        }
        if (cwdNode is not JsonValue cwdValue || !cwdValue.TryGetValue<string>(out var cwd) || string.IsNullOrWhiteSpace(cwd)) return;

        string absoluteCwd = GetAbsolutePath(cwd, Directory.GetCurrentDirectory(), "cwd");
        payload["cwd"] = absoluteCwd;
        if (payload["project"] is JsonValue projectValue && projectValue.TryGetValue<string>(out var project) &&
            !string.IsNullOrWhiteSpace(project) && !Guid.TryParse(project, out _))
        {
            payload["project"] = GetAbsolutePath(project, absoluteCwd, "project");
        }
    }

    private static string GetAbsolutePath(string path, string basePath, string field)
    {
        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, Path.GetFullPath(basePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new TaskCommandException("INVALID_ARGUMENT", $"The {field} or its base directory is invalid.", field);
        }
    }

    private static string ToOptionName(string field) => field switch
    {
        "targetColumnId" => "target-column-id",
        "columnId" => "column-id",
        "dueDate" => "due-date",
        _ => field
    };
}
