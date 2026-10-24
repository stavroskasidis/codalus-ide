using System.Diagnostics;
using System.Text;
using Codalus.Platform;
using Codalus.Workbench.Wcf;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The WCF test client against samples/Wcf's CoreWCF service, built and started here on a loopback port (M42, ADR
/// 0069): its metadata found from the running program, read, and each kind of operation called over BasicHttpBinding
/// (SOAP 1.1) and WSHttpBinding without security (SOAP 1.2 with WS-Addressing).
/// </summary>
public sealed class WcfServiceIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-wcf-" + Guid.NewGuid().ToString("N"));
    private Process? _service;
    private readonly StringBuilder _output = new();

    private static CancellationToken Timeout(int minutes = 6) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromMinutes(minutes)).Token).Token;

    public ValueTask InitializeAsync()
    {
        CopyDirectory(Path.Combine(ContainerToolsTests.RepoRoot, "samples", "Wcf"), Path.Combine(_root, "Wcf"));
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        Codalus.Tests.FixtureSdk.Pin(_root);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is { HasExited: false })
        {
            ProcessTree.Kill(_service);
            await _service.WaitForExitAsync(CancellationToken.None);
        }

        _service?.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task The_test_client_calls_the_running_fixture_service()
    {
        var ct = Timeout();
        var project = Path.Combine(_root, "Wcf", "CalculatorService", "CalculatorService.csproj");
        var (exitCode, build) = await RunDotnetAsync(["build", project, "--nologo", "-v", "q"], Path.GetDirectoryName(project)!, ct);
        Assert.True(exitCode == 0, build);

        // Started as the IDE starts a program, on a free loopback port.
        var output = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", "net10.0", "CalculatorService.dll");
        var listening = await StartServiceAsync(output, ct);

        using var client = new WcfTestClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
        var found = await client.DiscoverAsync([(project, _output.ToString())], ct);
        // CoreWCF answers ?wsdl at the BasicHttpBinding endpoint only (the SOAP 1.2 one wants POSTs); it lists both.
        Assert.Equal([listening + "/CalculatorService.svc?wsdl"], found.Select(s => s.MetadataUrl));

        var document = await client.LoadAsync(found[0].MetadataUrl, ct);
        Assert.Empty(document.Warnings);
        var basic = document.Endpoints.Single(e => e.Soap == SoapVersion.Soap11);
        var ws = document.Endpoints.Single(e => e.Soap == SoapVersion.Soap12);
        Assert.True(ws.Addressing);

        // Simple values, over both bindings.
        foreach (var endpoint in new[] { basic, ws })
        {
            var add = new SoapRequest(document, endpoint, endpoint.Operations.Single(o => o.Name == "Add"));
            add.Parameters[0].Value = "2";
            add.Parameters[1].Value = "3.5";
            var sum = await client.SendAsync(add, add.BuildEnvelope(), ct);
            Assert.True(sum is { StatusCode: 200, IsFault: false }, sum.Body);
            Assert.Equal("AddResult = 5.5", Assert.Single(sum.Tree!.Children).Label);
        }

        // A data contract with an enum and a list.
        var greet = new SoapRequest(document, basic, basic.Operations.Single(o => o.Name == "Greet"));
        var person = greet.Parameters[0];
        person.Children[0].Value = "Ada";
        person.Children[1].Value = "36";
        person.Children[2].Value = "Happy";
        person.Children[3].Children[0].NewItem().Value = "math";
        var greeting = await client.SendAsync(greet, greet.BuildEnvelope(), ct);
        Assert.Equal("GreetResult = Hello Ada, 36 and happy (math)!", Assert.Single(greeting.Tree!.Children).Label);

        // An array of data contracts back, from edited XML.
        var find = new SoapRequest(document, ws, ws.Operations.Single(o => o.Name == "FindPeople"));
        find.Parameters[0].Value = "A";
        find.Parameters[1].Value = "5";
        var edited = find.BuildEnvelope().Replace("<max>5</max>", "<max>1</max>", StringComparison.Ordinal);
        var people = await client.SendAsync(find, edited, ct);
        var result = Assert.Single(people.Tree!.Children);
        Assert.Equal("Name = Ada", Assert.Single(result.Children).Children[0].Label);

        // A declared fault, over both bindings.
        foreach (var endpoint in new[] { basic, ws })
        {
            var divide = new SoapRequest(document, endpoint, endpoint.Operations.Single(o => o.Name == "Divide"));
            divide.Parameters[0].Value = "1";
            divide.Parameters[1].Value = "0";
            var fault = await client.SendAsync(divide, divide.BuildEnvelope(), ct);
            Assert.True(fault.IsFault, fault.Body);
            Assert.Equal("Division by zero", fault.FaultReason);
        }
    }

    /// <summary>Starts the service on port 0 and waits for "Now listening on:"; returns the address.</summary>
    private async Task<string> StartServiceAsync(string dll, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(dll)! };
        foreach (var argument in new[] { dll, "--urls", "http://127.0.0.1:0" })
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service = Process.Start(psi)!;
        void OnLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_output)
            {
                _output.AppendLine(line);
            }

            if (line.Contains("Now listening on:", StringComparison.Ordinal))
            {
                listening.TrySetResult(line[(line.IndexOf("Now listening on:", StringComparison.Ordinal) + 17)..].Trim());
            }
        }

        _service.OutputDataReceived += (_, e) => OnLine(e.Data);
        _service.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _service.BeginOutputReadLine();
        _service.BeginErrorReadLine();
        var exited = _service.WaitForExitAsync(ct);
        var first = await Task.WhenAny(listening.Task, exited, Task.Delay(TimeSpan.FromSeconds(90), ct));
        Assert.True(first == listening.Task, "The service didn't start:\n" + _output);
        return await listening.Task;
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(IEnumerable<string> arguments, string directory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = directory };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            {
                continue;
            }

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
