using System.Text;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Build;

/// <summary>
/// The <c>DebugRoslynComponent</c> launch profile (M14c, ADR 0039): an analyzer or source generator is debugged by
/// running the compiler of the profile's <c>targetProject</c> (which references it) under the debugger. The compiler is
/// the SDK's own <c>csc.dll</c> (or <c>vbc.dll</c>) on <c>dotnet exec</c>, with the target's command line from its
/// design-time build (ADR 0005) in a response file, its outputs redirected to a temporary folder so the project's
/// own build outputs are left alone.
/// </summary>
public static class RoslynComponents
{
    /// <summary>Plans the compiler run for <paramref name="profile"/> of the analyzer project <paramref name="info"/>.</summary>
    public static async Task<LaunchPlan> PlanAsync(SolutionService solutions, RunInfo info, LaunchProfile profile, CancellationToken cancellationToken)
    {
        if (profile.TargetProject is not { Length: > 0 } relative)
        {
            throw new InvalidOperationException($"The launch profile '{profile.Name}' has no targetProject: the project whose compilation runs the component.");
        }

        var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(info.Path)!, relative.Replace('\\', Path.DirectorySeparatorChar)));
        if (!File.Exists(target))
        {
            throw new InvalidOperationException($"The launch profile '{profile.Name}' targets {target}, which doesn't exist.");
        }

        var inputs = solutions.CompilerInputs.GetValueOrDefault(target);
        if (inputs is not { State: CompilerInputStates.Ok })
        {
            var arrived = new TaskCompletionSource<CompilerInputsInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnInputs(object? sender, CompilerInputsInfo changed)
            {
                if (SolutionService.PathComparer.Equals(changed.Path, target))
                {
                    arrived.TrySetResult(changed);
                }
            }

            solutions.CompilerInputsChanged += OnInputs;
            try
            {
                await solutions.DesignTimeBuildAsync(target, cancellationToken).ConfigureAwait(false);
                inputs = await arrived.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException($"The compiler command line of {Path.GetFileNameWithoutExtension(target)} didn't arrive (its design-time build didn't finish).", ex);
            }
            finally
            {
                solutions.CompilerInputsChanged -= OnInputs;
            }
        }

        if (inputs.State != CompilerInputStates.Ok)
        {
            throw new InvalidOperationException($"The compiler command line of {Path.GetFileNameWithoutExtension(target)} isn't known: {inputs.Error}");
        }

        var sdk = solutions.SdkPath ?? throw new InvalidOperationException("The .NET SDK in use isn't known yet.");
        var output = Path.Combine(Path.GetTempPath(), "daedalus-roslyn", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())))[..12]);
        return Plan(sdk, inputs, output);
    }

    /// <summary>The compiler of <paramref name="sdkPath"/> on <paramref name="inputs"/>, writing into <paramref name="outputFolder"/>.</summary>
    public static LaunchPlan Plan(string sdkPath, CompilerInputsInfo inputs, string outputFolder)
    {
        if (inputs.Language is not ("C#" or "VB"))
        {
            throw new InvalidOperationException($"Roslyn components run on C# and Visual Basic projects; {Path.GetFileName(inputs.Path)} is {inputs.Language}.");
        }

        var sdk = Path.TrimEndingDirectorySeparator(sdkPath);
        var compiler = Path.Combine(sdk, "Roslyn", "bincore", inputs.Language == "VB" ? "vbc.dll" : "csc.dll");
        if (!File.Exists(compiler))
        {
            throw new InvalidOperationException($"The SDK's compiler wasn't found at {compiler}.");
        }

        var root = Directory.GetParent(sdk)?.Parent?.FullName ?? throw new InvalidOperationException($"{sdk} isn't in a .NET installation.");
        var dotnet = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Directory.CreateDirectory(outputFolder);
        var response = Path.Combine(outputFolder, "compile.rsp");
        File.WriteAllLines(response, Rewrite(inputs.Arguments, outputFolder).Select(Quote));
        List<string> arguments = ["exec", compiler, "@" + response];
        return new LaunchPlan(dotnet, $"exec \"{compiler}\" \"@{response}\"", Path.GetDirectoryName(inputs.Path)!, new Dictionary<string, string>(StringComparer.Ordinal), null)
        {
            ArgumentList = arguments,
        };
    }

    /// <summary>
    /// The target's compiler arguments with the outputs moved into <paramref name="outputFolder"/>: the assembly, its
    /// PDB and documentation file; no reference assembly.
    /// </summary>
    public static IReadOnlyList<string> Rewrite(IReadOnlyList<string> arguments, string outputFolder)
    {
        var result = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            var (option, value) = Split(argument);
            switch (option)
            {
                case "out" or "pdb" or "doc" when value is { Length: > 0 }:
                    result.Add($"/{option}:{Quote(Path.Combine(outputFolder, Path.GetFileName(value.Trim('"'))))}");
                    break;
                case "refout" or "refonly":
                    break;
                default:
                    result.Add(argument);
                    break;
            }
        }

        return result;
    }

    /// <summary>An option's name (lower case) and value: <c>/out:x.dll</c> is (<c>out</c>, <c>x.dll</c>); a source file has no name.</summary>
    private static (string? Option, string? Value) Split(string argument)
    {
        if (argument.Length < 2 || argument[0] is not ('/' or '-'))
        {
            return (null, null);
        }

        var colon = argument.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? (argument[1..].ToLowerInvariant(), null)
            : (argument[1..colon].ToLowerInvariant(), argument[(colon + 1)..]);
    }

    /// <summary>
    /// An argument for a compiler response file. The design-time build's arguments are already in command-line form
    /// (<c>/analyzer:"C:\Program Files\…"</c>), so one with quotes stays as it is; one with spaces is quoted, with the
    /// backslashes before quotes doubled.
    /// </summary>
    public static string Quote(string argument)
    {
        if (argument.Contains('"', StringComparison.Ordinal) || (argument.Length > 0 && !argument.Any(char.IsWhiteSpace)))
        {
            return argument;
        }

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            result.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            result.Append(c);
        }

        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }
}
