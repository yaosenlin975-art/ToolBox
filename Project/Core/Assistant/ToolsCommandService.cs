using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToolBox.Core.Tools;
using ToolBox.Core.Todo;

namespace ToolBox.Core.Assistant;

public sealed class ToolsCommandService
{
    private const int MaxJobs = 256;
    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly Func<bool> isEnabled;
    private readonly Func<ToolRegistry> registryFactory;
    private readonly string appDataDirectory;
    private readonly string jobsPath;
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Job> requests = new(StringComparer.Ordinal);
    private ToolRegistry? registry;
    private bool loaded;

    public ToolsCommandService(Func<bool> isEnabled, Func<ToolRegistry> registryFactory, string? appDataDirectory = null)
    {
        this.isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        this.registryFactory = registryFactory ?? throw new ArgumentNullException(nameof(registryFactory));
        this.appDataDirectory = Path.GetFullPath(appDataDirectory ?? GetDefaultAppDataDirectory());
        jobsPath = Path.Combine(this.appDataDirectory, "assistant-tool-jobs.json");
    }

    public async Task<TaskResponse> ExecuteAsync(TaskRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskProtocol.Validate(request);

            return request.Operation switch
            {
                "tools.list" => List(request),
                "tools.get" => Get(request),
                "tools.call" => await CallAsync(request).ConfigureAwait(false),
                "tools.result" => Result(request),
                "tools.cancel" => Cancel(request),
                _ => TaskResponse.Failure(request, "INVALID_ARGUMENT", "Unknown tools operation.", "operation")
            };
        }
        catch (TaskCommandException ex)
        {
            return TaskResponse.Failure(request, ex.Code, ex.Message, ex.Field);
        }
        catch (OperationCanceledException)
        {
            return TaskResponse.Failure(request, "TIMEOUT", "The command was canceled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TaskResponse.Failure(request, "IO_ERROR", "The tool job index could not be accessed.");
        }
        catch (Exception)
        {
            return TaskResponse.Failure(request, "INTERNAL_ERROR", "The tools command could not be completed.");
        }
    }

    public Task CancelAllAsync()
    {
        lock (gate)
        {
            EnsureLoadedLocked();
            foreach (var job in jobs.Values.Where(job => IsActive(job.Status)))
                CancelSafely(job.Cancellation);
        }
        return Task.CompletedTask;
    }

    private TaskResponse List(TaskRequest request)
    {
        if (!isEnabled()) return AssistantDisabled(request);
        ToolInfo[] tools = GetRegistry().GetAllTools().OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
        return TaskResponse.Success(request, new
        {
            assistantEnabled = true,
            requiresAssistantEnabled = true,
            tools = tools.Select(ToToolDescription).ToArray()
        });
    }

    private TaskResponse Get(TaskRequest request)
    {
        if (!isEnabled()) return AssistantDisabled(request);
        ToolRegistry current = GetRegistry();
        if (!current.TryGetTool(request.Id!, out ToolInfo? tool) || tool is null)
            return TaskResponse.Failure(request, "NOT_FOUND", "The requested tool was not found.", "id");
        return TaskResponse.Success(request, new
        {
            assistantEnabled = true,
            requiresAssistantEnabled = true,
            tool = ToToolDescription(tool)
        });
    }

    private async Task<TaskResponse> CallAsync(TaskRequest request)
    {
        if (!isEnabled()) return AssistantDisabled(request);

        string fingerprint = Fingerprint(request.Id!, request.Payload!.Value);
        string requestKey = Guid.Parse(request.RequestId).ToString("D");
        lock (gate)
        {
            EnsureLoadedLocked();
            if (!isEnabled()) return AssistantDisabled(request);
            if (requests.TryGetValue(requestKey, out Job? existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return TaskResponse.Failure(request, "CONFLICT", "requestId was already used with different tool arguments.");
                return TaskResponse.Success(request, new { callId = existing.CallId, status = existing.Status });
            }
        }

        ToolRegistry current = GetRegistry();
        if (!current.TryGetTool(request.Id!, out ToolInfo? tool) || tool is null)
            return TaskResponse.Failure(request, "NOT_FOUND", "The requested tool was not found.", "id");
        var args = ReadArguments(request.Payload!.Value, tool);
        Job job;

        lock (gate)
        {
            EnsureLoadedLocked();
            if (!isEnabled()) return AssistantDisabled(request);

            if (requests.TryGetValue(requestKey, out Job? existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return TaskResponse.Failure(request, "CONFLICT", "requestId was already used with different tool arguments.");
                return TaskResponse.Success(request, new { callId = existing.CallId, status = existing.Status });
            }

            MakeRoomLocked();
            if (jobs.Count >= MaxJobs)
                return TaskResponse.Failure(request, "JOB_LIMIT", "The active tool job limit has been reached.");

            job = new Job
            {
                CallId = Guid.NewGuid().ToString("D"),
                RequestId = requestKey,
                Fingerprint = fingerprint,
                ToolName = request.Id!,
                Status = "queued",
                CreatedAt = DateTimeOffset.UtcNow,
                Arguments = args
            };
            jobs.Add(job.CallId, job);
            requests.Add(job.RequestId, job);
            try
            {
                PersistLocked();
            }
            catch
            {
                jobs.Remove(job.CallId);
                requests.Remove(job.RequestId);
                job.Cancellation.Dispose();
                throw;
            }

            job.Work = Task.Run(() => RunJobAsync(job, current));
        }

        await Task.Yield();
        return TaskResponse.Success(request, new { callId = job.CallId, status = job.Status });
    }

    private TaskResponse Result(TaskRequest request)
    {
        string callId = Guid.Parse(request.Id!).ToString("D");
        lock (gate)
        {
            EnsureLoadedLocked();
            if (!jobs.TryGetValue(callId, out Job? job))
                return TaskResponse.Failure(request, "NOT_FOUND", "The tool call was not found.", "id");
            return CreateJobResponse(request, job);
        }
    }

    private TaskResponse Cancel(TaskRequest request)
    {
        string callId = Guid.Parse(request.Id!).ToString("D");
        lock (gate)
        {
            EnsureLoadedLocked();
            if (!jobs.TryGetValue(callId, out Job? job))
                return TaskResponse.Failure(request, "NOT_FOUND", "The tool call was not found.", "id");
            if (IsActive(job.Status)) CancelSafely(job.Cancellation);
            return TaskResponse.Success(request, new { callId = job.CallId, status = job.Status, cancellationRequested = job.Cancellation.IsCancellationRequested });
        }
    }

    private async Task RunJobAsync(Job job, ToolRegistry current)
    {
        lock (gate)
        {
            if (job.Cancellation.IsCancellationRequested)
            {
                job.Status = "canceled";
                PersistBestEffortLocked();
                job.Arguments.Clear();
                job.Cancellation.Dispose();
                return;
            }
            job.Status = "running";
            PersistBestEffortLocked();
        }

        try
        {
            string result = await current.ExecuteAsync(job.ToolName, job.Arguments, job.Cancellation.Token).ConfigureAwait(false);
            var response = TaskResponse.Success(new TaskRequest { RequestId = job.RequestId }, new { result });
            if (Encoding.UTF8.GetByteCount(result) > TaskProtocol.MaxBytes ||
                JsonSerializer.SerializeToUtf8Bytes(response, TaskProtocol.Options).Length > TaskProtocol.MaxBytes)
            {
                lock (gate)
                {
                    job.Status = "failed";
                    job.ErrorCode = "RESULT_TOO_LARGE";
                    job.Error = "The tool result exceeds the 4 MiB response limit.";
                }
            }
            else if (TryGetBusinessError(result) is { } businessError)
            {
                lock (gate)
                {
                    job.Status = "failed";
                    job.ErrorCode = "TOOL_EXECUTION_FAILED";
                    job.Error = businessError;
                }
            }
            else
            {
                lock (gate)
                {
                    job.Status = "completed";
                    job.Result = result;
                }
            }
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            lock (gate) job.Status = "canceled";
        }
        catch (Exception)
        {
            lock (gate)
            {
                job.Status = "failed";
                job.ErrorCode = "TOOL_EXECUTION_FAILED";
                job.Error = "The tool failed while executing.";
            }
        }
        finally
        {
            lock (gate)
            {
                PersistBestEffortLocked();
                job.Arguments.Clear();
                job.Cancellation.Dispose();
            }
        }
    }

    private TaskResponse CreateJobResponse(TaskRequest request, Job job)
    {
        object data = job.Status switch
        {
            "completed" => new { callId = job.CallId, status = job.Status, result = job.Result, error = (string?)null, errorCode = (string?)null },
            "failed" => new { callId = job.CallId, status = job.Status, result = (string?)null, error = job.Error, errorCode = job.ErrorCode },
            _ => new { callId = job.CallId, status = job.Status, result = (string?)null, error = (string?)null, errorCode = (string?)null }
        };
        if (job.Status == "failed")
            return new TaskResponse
            {
                RequestId = request.RequestId,
                Ok = false,
                Data = data,
                Error = new TaskError(job.ErrorCode ?? "TOOL_EXECUTION_FAILED", job.Error ?? "The tool failed while executing.")
            };
        return TaskResponse.Success(request, data);
    }

    private ToolRegistry GetRegistry()
    {
        lock (gate)
            return registry ??= registryFactory() ?? throw new InvalidOperationException("The tool registry factory returned null.");
    }

    private void EnsureLoadedLocked()
    {
        if (loaded) return;
        if (!File.Exists(jobsPath))
        {
            loaded = true;
            return;
        }
        try
        {
            var info = new FileInfo(jobsPath);
            if (info.Length > 1024 * 1024) throw new TaskCommandException("DATA_CORRUPT", "The tool job index is invalid.");
            byte[] bytes = File.ReadAllBytes(jobsPath);
            using JsonDocument document = JsonDocument.Parse(bytes);
            TaskProtocol.RejectDuplicateProperties(document.RootElement);
            PersistedState state = JsonSerializer.Deserialize<PersistedState>(bytes, StorageOptions)
                ?? throw new JsonException("Missing job state.");
            if (state.Version != 1 || state.Jobs is null || state.Jobs.Length > MaxJobs) throw new JsonException("Invalid job state.");
            foreach (PersistedJob saved in state.Jobs)
            {
                if (saved is null || saved.CallId is not { Length: 36 } callId || !Guid.TryParseExact(callId, "D", out _) ||
                    saved.RequestId is not { Length: > 0 } savedRequestId || !Guid.TryParse(savedRequestId, out _) ||
                    !IsToolName(saved.ToolName) || saved.Fingerprint is not { Length: 64 } fingerprint ||
                    fingerprint.Any(ch => !Uri.IsHexDigit(ch)) || jobs.ContainsKey(callId))
                    throw new JsonException("Invalid job record.");
                string requestId = Guid.Parse(savedRequestId).ToString("D");
                if (requests.ContainsKey(requestId)) throw new JsonException("Duplicate job request ID.");
                var job = new Job
                {
                    CallId = callId,
                    RequestId = requestId,
                    Fingerprint = fingerprint,
                    ToolName = saved.ToolName,
                    Status = "unknown",
                    CreatedAt = saved.CreatedAt
                };
                jobs.Add(job.CallId, job);
                requests.Add(job.RequestId, job);
            }
            loaded = true;
        }
        catch (TaskCommandException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            jobs.Clear();
            requests.Clear();
            throw new TaskCommandException(ex is IOException or UnauthorizedAccessException ? "IO_ERROR" : "DATA_CORRUPT",
                "The tool job index could not be read.");
        }
    }

    private void MakeRoomLocked()
    {
        foreach (Job old in jobs.Values.Where(job => !IsActive(job.Status)).OrderBy(job => job.CreatedAt).ToArray())
        {
            if (jobs.Count < MaxJobs) break;
            jobs.Remove(old.CallId);
            requests.Remove(old.RequestId);
        }
    }

    private void PersistLocked()
    {
        Directory.CreateDirectory(appDataDirectory);
        var state = new PersistedState
        {
            Version = 1,
            Jobs = jobs.Values.OrderBy(job => job.CreatedAt).Select(job => new PersistedJob
            {
                CallId = job.CallId,
                RequestId = job.RequestId,
                Fingerprint = job.Fingerprint,
                ToolName = job.ToolName,
                Status = job.Status,
                CreatedAt = job.CreatedAt
            }).ToArray()
        };
        string temporaryPath = jobsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(state, StorageOptions));
            File.Move(temporaryPath, jobsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void PersistBestEffortLocked()
    {
        try { PersistLocked(); }
        catch (Exception) { }
    }

    private static Dictionary<string, object> ReadArguments(JsonElement payload, ToolInfo tool)
    {
        var parameters = tool.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        if (tool.Handler is not MethodInfo method)
            throw new TaskCommandException("ENGINE_UNAVAILABLE", "The tool parameter schema is unavailable.");
        var parameterTypes = method.GetParameters()
            .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .ToDictionary(parameter => parameter.Name!, parameter => parameter.ParameterType, StringComparer.Ordinal);
        var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            if (!parameters.TryGetValue(property.Name, out ToolParamInfo? parameter))
                throw new TaskCommandException("INVALID_ARGUMENT", "Unknown tool parameter.", property.Name);

            object value = property.Value.ValueKind switch
            {
                JsonValueKind.String when parameter.JsonType == "string" => property.Value.GetString()!,
                JsonValueKind.Number when parameter.JsonType == "number" => property.Value.Clone(),
                JsonValueKind.True or JsonValueKind.False when parameter.JsonType == "boolean" => property.Value.GetBoolean(),
                JsonValueKind.Null when parameter.JsonType == "null" => property.Value,
                _ => throw new TaskCommandException("INVALID_ARGUMENT", "The tool parameter has the wrong JSON type.", property.Name)
            };
            try { _ = JsonSerializer.Deserialize(property.Value.GetRawText(), parameterTypes[property.Name]); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException or OverflowException)
            {
                throw new TaskCommandException("INVALID_ARGUMENT", "The tool parameter has the wrong JSON type.", property.Name);
            }
            arguments.Add(property.Name, value);
        }

        ToolParamInfo? missing = tool.Parameters.FirstOrDefault(parameter => parameter.Required && !arguments.ContainsKey(parameter.Name));
        if (missing is not null) throw new TaskCommandException("INVALID_ARGUMENT", "A required tool parameter is missing.", missing.Name);
        return arguments;
    }

    private static object ToToolDescription(ToolInfo tool)
    {
        using JsonDocument schema = JsonDocument.Parse(tool.ToJsonSchema());
        return new
        {
            name = tool.Name,
            description = tool.Description,
            isAsync = tool.IsAsync,
            parameters = tool.Parameters.Select(parameter => new
            {
                name = parameter.Name,
                description = parameter.Description,
                required = parameter.Required,
                type = parameter.JsonType
            }).ToArray(),
            inputSchema = schema.RootElement.Clone()
        };
    }

    private static string Fingerprint(string toolName, JsonElement payload)
    {
        var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in payload.EnumerateObject()) values[property.Name] = property.Value.Clone();
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new { toolName, arguments = values }, TaskProtocol.Options);
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static string? TryGetBusinessError(string result)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(result);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("success", out JsonElement success) &&
                success.ValueKind == JsonValueKind.False)
                return result;
        }
        catch (JsonException) { }

        string[] failurePrefixes = [
            "安全限制:", "文件不存在:", "目录不存在:", "读取失败:", "写入失败:", "创建失败:", "删除失败:",
            "复制失败:", "移动失败:", "搜索失败:", "路径不存在:", "用户取消删除操作:", "用户取消移动操作:", "未找到 Todo:",
            "未找到最近的截图，请先截图后再调用。"
        ];
        return failurePrefixes.Any(prefix => result.StartsWith(prefix, StringComparison.Ordinal)) ? result : null;
    }

    private static bool IsToolName(string? name) => name is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetter(name[0]) && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or '-');

    private static bool IsActive(string status) => status is "queued" or "running";

    private static void CancelSafely(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        catch (AggregateException) { }
    }

    private static string GetDefaultAppDataDirectory()
    {
#if DEBUG
        string? testRoot = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot)) return Path.Combine(testRoot, "assistant");
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox");
    }

    private static TaskResponse AssistantDisabled(TaskRequest request) =>
        TaskResponse.Failure(request, "ASSISTANT_DISABLED", "The Assistant is disabled.");

    private sealed class Job
    {
        public string CallId { get; init; } = "";
        public string RequestId { get; init; } = "";
        public string Fingerprint { get; init; } = "";
        public string ToolName { get; init; } = "";
        public string Status { get; set; } = "queued";
        public DateTimeOffset CreatedAt { get; init; }
        public Dictionary<string, object> Arguments { get; set; } = new(StringComparer.Ordinal);
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Work { get; set; }
        public string? Result { get; set; }
        public string? ErrorCode { get; set; }
        public string? Error { get; set; }
    }

    private sealed class PersistedState
    {
        public int Version { get; set; }
        public PersistedJob[] Jobs { get; set; } = [];
    }

    private sealed class PersistedJob
    {
        public string CallId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
    }
}
