using ToolBox.Views.Chat;

var passed = 0;
Verify("valid ready request", "{\"version\":1,\"id\":\"ready-1\",\"type\":\"ready\",\"payload\":{}}", true);
Verify("valid message request", "{\"version\":1,\"id\":\"msg-1\",\"type\":\"message.send\",\"sessionId\":\"a1b2c3d4\",\"payload\":{\"text\":\"hello\"}}", true);
Verify("cancel run id is payload scoped", "{\"version\":1,\"id\":\"cancel-1\",\"type\":\"run.cancel\",\"sessionId\":\"a1b2c3d4\",\"payload\":{\"runId\":\"run-1\"}}", true);
Verify("reject unknown command", "{\"version\":1,\"id\":\"x\",\"type\":\"filesystem.read\",\"payload\":{}}", false);
Verify("reject unknown root capability", "{\"version\":1,\"id\":\"x\",\"type\":\"ready\",\"payload\":{},\"proxy\":true}", false);
Verify("reject invalid session id", "{\"version\":1,\"id\":\"x\",\"type\":\"message.send\",\"sessionId\":\"../secret\",\"payload\":{\"text\":\"x\"}}", false);
Verify("reject extra message capability", "{\"version\":1,\"id\":\"x\",\"type\":\"message.send\",\"sessionId\":\"a1b2c3d4\",\"payload\":{\"text\":\"x\",\"tools\":[\"arbitrary\"]}}", false);
Verify("reject javascript link", "{\"version\":1,\"id\":\"x\",\"type\":\"link.open\",\"payload\":{\"url\":\"javascript:alert(1)\"}}", false);
Verify("reject user-info link", "{\"version\":1,\"id\":\"x\",\"type\":\"link.open\",\"payload\":{\"url\":\"https://user:pass@example.com\"}}", false);
Verify("reject duplicate envelope field", "{\"version\":1,\"id\":\"x\",\"type\":\"ready\",\"type\":\"message.send\",\"payload\":{}}", false);

Check(AssistantWebProtocol.IsTrustedOrigin("https://toolbox.assistant.local/index.html"), "accept virtual origin");
Check(!AssistantWebProtocol.IsTrustedOrigin("https://toolbox.assistant.local.evil/index.html"), "reject origin suffix attack");
Check(!AssistantWebProtocol.IsTrustedOrigin("https://toolbox.assistant.local:444/index.html"), "reject alternate origin port");
Console.WriteLine($"AssistantWeb protocol checks passed: {passed}");

void Verify(string label, string json, bool expected)
{
    var actual = AssistantWebProtocol.TryParse(json, out var command, out _, out _);
    Check(actual == expected, label);
    if (actual && command != null) Check(command.Payload.ValueKind == System.Text.Json.JsonValueKind.Object, $"{label} payload cloned");
}

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException($"Failed: {label}");
    passed++;
}
