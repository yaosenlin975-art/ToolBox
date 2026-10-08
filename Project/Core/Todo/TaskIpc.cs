using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ToolBox.Core.Todo;

public static class TaskIpc
{
    private const int ErrorFileNotFound = 2;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static string? TestSuffix
    {
        get
        {
#if DEBUG
            var root = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
            if (string.IsNullOrEmpty(root)) return null;
            var normalized = Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
#else
            return null;
#endif
        }
    }

    public static string PipeName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new TaskCommandException("ACCESS_DENIED", "Could not determine the current user.");
            return $"ToolBox.Tasks.v1.{sid}" + (TestSuffix is { } suffix ? $".{suffix}" : "");
        }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The IPC connection closed before a complete message arrived.");

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var count = newline < 0 ? read : newline;
            if (message.Length + count > TaskProtocol.MaxBytes)
                throw new TaskCommandException("RESULT_TOO_LARGE", "The IPC message exceeds 4 MiB.");
            if (count > 0) message.Write(buffer, 0, count);
            if (newline < 0) continue;
            if (newline != read - 1)
                throw new TaskCommandException("PROTOCOL_ERROR", "Only one IPC message is allowed per connection.");

            var bytes = message.ToArray();
            if (bytes.Length > 0 && bytes[^1] == '\r') Array.Resize(ref bytes, bytes.Length - 1);
            if (bytes.Length == 0) throw new TaskCommandException("PROTOCOL_ERROR", "The IPC message is empty.");
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind == JsonValueKind.Null)
                    throw new TaskCommandException("PROTOCOL_ERROR", "The IPC message root cannot be null.");
                try { TaskProtocol.RejectDuplicateProperties(document.RootElement); }
                catch (TaskCommandException) { throw new TaskCommandException("PROTOCOL_ERROR", "The IPC message contains duplicate JSON properties."); }
                return JsonSerializer.Deserialize<T>(document.RootElement, TaskProtocol.Options)
                    ?? throw new TaskCommandException("PROTOCOL_ERROR", "The IPC message is empty.");
            }
            catch (JsonException)
            {
                throw new TaskCommandException("PROTOCOL_ERROR", "The IPC message is not valid JSON.");
            }
        }
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var frame = SerializeFrame(value);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<TaskResponse> SendAsync(TaskRequest request, bool startHost, CancellationToken cancellationToken)
    {
        TaskProtocol.Validate(request);
        var frame = SerializeFrame(request);
        NamedPipeClientStream? pipe;
        try
        {
            pipe = await ConnectAsync(startHost, request.Operation == "host.stop", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TaskCommandException("TIMEOUT", "The request was canceled before it was sent.");
        }

        if (pipe is null) return TaskResponse.Success(request);
        await using var ownedPipe = pipe;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(RequestTimeout);
            try
            {
                await pipe.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                var response = await ReadAsync<TaskResponse>(pipe, timeout.Token).ConfigureAwait(false);
                if (response.ProtocolVersion != 1 || !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
                    throw new TaskCommandException("PROTOCOL_ERROR", "The host returned a response for a different request.");
                return response;
            }
            catch (OperationCanceledException)
            {
                throw new TaskCommandException("TIMEOUT", "The request was sent but no reliable response arrived; its result is unknown. Query the task before retrying.");
            }
            catch (IOException)
            {
                throw new TaskCommandException("HOST_UNAVAILABLE", "The connection closed after the request was sent; its result is unknown. Query the task before retrying.");
            }
            catch (ObjectDisposedException)
            {
                throw new TaskCommandException("HOST_UNAVAILABLE", "The connection closed after the request was sent; its result is unknown. Query the task before retrying.");
            }
            catch (TaskCommandException ex) when (ex.Code is "PROTOCOL_ERROR" or "RESULT_TOO_LARGE")
            {
                throw new TaskCommandException(ex.Code, $"{ex.Message} The request may have completed; query the task before retrying.");
            }
        }
    }

    private static byte[] SerializeFrame<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, TaskProtocol.Options);
        if (json.Length > TaskProtocol.MaxBytes)
            throw new TaskCommandException("RESULT_TOO_LARGE", "The IPC message exceeds 4 MiB.");
        var frame = new byte[json.Length + 1];
        Buffer.BlockCopy(json, 0, frame, 0, json.Length);
        frame[^1] = (byte)'\n';
        return frame;
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(bool startHost, bool stopWhenMissing, CancellationToken cancellationToken)
    {
        var pipeName = PipeName;
        var timer = Stopwatch.StartNew();
        var hostStarted = false;
        while (timer.Elapsed < StartupTimeout)
        {
            var remaining = StartupTimeout - timer.Elapsed;
            var attempt = (int)Math.Clamp(Math.Ceiling(Math.Min(remaining.TotalMilliseconds, 500)), 1, 500);
            try
            {
                return await TryConnectAsync(pipeName, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (!startHost)
                {
                    if (stopWhenMissing && PipeDoesNotExist(pipeName)) return null;
                }
                else if (!hostStarted && PipeDoesNotExist(pipeName))
                {
                    StartHost();
                    hostStarted = true;
                }
            }
            catch (IOException)
            {
                if (PipeDoesNotExist(pipeName))
                {
                    if (!startHost && stopWhenMissing) return null;
                    if (startHost && !hostStarted)
                    {
                        StartHost();
                        hostStarted = true;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                throw new TaskCommandException("ACCESS_DENIED", "Access to the ToolBox task host was denied.");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TaskCommandException("HOST_UNAVAILABLE", "The ToolBox task host did not become available within 10 seconds.");
    }

    private static bool PipeDoesNotExist(string pipeName) =>
        !WaitNamedPipeW($@"\\.\pipe\{pipeName}", 0) && Marshal.GetLastWin32Error() == ErrorFileNotFound;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipeW(string pipeName, uint timeoutMilliseconds);

    private static async Task<NamedPipeClientStream> TryConnectAsync(string pipeName, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void StartHost()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "ToolBox.exe");
        if (!File.Exists(executable))
            throw new TaskCommandException("HOST_UNAVAILABLE", "ToolBox.exe was not found beside the CLI.");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                // ShellExecute launches the exact sibling executable without inheriting the
                // Agent's redirected handles. No command interpreter or elevation verb is used.
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--task-host");
            using var process = Process.Start(startInfo);
            if (process is null) throw new TaskCommandException("HOST_UNAVAILABLE", "The ToolBox task host could not be started.");
        }
        catch (TaskCommandException) { throw; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new TaskCommandException("HOST_UNAVAILABLE", "The ToolBox task host could not be started.");
        }
    }
}
