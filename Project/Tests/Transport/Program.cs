using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ToolBox.Core.Todo;

internal static class Program
{
    private static int checks;

    private static async Task<int> Main()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "ToolBox.TransportChecks", Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
        Directory.CreateDirectory(testRoot);
        Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", testRoot);
        try
        {
            CheckProtocolExitCodes();
            CheckDateValidation();
            CheckAgentValidation();
            await CheckTransportFramesAsync();
            await CheckDisconnectedCreateAsync();
            Console.WriteLine($"PASS ({checks} checks)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", previousRoot);
            try { Directory.Delete(testRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CheckProtocolExitCodes()
    {
        Check(TaskResponse.Success(new TaskRequest()).ExitCode == 0, "success exit code");
        Check(ExitCode("INVALID_ARGUMENT") == 2, "invalid argument exit code");
        Check(ExitCode("NOT_FOUND") == 3, "not found exit code");
        Check(ExitCode("CONFLICT") == 4 && ExitCode("RESULT_TOO_LARGE") == 4, "conflict exit codes");
        Check(ExitCode("HOST_UNAVAILABLE") == 5 && ExitCode("TIMEOUT") == 5 && ExitCode("ACCESS_DENIED") == 5,
            "host and access exit codes");
        Check(ExitCode("IO_ERROR") == 6 && ExitCode("DATA_CORRUPT") == 6, "storage exit codes");
        Check(ExitCode("PROTOCOL_ERROR") == 7 && ExitCode("INTERNAL_ERROR") == 7 && ExitCode("unknown") == 7,
            "protocol and fallback exit codes");
    }

    private static int ExitCode(string code) => TaskResponse.Failure(null, code, "test").ExitCode;

    private static void CheckAgentValidation()
    {
        Check(ExitCode("ENGINE_UNAVAILABLE") == 4, "unavailable OCR engine exit code");
        foreach (var (operation, json) in new[] {
            ("ocr.file", "{}"), ("ocr.file", "{\"path\":\"relative.png\"}"),
            ("ocr.file", "{\"path\":\"C:\\\\test.png\",\"language\":\"../eng\"}"),
            ("ocr.file", "{\"path\":\"C:\\\\test.png\",\"download\":true}"),
            ("screenshot.search", "{\"limit\":101}"), ("screenshot.search", "{\"minRating\":-1}"),
            ("screenshot.search", "{\"colorHue\":360}"), ("screenshot.search", "{\"cursor\":\"C:\\\\private\"}") })
        {
            bool rejected = false;
            try { TaskProtocol.Validate(new TaskRequest { Operation = operation, Payload = Element(json) }); }
            catch (TaskCommandException ex) when (ex.Code == "INVALID_ARGUMENT") { rejected = true; }
            Check(rejected, operation + " rejects " + json);
        }
        TaskProtocol.Validate(new TaskRequest { Operation = "ocr.file", Payload = Element("{\"path\":\"C:\\\\test.png\",\"language\":\"chi_sim+eng\"}") });
        TaskProtocol.Validate(new TaskRequest { Operation = "screenshot.search", Payload = Element("{}") });
        Check(true, "approved Agent operations accepted");
    }

    private static void CheckDateValidation()
    {
        MustRejectDate("\"2026-10-08T12:30:00\"", "date without timezone");
        MustRejectDate("123", "numeric date");
        MustRejectDate("true", "boolean date");
        MustRejectDate("[]", "array date");
        MustRejectDate("{}", "object date");
    }

    private static void MustRejectDate(string value, string label)
    {
        using var document = JsonDocument.Parse("{\"title\":\"test\",\"dueDate\":" + value + "}");
        bool rejected = false;
        try { TaskProtocol.ValidatePayload("task.create", document.RootElement); }
        catch (TaskCommandException ex) when (ex.Code == "INVALID_ARGUMENT") { rejected = true; }
        Check(rejected, label + " rejected");
    }

    private static async Task CheckTransportFramesAsync()
    {
        var request = new TaskRequest
        {
            Operation = "task.create",
            Payload = Element("{\"title\":\"中文标题\",\"description\":\"第一行\\n第二行\"}")
        };
        using var roundTrip = new MemoryStream();
        await TaskIpc.WriteAsync(roundTrip, request, CancellationToken.None);
        roundTrip.Position = 0;
        TaskRequest decoded = await TaskIpc.ReadAsync<TaskRequest>(roundTrip, CancellationToken.None);
        Check(decoded.Payload?.GetProperty("title").GetString() == "中文标题" &&
            decoded.Payload?.GetProperty("description").GetString() == "第一行\n第二行", "UTF-8 and embedded newline roundtrip");

        var boundary = new TaskResponse
        {
            RequestId = "00000000-0000-0000-0000-000000000001",
            Ok = true,
            Data = ""
        };
        int targetTextLength = TaskProtocol.MaxBytes - JsonSerializer.SerializeToUtf8Bytes(boundary, TaskProtocol.Options).Length;
        boundary.Data = new string('x', targetTextLength);
        byte[] exactJson = JsonSerializer.SerializeToUtf8Bytes(boundary, TaskProtocol.Options);
        Check(exactJson.Length == TaskProtocol.MaxBytes, "construct exact 4 MiB JSON payload");

        using var exact = new MemoryStream();
        await TaskIpc.WriteAsync(exact, boundary, CancellationToken.None);
        Check(exact.Length == TaskProtocol.MaxBytes + 1, "write accepts full 4 MiB JSON plus LF");
        exact.Position = 0;
        TaskResponse exactDecoded = await TaskIpc.ReadAsync<TaskResponse>(exact, CancellationToken.None);
        Check(exactDecoded.Data is JsonElement data && data.GetString()?.Length == targetTextLength,
            "read accepts full 4 MiB frame");

        boundary.Data = new string('x', targetTextLength + 1);
        await MustRejectWriteAsync(boundary, "write rejects frame over 4 MiB");
        await MustRejectReadAsync(WithNewline(JsonSerializer.SerializeToUtf8Bytes(boundary, TaskProtocol.Options)),
            "read rejects frame over 4 MiB");

        await MustRejectReadAsync(Encoding.UTF8.GetBytes("{\"protocolVersion\":1}"), "EOF without newline rejected");
        await MustRejectReadAsync([0xC3, 0x28, 0x0A], "invalid UTF-8 rejected");
        await MustRejectReadAsync(WithNewline(Encoding.UTF8.GetBytes("{")), "invalid JSON rejected");
        await MustRejectReadAsync(WithNewline(Encoding.UTF8.GetBytes("null")), "null root rejected");
        await MustRejectReadAsync(WithNewline(Encoding.UTF8.GetBytes(
            "{\"protocolVersion\":1,\"protocolVersion\":1}")), "duplicate top-level field rejected");
        await MustRejectReadAsync(WithNewline(Encoding.UTF8.GetBytes(
            "{\"protocolVersion\":1,\"requestId\":\"00000000-0000-0000-0000-000000000001\",\"operation\":\"task.create\",\"payload\":{\"title\":\"a\",\"title\":\"b\"}}")),
            "duplicate payload field rejected");
        await MustRejectReadAsync(WithNewline(Encoding.UTF8.GetBytes("{\"unknown\":1}")), "unknown top-level field rejected");
    }

    private static async Task MustRejectWriteAsync(TaskResponse value, string label)
    {
        bool rejected = false;
        try
        {
            using var stream = new MemoryStream();
            await TaskIpc.WriteAsync(stream, value, CancellationToken.None);
        }
        catch (TaskCommandException) { rejected = true; }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, label);
    }

    private static async Task MustRejectReadAsync(byte[] bytes, string label)
    {
        bool rejected = false;
        try
        {
            using var stream = new MemoryStream(bytes);
            _ = await TaskIpc.ReadAsync<TaskRequest>(stream, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { rejected = true; }
        Check(rejected, label);
    }

    private static async Task CheckDisconnectedCreateAsync()
    {
        string? suffix = TaskIpc.TestSuffix;
        string pipeName = TaskIpc.PipeName;
        Check(!string.IsNullOrWhiteSpace(suffix) && pipeName.EndsWith(suffix, StringComparison.Ordinal),
            "test root selects an isolated pipe");

        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> server = Task.Run(async () =>
        {
            using var first = NewTestPipe(pipeName);
            using var second = NewTestPipe(pipeName);
            using var firstWaitCts = CancellationTokenSource.CreateLinkedTokenSource(serverTimeout.Token);
            using var secondWaitCts = CancellationTokenSource.CreateLinkedTokenSource(serverTimeout.Token);
            Task firstWait = first.WaitForConnectionAsync(firstWaitCts.Token);
            Task secondWait = second.WaitForConnectionAsync(secondWaitCts.Token);
            ready.SetResult(true);

            Task connected = await Task.WhenAny(firstWait, secondWait);
            await connected;
            bool firstWon = connected == firstWait;
            NamedPipeServerStream active = firstWon ? first : second;
            NamedPipeServerStream standby = firstWon ? second : first;
            Task standbyWait = firstWon ? secondWait : firstWait;
            CancellationTokenSource standbyWaitCts = firstWon ? secondWaitCts : firstWaitCts;
            TaskRequest received = await TaskIpc.ReadAsync<TaskRequest>(active, serverTimeout.Token);
            bool isCreate = received.Operation == "task.create";

            await active.DisposeAsync();
            try
            {
                await standbyWait.WaitAsync(TimeSpan.FromSeconds(1));
                _ = await TaskIpc.ReadAsync<TaskRequest>(standby, serverTimeout.Token);
                return isCreate ? 2 : 0;
            }
            catch (TimeoutException)
            {
                standbyWaitCts.Cancel();
                try { await standbyWait; }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                return isCreate ? 1 : 0;
            }
        });

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var create = new TaskRequest
        {
            Operation = "task.create",
            Payload = Element("{\"title\":\"single attempt\"}")
        };
        TaskResponse? response = null;
        TaskCommandException? sendError = null;
        try { response = await TaskIpc.SendAsync(create, false, CancellationToken.None); }
        catch (TaskCommandException ex) { sendError = ex; }
        int receivedCount = await server.WaitAsync(TimeSpan.FromSeconds(10));
        Check(receivedCount == 1, "disconnected create is received once without replay");
        string? errorCode = response?.Error?.Code ?? sendError?.Code;
        string? errorMessage = response?.Error?.Message ?? sendError?.Message;
        Check(errorCode is "HOST_UNAVAILABLE" or "TIMEOUT", "disconnect returns host unavailable or timeout");
        Check(errorMessage?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true ||
            errorMessage?.Contains("结果未知", StringComparison.Ordinal) == true,
            "disconnect explains that create result is unknown");
    }

    private static NamedPipeServerStream NewTestPipe(string name) => new(name, PipeDirection.InOut, 2,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static byte[] WithNewline(byte[] json)
    {
        byte[] frame = new byte[json.Length + 1];
        json.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        return frame;
    }

    private static void Check(bool condition, string label)
    {
        checks++;
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
    }
}
