using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Codalus.Tests;

namespace Codalus.Release.Tests;

/// <summary>
/// The pipeline's live-output action (.github/actions/live-output, ADR 0028): its three scripts run under Node as the
/// runner runs them, against a stand-in for GitHub's artifact service (nothing reaches the real one). A test job's
/// console and samples are uploaded as they grow, each upload replacing the one before, and the job's end removes the
/// last: what a runner that goes silent leaves behind is its last upload. That the real service takes these requests
/// is what a probe run on a hosted runner showed (the ADR has the run).
/// </summary>
public sealed class LiveOutputActionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Action =>
        Path.Combine(typeof(LiveOutputActionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!, ".github", "actions", "live-output");

    /// <summary>Node on the PATH (every hosted runner has it): the tests skip without it locally and fail without it in CI.</summary>
    private static string? Node =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "node.exe" : "node")).FirstOrDefault(File.Exists);

    /// <summary>A token shaped like the runner's: a JWT whose scopes name the run and the job.</summary>
    private static string Token(string run, string job)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Part("""{"alg":"none"}""") + "." + Part($$"""{"scp":"Actions.GenericRead:x Actions.Results:{{run}}:{{job}} Actions.UploadArtifacts:y"}""") + ".signature";
    }

    /// <summary>The artifact service's three calls and the blob store behind its signed URLs.</summary>
    private sealed class ArtifactService : IAsyncDisposable
    {
        private readonly MockHttpServer _server;
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

        public ArtifactService(string token)
        {
            _server = new MockHttpServer(request =>
            {
                const string Service = "/twirp/github.actions.results.api.v1.ArtifactService/";
                if (request.Method == "PUT" && request.Path.StartsWith("/blob/", StringComparison.Ordinal))
                {
                    if (request.Header("x-ms-blob-type") != "BlockBlob")
                    {
                        return MockHttpResponse.Text("no x-ms-blob-type", 400);
                    }

                    _blobs[request.Path["/blob/".Length..]] = request.RawBody;
                    return MockHttpResponse.Text(string.Empty, 201);
                }

                if (request.Method != "POST" || !request.Path.StartsWith(Service, StringComparison.Ordinal) || request.BearerToken != token)
                {
                    return MockHttpResponse.NotFound();
                }

                using var body = JsonDocument.Parse(request.Body);
                var root = body.RootElement;
                Ids.Add((root.GetProperty("workflow_run_backend_id").GetString()!, root.GetProperty("workflow_job_run_backend_id").GetString()!));
                var name = root.GetProperty("name").GetString()!;
                switch (request.Path[Service.Length..])
                {
                    case "CreateArtifact":
                        if (Refuse)
                        {
                            return MockHttpResponse.Json("""{"code":"internal","msg":"the service is down"}""", 500);
                        }

                        if (root.GetProperty("version").GetInt32() != 4 || !DateTimeOffset.TryParse(root.GetProperty("expires_at").GetString(), CultureInfo.InvariantCulture, out _))
                        {
                            return MockHttpResponse.Json("""{"code":"invalid_argument","msg":"version or expires_at"}""", 400);
                        }

                        Created.Enqueue(name);
                        return MockHttpResponse.Json(JsonSerializer.Serialize(new { ok = true, signed_upload_url = _server!.BaseUrl + "/blob/" + name }));
                    case "FinalizeArtifact":
                        if (!_blobs.TryGetValue(name, out var zip)
                            || root.GetProperty("size").GetString() != zip.Length.ToString(CultureInfo.InvariantCulture)
                            || root.GetProperty("hash").GetString() != "sha256:" + Convert.ToHexStringLower(SHA256.HashData(zip)))
                        {
                            return MockHttpResponse.Json("""{"code":"invalid_argument","msg":"size or hash"}""", 400);
                        }

                        Artifacts[name] = zip;
                        return MockHttpResponse.Json("""{"ok":true,"artifact_id":"1"}""");
                    case "DeleteArtifact":
                        return Artifacts.TryRemove(name, out _)
                            ? MockHttpResponse.Json("""{"ok":true,"artifact_id":"1"}""")
                            : MockHttpResponse.Json("""{"code":"not_found","msg":"no such artifact"}""", 404);
                    default:
                        return MockHttpResponse.NotFound();
                }
            });
        }

        public string Url => _server.BaseUrl + "/";

        /// <summary>The artifacts of the run now: finalized and not deleted.</summary>
        public ConcurrentDictionary<string, byte[]> Artifacts { get; } = new();

        public ConcurrentQueue<string> Created { get; } = new();

        public ConcurrentBag<(string Run, string Job)> Ids { get; } = [];

        public bool Refuse { get; set; }

        public ValueTask DisposeAsync() => _server.DisposeAsync();
    }

    private sealed record Job(string Root, string Results, string RunnerTemp, IReadOnlyDictionary<string, string> Environment)
    {
        public string Console => Path.Combine(Results, "shell-2", "console.txt");

        public int? UploaderId => int.TryParse(ReadOrEmpty(Path.Combine(RunnerTemp, "codalus-live-output", "pid")), out var id) ? id : null;

        public bool UploaderRuns
        {
            get
            {
                try
                {
                    return UploaderId is { } id && !Process.GetProcessById(id).HasExited;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
        }

        private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static Job NewJob(ArtifactService service, string token)
    {
        var root = Directory.CreateTempSubdirectory("codalus-live-output-").FullName;
        var results = Path.Combine(root, "artifacts", "test-results");
        Directory.CreateDirectory(Path.Combine(results, "shell-2"));
        Directory.CreateDirectory(Path.Combine(root, "runner-temp"));
        return new Job(root, results, Path.Combine(root, "runner-temp"), new Dictionary<string, string>
        {
            ["GITHUB_WORKSPACE"] = root,
            ["RUNNER_TEMP"] = Path.Combine(root, "runner-temp"),
            ["ACTIONS_RUNTIME_TOKEN"] = token,
            ["ACTIONS_RESULTS_URL"] = service.Url,
            ["INPUT_NAME"] = "live-ubuntu-latest-shell-2-1",
            ["INPUT_INTERVAL"] = "1",
        });
    }

    /// <summary>Runs one of the action's scripts to its end, as a step does, and returns what it printed.</summary>
    private static async Task<string> StepAsync(Job job, string script)
    {
        var start = new ProcessStartInfo(Node!, [Path.Combine(Action, script)])
        {
            WorkingDirectory = job.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (name, value) in job.Environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var text = await output + await error;
        Assert.True(process.ExitCode == 0, $"{script} exited with {process.ExitCode}: {text}");
        return text;
    }

    private static async Task WaitAsync(Func<bool> condition, Func<string> describe, int seconds = 30)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(seconds), "Timed out: " + describe());
            await Task.Delay(50, Ct);
        }
    }

    private static string Entry(byte[] zip, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry(name)?.Open() ?? throw new InvalidOperationException($"No {name} among {string.Join(", ", archive.Entries.Select(e => e.FullName))}"));
        return reader.ReadToEnd();
    }

    private static void End(Job job)
    {
        try
        {
            if (job.UploaderId is { } id)
            {
                using var uploader = Process.GetProcessById(id);
                uploader.Kill();
                uploader.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // ended already
        }

        try
        {
            Directory.Delete(job.Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void NeedsNode()
    {
        Assert.True(Node is not null || Environment.GetEnvironmentVariable("CI") != "true", "Node isn't on the PATH.");
        Assert.SkipWhen(Node is null, "Node isn't on the PATH.");
    }

    [Fact]
    public async Task A_jobs_output_is_uploaded_as_it_grows_and_removed_at_the_jobs_end()
    {
        NeedsNode();
        var token = Token("run-7", "job-9");
        await using var service = new ArtifactService(token);
        var job = NewJob(service, token);
        try
        {
            string Describe() => $"artifacts: {string.Join(", ", service.Artifacts.Keys)}; created: {string.Join(", ", service.Created)}; the uploader's log:\n{(File.Exists(Path.Combine(job.RunnerTemp, "codalus-live-output", "uploader.log")) ? File.ReadAllText(Path.Combine(job.RunnerTemp, "codalus-live-output", "uploader.log")) : "(none)")}";

            // The step returns at once; the uploader it started stays, as it does behind the test steps.
            await File.WriteAllTextAsync(job.Console, "passed First.Test\n", Ct);
            await File.WriteAllTextAsync(Path.Combine(job.Results, "shell-2", "memory.txt"), "22:00:00 free=61%\n", Ct);
            await File.WriteAllTextAsync(Path.Combine(job.Results, "shell-2", "testhost.dmp"), "not a file it sends", Ct);
            var started = await StepAsync(job, "main.js");
            Assert.Contains("live-ubuntu-latest-shell-2-1-<n>", started, StringComparison.Ordinal);
            Assert.True(job.UploaderRuns, "the uploader runs on after its step");

            // The first upload: the files as they are, under the run and the job the token names.
            await WaitAsync(() => service.Artifacts.ContainsKey("live-ubuntu-latest-shell-2-1-0001"), Describe);
            var first = service.Artifacts["live-ubuntu-latest-shell-2-1-0001"];
            Assert.Equal("passed First.Test\n", Entry(first, "shell-2/console.txt"));
            Assert.Equal("22:00:00 free=61%\n", Entry(first, "shell-2/memory.txt"));
            using (var archive = new ZipArchive(new MemoryStream(first), ZipArchiveMode.Read))
            {
                Assert.Equal(["shell-2/console.txt", "shell-2/memory.txt"], archive.Entries.Select(e => e.FullName));
            }

            Assert.All(service.Ids, ids => Assert.Equal(("run-7", "job-9"), ids));

            // Nothing new, nothing uploaded.
            await Task.Delay(2500, Ct);
            Assert.Equal(["live-ubuntu-latest-shell-2-1-0001"], service.Created);

            // The console grew, and another shard began: the next upload has both, and the one before is gone, so the
            // run always has one whole upload and never two.
            await File.AppendAllTextAsync(job.Console, "failed Second.Test\n", Ct);
            Directory.CreateDirectory(Path.Combine(job.Results, "others"));
            await File.WriteAllTextAsync(Path.Combine(job.Results, "others", "console.txt"), "passed Third.Test\n", Ct);
            await WaitAsync(() => service.Artifacts.Keys.Any(k => k != "live-ubuntu-latest-shell-2-1-0001") && !service.Artifacts.ContainsKey("live-ubuntu-latest-shell-2-1-0001"), Describe);
            var latest = Assert.Single(service.Artifacts);
            Assert.Equal("passed First.Test\nfailed Second.Test\n", Entry(latest.Value, "shell-2/console.txt"));
            Assert.Equal("passed Third.Test\n", Entry(latest.Value, "others/console.txt"));

            // The job's end, on a runner that is still there: the uploader stops, its log is in the job's, and nothing
            // of it is left on the run. (A runner that went silent never runs this, and its last upload stays.)
            var ended = await StepAsync(job, "post.js");
            Assert.Contains("uploaded live-ubuntu-latest-shell-2-1-0001: 2 file(s)", ended, StringComparison.Ordinal);
            Assert.Contains($"Removed {latest.Key}.", ended, StringComparison.Ordinal);
            Assert.DoesNotContain("::warning", ended, StringComparison.Ordinal);
            Assert.Empty(service.Artifacts);
            Assert.False(job.UploaderRuns, "the uploader ended with the job");
        }
        finally
        {
            End(job);
        }
    }

    [Fact]
    public async Task A_service_that_refuses_the_uploads_is_said_at_the_jobs_end_and_fails_nothing()
    {
        NeedsNode();
        var token = Token("run-7", "job-9");
        await using var service = new ArtifactService(token) { Refuse = true };
        var job = NewJob(service, token);
        try
        {
            await File.WriteAllTextAsync(job.Console, "passed First.Test\n", Ct);
            await StepAsync(job, "main.js");
            var status = Path.Combine(job.RunnerTemp, "codalus-live-output", "status.json");
            await WaitAsync(() => File.Exists(status) && File.ReadAllText(status).Contains("the service is down", StringComparison.Ordinal), () => File.Exists(status) ? File.ReadAllText(status) : "(no status)");

            // Found out here, on every run, not the day a runner is lost: a warning on the run's page. The step passes.
            var ended = await StepAsync(job, "post.js");
            Assert.Contains("::warning title=Live output::", ended, StringComparison.Ordinal);
            Assert.Contains("upload(s) failed, 0 worked", ended, StringComparison.Ordinal);
            Assert.Contains("the service is down", ended, StringComparison.Ordinal);
            Assert.Empty(service.Artifacts);
            Assert.False(job.UploaderRuns, "the uploader ended with the job");
        }
        finally
        {
            End(job);
        }
    }
}
