using DaedalusStudio.Plugins.Proposed;

namespace DaedalusStudio.Plugins.DotNetTest;

/// <summary>Create Unit Tests (M8): generates a test class for a type or method into a test project.</summary>
internal static class CreateUnitTests
{
    public static Task<object?> RunAsync(IPluginContext context, IProposedApi api, CommandInvocation args, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Create Unit Tests is not available yet.");
}
