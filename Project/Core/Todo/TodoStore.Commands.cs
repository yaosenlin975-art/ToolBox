using System.IO;
using System.Text.Json;

namespace ToolBox.Core.Todo;

public partial class TodoStore
{
    public async Task<TaskResponse> ExecuteTaskAsync(TaskRequest request)
    {
        var command = new TaskRequest
        {
            ProtocolVersion = request.ProtocolVersion,
            RequestId = request.RequestId,
            Operation = request.Operation,
            Id = request.Id,
            Status = request.Status,
            Payload = request.Payload is { } payload ? payload.Clone() : null,
            Arguments = request.Arguments?.ToArray()
        };

        try { TaskProtocol.Validate(command); }
        catch (TaskCommandException ex) { return TaskResponse.Failure(command, ex.Code, ex.Message, ex.Field); }

        await stateLock.WaitAsync();
        try
        {
            EnsureStorageAvailable();
            TaskResponse response = command.Operation switch
            {
                "task.list" => Success(command, items
                    .Where(item => MatchesStatus(item, command.Status))
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Select(ToTaskDto)
                    .ToArray()),
                "task.get" => GetTask(command),
                "task.create" => await CreateTaskAsync(command),
                "task.update" => await UpdateTaskAsync(command),
                "task.complete" => await CompleteTaskAsync(command),
                "task.reopen" => await ReopenTaskAsync(command),
                "task.delete" => await DeleteTaskAsync(command),
                "task.restore" => await RestoreTaskAsync(command),
                _ => throw new TaskCommandException("PROTOCOL_ERROR", "This operation is handled by the application host.", "operation")
            };
            return LimitResponse(command, response);
        }
        catch (TaskCommandException ex)
        {
            return LimitResponse(command, TaskResponse.Failure(command, ex.Code, ex.Message, ex.Field));
        }
        catch (UnauthorizedAccessException)
        {
            return TaskResponse.Failure(command, "IO_ERROR", "Todo data could not be accessed.");
        }
        catch (IOException)
        {
            return TaskResponse.Failure(command, "IO_ERROR", "Todo data could not be accessed.");
        }
        catch (Exception)
        {
            return TaskResponse.Failure(command, "INTERNAL_ERROR", "The task command failed.");
        }
        finally { stateLock.Release(); }
    }

    private TaskResponse GetTask(TaskRequest request)
    {
        var item = FindTask(request.Id!);
        return item == null
            ? throw new TaskCommandException("NOT_FOUND", "Task not found.", "id")
            : Success(request, ToTaskDto(item));
    }

    private async Task<TaskResponse> CreateTaskAsync(TaskRequest request)
    {
        var payload = request.Payload!.Value;
        string? parentId = payload.TryGetProperty("parentId", out var parentValue) && parentValue.ValueKind != JsonValueKind.Null
            ? parentValue.GetString()
            : null;
        if (parentId != null)
        {
            var parent = FindTask(parentId);
            if (parent == null) throw new TaskCommandException("NOT_FOUND", "Parent task not found.", "parentId");
            if (parent.IsTrashed) throw new TaskCommandException("CONFLICT", "A task cannot be created under a trashed parent.", "parentId");
        }

        string title = payload.GetProperty("title").GetString()!.Trim();
        string description = payload.TryGetProperty("description", out var descriptionValue) ? descriptionValue.GetString()! : "";
        int priority = payload.TryGetProperty("priority", out var priorityValue) ? priorityValue.GetInt32() : 0;
        string category = payload.TryGetProperty("category", out var categoryValue) ? categoryValue.GetString()!.Trim() : "默认";
        List<string>? tags = payload.TryGetProperty("tags", out var tagsValue)
            ? tagsValue.EnumerateArray().Select(tag => tag.GetString()!).ToList()
            : null;
        DateTime? dueDate = payload.TryGetProperty("dueDate", out var dateValue) && dateValue.ValueKind != JsonValueKind.Null
            ? TaskProtocol.ParseDate(dateValue)
            : null;

        var item = AddCore(title, description, priority, category, tags, dueDate, parentId: parentId);
        var response = PrepareWriteResponse(request, item);
        await SaveAsync();
        NotifyChanged();
        return response;
    }

    private async Task<TaskResponse> UpdateTaskAsync(TaskRequest request)
    {
        var item = FindTask(request.Id!);
        if (item == null) throw new TaskCommandException("NOT_FOUND", "Task not found.", "id");
        if (item.IsTrashed) throw new TaskCommandException("CONFLICT", "Restore the task before updating it.", "id");

        var payload = request.Payload!.Value;
        string? title = payload.TryGetProperty("title", out var titleValue) ? titleValue.GetString()!.Trim() : null;
        string? description = payload.TryGetProperty("description", out var descriptionValue) ? descriptionValue.GetString() : null;
        int? priority = payload.TryGetProperty("priority", out var priorityValue) ? priorityValue.GetInt32() : null;
        string? category = payload.TryGetProperty("category", out var categoryValue) ? categoryValue.GetString()!.Trim() : null;
        List<string>? tags = payload.TryGetProperty("tags", out var tagsValue)
            ? tagsValue.EnumerateArray().Select(tag => tag.GetString()!).ToList()
            : null;
        bool setDueDate = payload.TryGetProperty("dueDate", out var dateValue);
        DateTime? dueDate = setDueDate && dateValue.ValueKind != JsonValueKind.Null
            ? TaskProtocol.ParseDate(dateValue)
            : null;
        int? progress = payload.TryGetProperty("progress", out var progressValue) ? progressValue.GetInt32() : null;
        if (progress.HasValue && HasChildren(item.Id))
            throw new TaskCommandException("CONFLICT", "Parent progress is calculated from its children.", "progress");

        UpdateCore(item, title, description, priority, category, tags, dueDate, progress, setDueDate);
        var response = PrepareWriteResponse(request, item);
        await SaveAsync();
        NotifyChanged();
        return response;
    }

    private async Task<TaskResponse> CompleteTaskAsync(TaskRequest request)
    {
        var item = FindTask(request.Id!);
        if (item == null) throw new TaskCommandException("NOT_FOUND", "Task not found.", "id");
        if (item.IsTrashed) throw new TaskCommandException("CONFLICT", "Restore the task before completing it.", "id");
        if (!CanCompleteCore(item)) throw new TaskCommandException("CONFLICT", "Complete all active child tasks first.", "id");
        CompleteCore(item);
        var response = PrepareWriteResponse(request, item);
        await SaveAsync();
        NotifyChanged();
        return response;
    }

    private async Task<TaskResponse> ReopenTaskAsync(TaskRequest request)
    {
        var item = FindTask(request.Id!);
        if (item == null) throw new TaskCommandException("NOT_FOUND", "Task not found.", "id");
        if (item.IsTrashed) throw new TaskCommandException("CONFLICT", "Restore the task before reopening it.", "id");
        UncompleteCore(item);
        var response = PrepareWriteResponse(request, item);
        await SaveAsync();
        NotifyChanged();
        return response;
    }

    private async Task<TaskResponse> DeleteTaskAsync(TaskRequest request)
    {
        var item = FindTask(request.Id!);
        if (item == null) throw new TaskCommandException("NOT_FOUND", "Task not found.", "id");
        if (TrashSubtreeCore(item.Id))
        {
            RecalculateAncestorsCore(item.Id);
            var response = PrepareWriteResponse(request, item);
            await SaveAsync();
            NotifyChanged();
            return response;
        }
        return PrepareWriteResponse(request, item, rollbackOnOversize: false);
    }

    private async Task<TaskResponse> RestoreTaskAsync(TaskRequest request)
    {
        var item = FindTask(request.Id!);
        if (item == null) throw new TaskCommandException("NOT_FOUND", "Task not found.", "id");
        if (HasTrashedAncestor(item))
            throw new TaskCommandException("CONFLICT", "Restore the parent task first.", "id");
        if (RestoreSubtreeCore(item.Id))
        {
            RecalculateAndAncestorsCore(item.Id);
            var response = PrepareWriteResponse(request, item);
            await SaveAsync();
            NotifyChanged();
            return response;
        }
        return PrepareWriteResponse(request, item, rollbackOnOversize: false);
    }

    private TodoItem? FindTask(string id) => items.FirstOrDefault(item => item.Id == id);

    private bool HasTrashedAncestor(TodoItem item)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { item.Id };
        var parentId = item.ParentId;
        while (parentId != null)
        {
            if (!visited.Add(parentId)) return true;
            var parent = FindTask(parentId);
            if (parent == null || parent.IsTrashed) return true;
            parentId = parent.ParentId;
        }
        return false;
    }

    private static bool MatchesStatus(TodoItem item, string? status) => status switch
    {
        "pending" => !item.IsTrashed && !item.IsCompleted,
        "completed" => !item.IsTrashed && item.IsCompleted,
        "trashed" => item.IsTrashed,
        "repeating" => !item.IsTrashed && item.RepeatConfig != null,
        _ => !item.IsTrashed
    };

    private static JsonElement ToTaskDto(TodoItem item)
    {
        var repeat = item.RepeatConfig;
        return JsonSerializer.SerializeToElement(new
        {
            id = item.Id,
            parentId = item.ParentId,
            title = item.Title,
            description = item.Description,
            isCompleted = item.IsCompleted,
            priority = item.Priority,
            category = item.Category,
            tags = item.Tags.ToArray(),
            dueDate = Utc(item.DueDate),
            progress = item.Progress,
            createdAt = Utc(item.CreatedAt),
            completedAt = Utc(item.CompletedAt),
            isTrashed = item.IsTrashed,
            isRepeating = repeat != null,
            repeatConfig = repeat == null ? null : new
            {
                frequency = repeat.Frequency.ToString().ToLowerInvariant(),
                startDate = Utc(repeat.StartDate),
                endDate = Utc(repeat.EndDate),
                reminderHour = repeat.ReminderHour,
                reminderMinute = repeat.ReminderMinute,
                repeatDayOfWeek = repeat.RepeatDayOfWeek,
                repeatDayOfMonth = repeat.RepeatDayOfMonth,
                repeatMonth = repeat.RepeatMonth,
                repeatDay = repeat.RepeatDay
            },
            lastResetAt = Utc(item.LastResetAt)
        }, TaskProtocol.Options);
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    private static DateTime? Utc(DateTime? value) => value.HasValue ? Utc(value.Value) : null;

    private static TaskResponse Success(TaskRequest request, object? data)
        => TaskResponse.Success(request, data);

    private TaskResponse PrepareWriteResponse(TaskRequest request, TodoItem item, bool rollbackOnOversize = true)
    {
        var response = Success(request, ToTaskDto(item));
        if (JsonSerializer.SerializeToUtf8Bytes(response, TaskProtocol.Options).Length <= TaskProtocol.MaxBytes)
            return response;

        if (rollbackOnOversize)
        {
            try { RestoreSnapshot(committedSnapshot ?? throw new InvalidOperationException("Missing commit snapshot.")); }
            catch (Exception)
            {
                storageFailureCode = "IO_ERROR";
                storageFailureMessage = "Todo state could not be restored after rejecting an oversized result; restart the application.";
                throw new TaskCommandException(storageFailureCode, storageFailureMessage);
            }
        }
        throw new TaskCommandException("RESULT_TOO_LARGE", "The task result exceeds the 4 MiB response limit.");
    }

    private static TaskResponse LimitResponse(TaskRequest request, TaskResponse response)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(response, TaskProtocol.Options).Length <= TaskProtocol.MaxBytes)
            return response;
        return TaskResponse.Failure(request, "RESULT_TOO_LARGE", "The task result exceeds the 4 MiB response limit.");
    }
}
