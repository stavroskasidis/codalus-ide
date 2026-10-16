using System.Text.Json.Nodes;

// A NuGet credential-provider plugin (NuGet's cross-platform plugin protocol, version 2) for the package sign-in
// tests. It knows one source's credentials (FAKE_NUGET_PROVIDER_CREDENTIALS, "user:password") and gives them out
// non-interactively only after an interactive sign-in, which "shows" a device code through NuGet's log and leaves a
// marker file (FAKE_NUGET_PROVIDER_STATE).
if (!args.Contains("-Plugin", StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Run by NuGet with -Plugin.");
    return 1;
}

var state = Environment.GetEnvironmentVariable("FAKE_NUGET_PROVIDER_STATE") ?? Path.Combine(Path.GetTempPath(), "fake-nuget-provider.signed-in");
var credentials = (Environment.GetEnvironmentVariable("FAKE_NUGET_PROVIDER_CREDENTIALS") ?? "user:secret").Split(':', 2);
var output = Console.Out;
var trace = Environment.GetEnvironmentVariable("FAKE_NUGET_PROVIDER_LOG"); // every message in and out, for debugging
var gate = new object();
var nextId = 0;

void Send(string id, string type, string method, JsonObject payload)
{
    var message = new JsonObject { ["RequestId"] = id, ["Type"] = type, ["Method"] = method, ["Payload"] = payload };
    lock (gate)
    {
        if (trace is not null)
        {
            File.AppendAllText(trace, "> " + message.ToJsonString() + Environment.NewLine);
        }

        output.WriteLine(message.ToJsonString());
        output.Flush();
    }
}

string NewId() => "fake-" + Interlocked.Increment(ref nextId);

// The handshake is symmetric: each side asks the other.
Send(NewId(), "Request", "Handshake", new JsonObject { ["ProtocolVersion"] = "2.0.0", ["MinimumProtocolVersion"] = "1.0.0" });

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    if (trace is not null)
    {
        File.AppendAllText(trace, "< " + line + Environment.NewLine);
    }

    if (JsonNode.Parse(line) is not JsonObject message || Value(message, "Type") != "Request")
    {
        continue; // responses to our requests
    }

    var id = Value(message, "RequestId") ?? NewId();
    var method = Value(message, "Method") ?? string.Empty;
    var payload = message.FirstOrDefault(p => p.Key.Equals("Payload", StringComparison.OrdinalIgnoreCase)).Value as JsonObject ?? [];
    switch (method)
    {
        case "Handshake":
            Send(id, "Response", method, new JsonObject { ["ResponseCode"] = "Success", ["ProtocolVersion"] = "2.0.0" });
            break;
        case "GetOperationClaims":
            Send(id, "Response", method, new JsonObject { ["ResponseCode"] = "Success", ["Claims"] = new JsonArray("Authentication") });
            break;
        case "GetAuthenticationCredentials":
            var interactive = !string.Equals(Value(payload, "IsNonInteractive"), "true", StringComparison.OrdinalIgnoreCase);
            if (interactive)
            {
                Send(NewId(), "Request", "Log", new JsonObject
                {
                    ["LogLevel"] = "Minimal",
                    ["Message"] = "[FakeCredentialProvider] To sign in, use a web browser to open the page https://login.example.invalid/device and enter the code FAKE-1234 to authenticate.",
                });
                File.WriteAllText(state, "signed in");
            }

            Send(id, "Response", method, interactive || File.Exists(state)
                ? new JsonObject { ["ResponseCode"] = "Success", ["Username"] = credentials[0], ["Password"] = credentials[^1], ["AuthenticationTypes"] = new JsonArray("Basic") }
                : new JsonObject { ["ResponseCode"] = "NotFound", ["Message"] = "Sign in first." });
            break;
        case "Close":
            return 0;
        default:
            // Initialize, SetLogLevel, MonitorNuGetProcessExit, SetCredentials…
            Send(id, "Response", method, new JsonObject { ["ResponseCode"] = "Success" });
            break;
    }
}

return 0;

static string? Value(JsonObject json, string name) =>
    json.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
