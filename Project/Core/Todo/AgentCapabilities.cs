using System.Text.Json;

namespace ToolBox.Core.Todo;

public sealed record AgentCapabilityAvailability(bool RuntimeChecked, string[] Requirements);

public sealed record AgentCapability(
    string Name,
    string Command,
    string Description,
    JsonElement InputSchema,
    string[] OutputFields,
    string[] Effects,
    AgentCapabilityAvailability Availability);

public static class AgentCapabilities
{
    private static readonly AgentCapability[] Capabilities =
    [
        Create("task.create", "Create one task.", ObjectSchema(
            new Dictionary<string, object?>
            {
                ["title"] = new { type = "string", minLength = 1 },
                ["description"] = new { type = "string" },
                ["priority"] = new { type = "integer", minimum = 0, maximum = 2 },
                ["category"] = new { type = "string", minLength = 1 },
                ["tags"] = new { type = "array", items = new { type = "string" } },
                ["dueDate"] = new { type = new[] { "string", "null" }, format = "date-time" },
                ["parentId"] = new { type = new[] { "string", "null" }, pattern = "^[A-Fa-f0-9]{12}$" }
            }, ["title"]),
            ["id", "parentId", "title", "description", "isCompleted", "priority", "category", "tags", "dueDate", "progress", "createdAt", "completedAt", "isTrashed", "isRepeating", "repeatConfig", "lastResetAt"],
            ["writes one task through the existing local ToolBox host"],
            ["A same-user ToolBox host must be available; the CLI may start it without showing a window."]),

        Create("task.list", "List tasks with an optional status filter.", ObjectSchema(
            new Dictionary<string, object?>
            {
                ["status"] = new { type = "string", @enum = new[] { "all", "pending", "completed", "trashed", "repeating" }, @default = "all" }
            }),
            ["[] (task objects)"], ["reads the local task store"], ["A same-user ToolBox host must be available; the CLI may start it without showing a window."]),

        Create("task.get", "Read one task by its exact ID.", ObjectSchema(
            new Dictionary<string, object?> { ["id"] = TaskIdSchema() }, ["id"]),
            ["id", "parentId", "title", "description", "isCompleted", "priority", "category", "tags", "dueDate", "progress", "createdAt", "completedAt", "isTrashed", "isRepeating", "repeatConfig", "lastResetAt"],
            ["reads the local task store"], ["A same-user ToolBox host must be available; the CLI may start it without showing a window."]),

        Create("task.update", "Update one task by exact ID and supplied fields.", ObjectSchema(
            new Dictionary<string, object?>
            {
                ["id"] = TaskIdSchema(),
                ["title"] = new { type = "string", minLength = 1 },
                ["description"] = new { type = "string" },
                ["priority"] = new { type = "integer", minimum = 0, maximum = 2 },
                ["category"] = new { type = "string", minLength = 1 },
                ["tags"] = new { type = "array", items = new { type = "string" } },
                ["dueDate"] = new { type = new[] { "string", "null" }, format = "date-time" },
                ["progress"] = new { type = "integer", minimum = 0, maximum = 100 }
            }, ["id"], anyOf: ["title", "description", "priority", "category", "tags", "dueDate", "progress"]),
            ["id", "parentId", "title", "description", "isCompleted", "priority", "category", "tags", "dueDate", "progress", "createdAt", "completedAt", "isTrashed", "isRepeating", "repeatConfig", "lastResetAt"],
            ["writes only supplied task fields through the existing local ToolBox host"],
            ["A same-user ToolBox host must be available; the CLI may start it without showing a window."]),

        Create("task.complete", "Mark one task complete.", ObjectSchema(
            new Dictionary<string, object?> { ["id"] = TaskIdSchema() }, ["id"]),
            ["id", "parentId", "title", "description", "isCompleted", "priority", "category", "tags", "dueDate", "progress", "createdAt", "completedAt", "isTrashed", "isRepeating", "repeatConfig", "lastResetAt"], ["updates task completion state in the local store"],
            ["A same-user ToolBox host must be available; the CLI may start it without showing a window."]),

        Create("task.reopen", "Reopen one completed task.", ObjectSchema(
            new Dictionary<string, object?> { ["id"] = TaskIdSchema() }, ["id"]),
            ["id", "parentId", "title", "description", "isCompleted", "priority", "category", "tags", "dueDate", "progress", "createdAt", "completedAt", "isTrashed", "isRepeating", "repeatConfig", "lastResetAt"], ["updates task completion state in the local store"],
            ["A same-user ToolBox host must be available; the CLI may start it without showing a window."]),

        Create("task.delete", "Move one task to the recycle bin.", ObjectSchema(
            new Dictionary<string, object?> { ["id"] = TaskIdSchema() }, ["id"]),
            ["id", "parentId", "title", "description", "isCompleted", "priority", "category", "tags", "dueDate", "progress", "createdAt", "completedAt", "isTrashed", "isRepeating", "repeatConfig", "lastResetAt"], ["moves the task to the local recycle bin"],
            ["A same-user ToolBox host must be available; the CLI may start it without showing a window."]),

        Create("task.restore", "Restore one task from the recycle bin.", ObjectSchema(
            new Dictionary<string, object?> { ["id"] = TaskIdSchema() }, ["id"]),
            ["id", "parentId", "title", "description", "isCompleted", "priority", "category", "tags", "dueDate", "progress", "createdAt", "completedAt", "isTrashed", "isRepeating", "repeatConfig", "lastResetAt"], ["restores the task in the local store"],
            ["A same-user ToolBox host must be available; the CLI may start it without showing a window."]),

        LocalBoardCapability("project.register", "Register a local project directory.", ObjectSchema(
            new Dictionary<string, object?>
            {
                ["path"] = new { type = "string", format = "absolute-path", minLength = 1 },
                ["init"] = new { type = "boolean", @default = false },
                ["replacePath"] = new { type = "boolean", @default = false }
            }, ["path"]),
            ["boardId", "name", "path"], ["writes the local project registry", "may initialize a board in the requested directory"],
            "The same-user ToolBox host and requested directory must be accessible; initialization is opt-in."),

        LocalBoardCapability("project.list", "List locally registered projects.", EmptyObjectSchema(),
            ["[].boardId", "[].name", "[].path"], ["reads the local project registry"],
            "A same-user ToolBox host must be available; the local project registry is read when invoked."),

        LocalBoardCapability("project.get", "Read a registered project by ID, path, or current working directory.", ObjectSchema(
            ProjectContextProperties(), anyOf: ["project", "cwd"]),
            ["boardId", "name", "path"], ["reads the local project registry"],
            "A same-user ToolBox host and a resolvable registered project must be available."),

        LocalBoardCapability("project.unregister", "Unregister a local project.", ObjectSchema(
            ProjectContextProperties(), anyOf: ["project", "cwd"]),
            ["boardId", "name", "path"], ["removes the project mapping from the local registry"],
            "A same-user ToolBox host and a resolvable registered project must be available."),

        LocalBoardCapability("board.get", "Read the complete board for a registered project.", ObjectSchema(
            ProjectContextProperties(), anyOf: ["project", "cwd"]),
            ["schemaVersion", "boardId", "name", "revision", "columns[]", "cards[]"], ["reads the project board"],
            "A same-user ToolBox host and a resolvable project board must be available."),

        LocalBoardCapability("board.column.list", "List columns for a project board.", ObjectSchema(
            ProjectContextProperties(), anyOf: ["project", "cwd"]),
            ["columns", "revision"], ["reads the project board"],
            "A same-user ToolBox host and a resolvable project board must be available."),

        LocalBoardCapability("board.column.create", "Create a column on a project board.", ObjectSchema(
            WithProjectContext(["title", "order", "revision"]), ["title"]),
            ["column", "revision"], ["writes the project board"],
            "A same-user ToolBox host, a resolvable project board, and a valid title must be available."),

        LocalBoardCapability("board.column.update", "Update a column title or position.", ObjectSchema(
            WithProjectContext(["id", "title", "order", "revision"]), ["id"], anyOf: ["title", "order"]),
            ["column", "revision"], ["writes the project board"],
            "A same-user ToolBox host and a resolvable project board must be available; submit the latest revision when supplied."),

        LocalBoardCapability("board.column.delete", "Delete a column, optionally moving its cards to another column.", ObjectSchema(
            WithProjectContext(["id", "targetColumnId", "revision"]), ["id"]),
            ["column", "revision"], ["deletes a project board column", "requires targetColumnId when the column contains cards"],
            "A same-user ToolBox host and a resolvable project board must be available; deleting a populated column requires a valid target column."),

        LocalBoardCapability("board.column.move", "Move a column to a new position.", ObjectSchema(
            WithProjectContext(["id", "order", "revision"]), ["id", "order"]),
            ["column", "revision"], ["writes column order in the project board"],
            "A same-user ToolBox host and a resolvable project board must be available; submit the latest revision when supplied."),

        LocalBoardCapability("board.card.list", "List cards on a project board, including deleted cards when requested.", ObjectSchema(
            WithProjectContext(["columnId", "includeDeleted"])),
            ["cards", "revision"], ["reads the project board"],
            "A same-user ToolBox host and a resolvable project board must be available."),

        LocalBoardCapability("board.card.get", "Read one card by ID.", ObjectSchema(
            WithProjectContext(["id"]), ["id"]),
            ["card", "revision"], ["reads the project board"],
            "A same-user ToolBox host and a resolvable project board must be available."),

        LocalBoardCapability("board.card.create", "Create a card on a project board.", ObjectSchema(
            WithProjectContext(["title", "description", "priority", "tags", "dueDate", "columnId", "order", "revision"]), ["title"]),
            ["card", "revision"], ["writes the project board"],
            "A same-user ToolBox host, a resolvable project board, and a valid title must be available."),

        LocalBoardCapability("board.card.update", "Update supplied card fields.", ObjectSchema(
            WithProjectContext(["id", "title", "description", "priority", "tags", "dueDate", "columnId", "order", "revision"]), ["id"],
            anyOf: ["title", "description", "priority", "tags", "dueDate", "columnId", "order"]),
            ["card", "revision"], ["writes the project board"],
            "A same-user ToolBox host and a resolvable project board must be available; submit the latest revision when supplied."),

        LocalBoardCapability("board.card.move", "Move a card to a column and position.", ObjectSchema(
            WithProjectContext(["id", "targetColumnId", "order", "revision"]), ["id", "targetColumnId"]),
            ["card", "revision"], ["writes the project board"],
            "A same-user ToolBox host and a resolvable project board must be available; the destination column must exist."),

        LocalBoardCapability("board.card.delete", "Move a card to the board recycle bin.", ObjectSchema(
            WithProjectContext(["id", "revision"]), ["id"]),
            ["card", "revision"], ["soft-deletes the card on the project board"],
            "A same-user ToolBox host and a resolvable project board must be available."),

        LocalBoardCapability("board.card.restore", "Restore a card from the board recycle bin.", ObjectSchema(
            WithProjectContext(["id", "revision"]), ["id"]),
            ["card", "revision"], ["restores the card on the project board"],
            "A same-user ToolBox host and a resolvable project board must be available."),

        LocalBoardCapability("board.card.add-to-todo", "Copy a board card into personal Todo.", ObjectSchema(
            WithProjectContext(["id", "revision"]), ["id"]),
            ["todoId"], ["creates a personal Todo linked to the board card", "repeated requests use the saved card-to-Todo association"],
            "A same-user ToolBox host, a resolvable project board, and the personal Todo store must be available."),

        Create("host.stop", "Stop the existing background task host.", EmptyObjectSchema(),
            [], ["stops the background ToolBox host if running"], []),

        Create("capabilities.list", "List the static locally allowed capability set.", EmptyObjectSchema(),
            ["[] (capability objects)"], ["returns static metadata only; does not start the host or read user data"], []),

        Create("capabilities.get", "Read static metadata for one exact capability name.", ObjectSchema(
            new Dictionary<string, object?> { ["name"] = new { type = "string", minLength = 1 } }, ["name"]),
            ["name", "command", "description", "inputSchema", "outputFields", "effects", "availability"],
            ["returns static metadata only; does not start the host or read user data"], []),

        Create("ocr.file", "Recognize text from one explicitly named local image.", ObjectSchema(
            new Dictionary<string, object?>
            {
                ["path"] = new { type = "string", format = "absolute-local-path", maxLength = 32768, description = "Absolute path to a local image file; network paths and URIs are not accepted." },
                ["language"] = new { type = "string", minLength = 1, maxLength = 128, pattern = "^[a-z][a-z0-9_]{0,31}(\\+[a-z][a-z0-9_]{0,31})*$", @default = "chi_sim+eng" }
            }, ["path"]),
            ["engine", "text", "lines[].text", "lines[].confidence", "lines[].rect"], ["reads the specified local image", "runs local OCR", "does not access the network"],
            ["A local OCR engine and the requested language data are checked only when ocr.file is invoked; this listing does not check availability."]),

        Create("screenshot.search", "Search existing screenshot metadata with stable ordinal pagination.", ObjectSchema(
            new Dictionary<string, object?>
            {
                ["tag"] = new { type = "string", minLength = 1, maxLength = 4096 },
                ["minRating"] = new { type = "integer", minimum = 0, maximum = 5 },
                ["colorHue"] = new { type = "integer", minimum = 0, maximum = 359 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = 100, @default = 20 },
                ["cursor"] = new { type = "string", minLength = 17, maxLength = 17, pattern = "^[0-9]{17}$", description = "lastCacheKey from the previous page; continue in ordinal public CacheKey order." }
            }),
            ["items[].cacheKey", "items[].tags", "items[].rating", "items[].notes", "items[].taggedAt", "nextCursor"], ["reads existing screenshot metadata only"],
            ["The ToolBox host and screenshot cache metadata are checked only when screenshot.search is invoked; this listing does not check availability."])
    ];

    public static IReadOnlyList<AgentCapability> All => Capabilities;

    public static bool TryGet(string name, out AgentCapability capability)
    {
        foreach (var candidate in Capabilities)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                capability = candidate;
                return true;
            }
        }

        capability = null!;
        return false;
    }

    private static AgentCapability Create(string name, string description, JsonElement inputSchema,
        string[] outputFields, string[] effects, string[] requirements) =>
        new(name, CommandSyntax(name), description, inputSchema, outputFields, effects,
            new AgentCapabilityAvailability(false, requirements));

    private static string CommandSyntax(string name) => "toolbox-cli " + name.Replace('.', ' ') + (name switch
    {
        "task.create" or "ocr.file" or "screenshot.search" => " --input <json-file|->",
        "task.update" => " <id> --input <json-file|-> (id is positional; omit it from JSON)",
        "task.list" => " [--status <status>]",
        "project.register" => " --path <directory> [--init] [--replace-path]",
        "project.get" or "project.unregister" => " [--project <project>] [--cwd <directory>]",
        "board.get" or "board.column.list" => " [--project <project>] [--cwd <directory>]",
        "board.column.create" => " [--project <project>] [--cwd <directory>] --title <title> [--order <n>] [--revision <revision>]",
        "board.column.update" => " [--project <project>] [--cwd <directory>] --id <uuid> --input <json-file|-> [--revision <revision>]",
        "board.column.delete" => " [--project <project>] [--cwd <directory>] --id <uuid> [--target-column-id <uuid>] [--revision <revision>]",
        "board.column.move" => " [--project <project>] [--cwd <directory>] --id <uuid> --order <n> [--revision <revision>]",
        "board.card.get" or "board.card.delete" or "board.card.restore" or "board.card.add-to-todo" => " [--project <project>] [--cwd <directory>] --id <uuid> [--revision <revision>]",
        "board.card.update" => " [--project <project>] [--cwd <directory>] --id <uuid> --input <json-file|-> [--revision <revision>]",
        "board.card.create" => " [--project <project>] [--cwd <directory>] --input <json-file|->",
        "board.card.move" => " [--project <project>] [--cwd <directory>] --id <uuid> --target-column-id <uuid> [--order <n>] [--revision <revision>]",
        "board.card.list" => " [--project <project>] [--cwd <directory>] [--column-id <uuid>] [--include-deleted]",
        "capabilities.get" => " <name>",
        "task.get" or "task.complete" or "task.reopen" or "task.delete" or "task.restore" => " <id>",
        _ => ""
    });

    private static AgentCapability LocalBoardCapability(string name, string description, JsonElement inputSchema,
        string[] outputFields, string[] effects, string requirement) =>
        Create(name, description, inputSchema, outputFields, effects, [requirement]);

    private static Dictionary<string, object?> ProjectContextProperties() => new()
    {
        ["project"] = new { type = "string", minLength = 1 },
        ["cwd"] = new { type = "string", format = "absolute-path", minLength = 1 }
    };

    private static Dictionary<string, object?> WithProjectContext(string[] fields)
    {
        var properties = ProjectContextProperties();
        foreach (string field in fields)
        {
            properties[field] = field switch
            {
                "id" or "columnId" or "targetColumnId" => new { type = "string", format = "uuid" },
                "title" => new { type = "string", minLength = 1 },
                "description" => new { type = "string" },
                "priority" => new { type = "integer", minimum = 0, maximum = 2 },
                "tags" => new { type = "array", items = new { type = "string" } },
                "dueDate" => new { type = new[] { "string", "null" }, format = "date-time" },
                "order" => new { type = "integer" },
                "revision" => new { type = "integer", format = "int64" },
                "includeDeleted" => new { type = "boolean", @default = false },
                _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown board schema field.")
            };
        }
        return properties;
    }

    private static JsonElement EmptyObjectSchema() => ObjectSchema(new Dictionary<string, object?>());

    private static JsonElement ObjectSchema(Dictionary<string, object?> properties, string[]? required = null,
        string[]? anyOf = null)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required is { Length: > 0 }) schema["required"] = required;
        if (anyOf is { Length: > 0 }) schema["anyOf"] = anyOf.Select(field => new { required = new[] { field } }).ToArray();
        return JsonSerializer.SerializeToElement(schema, TaskProtocol.Options);
    }

    private static object TaskIdSchema() => new { type = "string", pattern = "^[A-Fa-f0-9]{12}$" };
}
