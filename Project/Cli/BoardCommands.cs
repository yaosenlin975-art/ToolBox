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
                (command => AddIntegerField(command, "order", "--order", "Column position.")),
                (command => AddStringField(command, "status", "--status", "Status mapped to this column."))
            ]);
        AddBoardCommand(columns, "update", "Update a board column.", "board.column.update",
            required: ["id"], oneOf: ["title", "order", "status"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Column ID.")),
                (command => AddStringField(command, "title", "--title", "New column title.")),
                (command => AddIntegerField(command, "order", "--order", "New column position.")),
                (command => AddStringField(command, "status", "--status", "Status mapped to this column.")),
                (command => AddBooleanField(command, "status", "--clear-status", "Clear the status mapping.", includeNull: true))
            ]);
        AddBoardCommand(columns, "delete", "Delete a board column.", "board.column.delete",
            required: ["id"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Column ID.")),
                (command => AddStringField(command, "targetColumnId", "--target-column-id", "Column to receive its cards.")),
                AddActorField,
                AddHostField,
                AddSessionField
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
                (command => AddBooleanField(command, "includeDeleted", "--include-deleted", "Include soft-deleted cards.")),
                (command => AddStringField(command, "status", "--status", "Filter by stable card status.")),
                (command => AddStringField(command, "parentId", "--parent-id", "Filter by parent card ID.")),
                (command => AddBooleanField(command, "rootsOnly", "--roots-only", "List root cards only.")),
                (command => AddBooleanField(command, "available", "--available", "List unstarted cards or cards with expired claims.")),
                (command => AddBooleanField(command, "mine", "--mine", "List cards claimed by this actor.")),
                AddActorField,
                AddHostField,
                AddSessionField
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
                (command => AddIntegerField(command, "order", "--order", "Card position.")),
                (command => AddStringField(command, "parentId", "--parent-id", "Parent card ID.")),
                AddActorField,
                AddHostField,
                AddSessionField
            ]);
        AddBoardCommand(cards, "update", "Update a board card.", "board.card.update",
            required: ["id"], oneOf: ["title", "description", "priority", "tags", "dueDate", "columnId", "order", "parentId", "clearParentId"], fields:
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
                (command => AddIntegerField(command, "order", "--order", "New card position.")),
                (command => AddStringField(command, "parentId", "--parent-id", "New parent card ID.")),
                (command => AddBooleanField(command, "clearParentId", "--clear-parent-id", "Clear the parent card.")),
                AddActorField,
                AddHostField,
                AddSessionField
            ]);
        AddBoardCommand(cards, "move", "Move a board card to a column.", "board.card.move",
            required: ["id", "targetColumnId"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Card ID.")),
                (command => AddStringField(command, "targetColumnId", "--target-column-id", "Destination column ID.")),
                (command => AddIntegerField(command, "order", "--order", "Destination card position.")),
                (command => AddStringField(command, "transition", "--transition", "State transition required when moving between mapped statuses.")),
                (command => AddIntegerField(command, "ttlMinutes", "--ttl-minutes", "Claim lease duration from 5 to 480 minutes.")),
                AddActorField,
                AddHostField,
                AddSessionField
            ]);
        AddBoardCommand(cards, "delete", "Soft-delete a board card.", "board.card.delete",
            required: ["id"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Card ID.")),
                AddActorField,
                AddHostField,
                AddSessionField
            ]);
        AddBoardCommand(cards, "restore", "Restore a soft-deleted board card.", "board.card.restore",
            required: ["id"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Card ID.")),
                AddActorField,
                AddHostField,
                AddSessionField
            ]);
        AddBoardCommand(cards, "add-to-todo", "Copy a board card to personal tasks.", "board.card.add-to-todo",
            required: ["id"], fields:
            [
                (command => AddStringField(command, "project", "--project", "Project board ID or path.")),
                AddRevisionField,
                (command => AddStringField(command, "id", "--id", "Card ID."))
            ]);
        board.Subcommands.Add(cards);
        AddBoardV2Commands(board);
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

    private static BoardField AddCommentRevisionField(Command command)
    {
        var option = new Option<long?>("--comment-revision") { Description = "Expected comment revision for writes or pagination." };
        command.Options.Add(option);
        return new BoardField("commentRevision", option, parseResult => parseResult.GetValue(option));
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

    private static BoardField AddActorField(Command command)
    {
        var option = new Option<string?>("--actor") { Description = "Actor ID; defaults to TOOLBOX_BOARD_ACTOR." };
        command.Options.Add(option);
        var mine = command.Options.OfType<Option<bool>>().FirstOrDefault(candidate => candidate.Name == "--mine");
        return new BoardField("actor", option,
            parseResult => parseResult.GetValue(option) ?? Environment.GetEnvironmentVariable("TOOLBOX_BOARD_ACTOR"),
            IsActive: parseResult => parseResult.GetResult(option) is not null || mine is null || parseResult.GetValue(mine));
    }

    private static BoardField AddHostField(Command command) =>
        AddStringField(command, "host", "--host", "Explicit host ID; it is never guessed.");

    private static BoardField AddSessionField(Command command) =>
        AddStringField(command, "session", "--session", "Explicit session ID when available; it is never guessed.");

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
                if (fields.Where(field => field.Name == "status").Count(field => WasSpecified(parseResult, field)) > 1)
                    return WriteFailure(request, "INVALID_ARGUMENT", "Use either --status or --clear-status.", "status");
                if (fields.Any(field => field.Name == "parentId" && WasSpecified(parseResult, field)) &&
                    fields.Any(field => field.Name == "clearParentId" && WasSpecified(parseResult, field)))
                    return WriteFailure(request, "INVALID_ARGUMENT", "Use either --parent-id or --clear-parent-id.", "parentId");
                if (fields.Any(field => field.Name == "clear" && WasSpecified(parseResult, field)) &&
                    fields.Any(field => (field.Name is "targetHost" or "targetSession") && WasSpecified(parseResult, field)))
                    return WriteFailure(request, "INVALID_ARGUMENT", "Use --clear or target host/session fields.", "clear");

                request.Payload = BuildBoardPayload(parseResult, operation, fields);
            }

            JsonElement finalPayload = request.Payload!.Value;
            foreach (string field in required ?? [])
                if (!finalPayload.TryGetProperty(field, out _))
                    return WriteFailure(request, "INVALID_ARGUMENT", $"--{ToOptionName(field)} is required unless --input is used.", field);
            if (oneOf is { Length: > 0 } && !PayloadHasOneOf(finalPayload, oneOf))
                return WriteFailure(request, "INVALID_ARGUMENT", "At least one update field is required.", "payload");
            TaskProtocol.Validate(request);
            ValidateBoardCliPayload(operation, request.Payload!.Value);
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
        if (parseResult.GetResult(field.Option) is null)
            return field.Name == "actor" && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TOOLBOX_BOARD_ACTOR")) &&
                (field.IsActive?.Invoke(parseResult) ?? true);
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
            {
                if (field.Name == "actor" && parseResult.GetResult(field.Option) is null) continue;
                throw new TaskCommandException("INVALID_ARGUMENT", $"Field '{field.Name}' was provided in both JSON and an option.", field.Name);
            }
            var value = field.GetValue(parseResult);
            if (value is null && !field.IncludeNull) continue;
            node[field.Name] = value is null ? null : JsonSerializer.SerializeToNode(value, TaskProtocol.Options);
        }

        if (operation == "board.card.list" && node["mine"] is JsonValue mineValue && mineValue.TryGetValue<bool>(out bool mineOnly) && mineOnly && !node.ContainsKey("actor") &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TOOLBOX_BOARD_ACTOR")))
            node["actor"] = Environment.GetEnvironmentVariable("TOOLBOX_BOARD_ACTOR");

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
        if (operation == "board.attachment.add" && payload["file"] is JsonValue fileValue &&
            fileValue.TryGetValue<string>(out var file) && !string.IsNullOrWhiteSpace(file))
            payload["file"] = GetAbsolutePath(file, Directory.GetCurrentDirectory(), "file");
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
        "parentId" => "parent-id",
        "commentId" => "comment-id",
        "attachmentId" => "attachment-id",
        "claimId" => "claim-id",
        "targetHost" => "target-host",
        "targetSession" => "target-session",
        "ttlMinutes" => "ttl-minutes",
        "commentRevision" => "comment-revision",
        "clearParentId" => "clear-parent-id",
        "dueDate" => "due-date",
        _ => field
    };

    private static bool PayloadHasOneOf(JsonElement payload, string[] fields) => fields.Any(field =>
        payload.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.False);

    private static void ValidateBoardCliPayload(string operation, JsonElement payload)
    {
        if (operation == "board.card.list" && payload.TryGetProperty("mine", out var mine) && mine.ValueKind == JsonValueKind.True &&
            (!payload.TryGetProperty("actor", out _) || !payload.TryGetProperty("host", out _)))
            throw new TaskCommandException("INVALID_ARGUMENT", "--mine requires --actor and --host (TOOLBOX_BOARD_ACTOR may supply the actor).", "mine");
        foreach (string name in new[] { "ttlMinutes", "limit" })
        {
            if (!payload.TryGetProperty(name, out var value)) continue;
            bool valid = name == "ttlMinutes"
                ? value.TryGetInt32(out int ttl) && ttl is >= 5 and <= 480
                : value.TryGetInt32(out int limit) && limit is >= 1 and <= 100;
            if (!valid) throw new TaskCommandException("INVALID_ARGUMENT", $"{name} is outside its supported range.", name);
        }
        if (payload.TryGetProperty("status", out var status) && status.ValueKind != JsonValueKind.Null &&
            (status.ValueKind != JsonValueKind.String || status.GetString() is not ("notStarted" or "inProgress" or "pendingReview" or "closed")))
            throw new TaskCommandException("INVALID_ARGUMENT", "Invalid board status.", "status");
        if (payload.TryGetProperty("transition", out var transition) &&
            (transition.ValueKind != JsonValueKind.String || transition.GetString() is not ("claim" or "release" or "submit" or "close" or "reopen")))
            throw new TaskCommandException("INVALID_ARGUMENT", "Invalid card transition.", "transition");
        if (payload.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String &&
            System.Text.Encoding.UTF8.GetByteCount(body.GetString()!) > 16 * 1024)
            throw new TaskCommandException("INVALID_ARGUMENT", "Comment body exceeds 16 KiB in UTF-8.", "body");
        if (operation == "board.comment.deliver")
        {
            bool confirmed = payload.TryGetProperty("confirm", out var confirm) && confirm.ValueKind == JsonValueKind.True;
            bool verifiedUnknown = payload.TryGetProperty("verifiedUnknown", out var verified) && verified.ValueKind == JsonValueKind.True;
            if ((confirmed || verifiedUnknown) && (!confirmed || !payload.TryGetProperty("confirmationToken", out _)))
                throw new TaskCommandException("INVALID_ARGUMENT", "--confirm and --confirmation-token are required for delivery or an unknown retry.", "confirmationToken");
        }
        if (operation == "board.card.update" && payload.TryGetProperty("clearParentId", out var clearParent) &&
            clearParent.ValueKind == JsonValueKind.True && payload.TryGetProperty("parentId", out _))
            throw new TaskCommandException("INVALID_ARGUMENT", "Use either parentId or clearParentId.", "parentId");
        if (operation == "board.comment.bind" && payload.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.True &&
            (payload.TryGetProperty("targetHost", out _) || payload.TryGetProperty("targetSession", out _)))
            throw new TaskCommandException("INVALID_ARGUMENT", "Use clear or target host/session fields.", "clear");
    }
}
