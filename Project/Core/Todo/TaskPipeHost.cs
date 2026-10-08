using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ToolBox.Core.Todo;

internal sealed class TaskPipeHost(Func<TaskRequest, CancellationToken, Task<TaskResponse>> handler, Action stop) : IDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private NamedPipeServerStream? listener;

    public void Start()
    {
        // Bind synchronously so another instance cannot impersonate this host during startup.
        listener = CreateListener();
        _ = RunAsync();
    }

    private static NamedPipeServerStream CreateListener()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetOwner(identity.Owner!);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // CurrentUserOnly would discard this ACL; restrict to this SID and also reject network logons.
        return NamedPipeServerStreamAcl.Create(TaskIpc.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
    }

    private async Task RunAsync()
    {
        // ponytail: one connection at a time; isolate OCR workers if native calls outliving client timeouts become a problem.
        while (!shutdown.IsCancellationRequested)
        {
            TaskRequest? request = null;
            try
            {
                using var pipe = listener!;
                await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                TaskResponse response;
                try
                {
                    request = await TaskIpc.ReadAsync<TaskRequest>(pipe, timeout.Token).ConfigureAwait(false);
                    TaskProtocol.Validate(request);
                    response = await handler(request, timeout.Token).ConfigureAwait(false);
                }
                catch (TaskCommandException ex)
                {
                    response = TaskResponse.Failure(request, ex.Code, ex.Message, ex.Field);
                }
                catch (System.Text.Json.JsonException)
                {
                    response = TaskResponse.Failure(request, "PROTOCOL_ERROR", "Invalid JSON request.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ToolBox task host] {ex}");
                    response = TaskResponse.Failure(request, "INTERNAL_ERROR", "The task operation failed.");
                }
                try
                {
                    await TaskIpc.WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false);
                }
                catch (TaskCommandException ex) when (ex.Code == "RESULT_TOO_LARGE")
                {
                    await TaskIpc.WriteAsync(pipe, TaskResponse.Failure(request, ex.Code, ex.Message), timeout.Token).ConfigureAwait(false);
                }
                if (request?.Operation == "host.stop" && response.Ok)
                {
                    stop();
                    return;
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine($"[ToolBox task pipe] {ex.Message}");
            }
            finally
            {
                listener?.Dispose();
                listener = null;
            }
            if (!shutdown.IsCancellationRequested)
            {
                try { listener = CreateListener(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ToolBox task pipe] {ex}");
                    stop();
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        shutdown.Cancel();
        listener?.Dispose();
    }
}
