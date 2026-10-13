using Codalus.Projects.Protocol;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace Codalus.Projects.Host.ProjectManagement;

/// <summary>
/// Pre-build and post-build events (ADR 0034), stored the way Visual Studio stores them in SDK-style projects:
/// <c>&lt;Target Name="PreBuild" BeforeTargets="PreBuildEvent"&gt;&lt;Exec Command="…" /&gt;&lt;/Target&gt;</c> and
/// <c>&lt;Target Name="PostBuild" AfterTargets="PostBuildEvent"&gt;</c>, with <c>RunPostBuildEvent</c> as a property.
/// </summary>
internal static class BuildEvents
{
    public const string PreBuildTarget = "PreBuild";
    public const string PostBuildTarget = "PostBuild";

    public static BuildEventsResult Get(string path)
    {
        using var collection = new ProjectCollection();
        var xml = ProjectRootElement.Open(path, collection, preserveFormatting: true);
        xml.Reload(throwIfUnsavedChanges: false, preserveFormatting: true);
        var run = xml.Properties.LastOrDefault(p => p.Name == "RunPostBuildEvent" && p.Condition.Length == 0 && p.Parent.Condition.Length == 0)?.Value;
        return new BuildEventsResult(path, Command(xml, PreBuildTarget), Command(xml, PostBuildTarget), run is { Length: > 0 } ? run : "OnBuildSuccess");
    }

    /// <summary>Writes the events; returns whether the project file changed.</summary>
    public static bool Set(string path, string? preBuild, string? postBuild, string? runPostBuildEvent)
    {
        using var collection = new ProjectCollection();
        var xml = ProjectRootElement.Open(path, collection, preserveFormatting: true);
        xml.Reload(throwIfUnsavedChanges: false, preserveFormatting: true);
        var before = xml.RawXml;
        if (preBuild is not null)
        {
            SetTarget(xml, PreBuildTarget, "BeforeTargets", "PreBuildEvent", preBuild);
        }

        if (postBuild is not null)
        {
            SetTarget(xml, PostBuildTarget, "AfterTargets", "PostBuildEvent", postBuild);
        }

        if (runPostBuildEvent is not null)
        {
            var value = runPostBuildEvent is "OnBuildSuccess" or "" ? null : runPostBuildEvent;
            if (value is not null && value is not ("Always" or "OnOutputUpdated"))
            {
                throw new ArgumentException($"RunPostBuildEvent is Always, OnBuildSuccess or OnOutputUpdated, not '{runPostBuildEvent}'.");
            }

            ProjectProperties.SetIn(xml, "RunPostBuildEvent", value, null);
        }

        if (xml.RawXml == before)
        {
            return false;
        }

        xml.Save();
        return true;
    }

    private static string Command(ProjectRootElement xml, string name) =>
        xml.Targets.FirstOrDefault(t => t.Name == name)?.Tasks.FirstOrDefault(t => t.Name == "Exec")?.GetParameter("Command") is { } command
            ? command.ReplaceLineEndings("\n")
            : string.Empty;

    private static void SetTarget(ProjectRootElement xml, string name, string hook, string hookTarget, string command)
    {
        var target = xml.Targets.FirstOrDefault(t => t.Name == name);
        command = command.Trim().ReplaceLineEndings("\r\n");
        if (command.Length == 0)
        {
            if (target is not null)
            {
                xml.RemoveChild(target);
            }

            return;
        }

        if (target is null)
        {
            target = xml.AddTarget(name);
            if (hook == "BeforeTargets")
            {
                target.BeforeTargets = hookTarget;
            }
            else
            {
                target.AfterTargets = hookTarget;
            }
        }

        var exec = target.Tasks.FirstOrDefault(t => t.Name == "Exec") ?? target.AddTask("Exec");
        exec.SetParameter("Command", command);
    }
}
