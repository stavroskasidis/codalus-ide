using Codalus.Projects.Host.Packages;
using Codalus.Projects.Protocol;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace Codalus.Projects.Host.ProjectManagement;

/// <summary>
/// Project properties for the property pages (ADR 0013): evaluated values with where they come from, and the
/// minimal project-file edit to set or reset one for every configuration or for one.
/// </summary>
internal static class ProjectProperties
{
    public static ProjectPropertiesResult Get(string path, IReadOnlyDictionary<string, string> globalProperties, IReadOnlyList<string> names, string? scope)
    {
        using var gate = StringCacheGate.Enter();
        using var collection = new ProjectCollection(new Dictionary<string, string>(globalProperties, StringComparer.OrdinalIgnoreCase));
        var project = collection.LoadProject(path);
        var xml = ProjectRootElement.Open(path, collection, preserveFormatting: true);
        var values = names.Distinct(StringComparer.OrdinalIgnoreCase).Select(name => Describe(project, xml, name, scope)).ToArray();
        return new ProjectPropertiesResult(
            path,
            project.GetPropertyValue("Configuration"),
            project.GetPropertyValue("Platform"),
            Split(project.GetPropertyValue("Configurations")) is { Length: > 0 } configurations ? configurations : ["Debug", "Release"],
            values);
    }

    private static ProjectPropertyValue Describe(Project project, ProjectRootElement xml, string name, string? scope)
    {
        var projectValue = InScope(xml, name, scope).LastOrDefault()?.Value;
        var property = project.GetProperty(name);
        if (property is null)
        {
            return new ProjectPropertyValue(name, string.Empty, null, PropertyOrigins.Unset, null, 0, null, projectValue);
        }

        if (property.IsGlobalProperty || property.IsEnvironmentProperty)
        {
            return new ProjectPropertyValue(name, property.EvaluatedValue, null, PropertyOrigins.Global, null, 0, null, projectValue);
        }

        if (property.Xml is not { } element)
        {
            return new ProjectPropertyValue(name, property.EvaluatedValue, null, PropertyOrigins.Sdk, null, 0, null, projectValue);
        }

        var file = element.ContainingProject.FullPath;
        var origin = string.Equals(file, project.FullPath, StringComparison.OrdinalIgnoreCase) ? PropertyOrigins.Project
            : PackageReferences.IsUserFile(project, file) ? PropertyOrigins.Import
            : PropertyOrigins.Sdk;
        var condition = string.Join(" and ", new[] { element.Parent?.Condition, element.Condition }.Where(c => !string.IsNullOrWhiteSpace(c)));
        return new ProjectPropertyValue(name, property.EvaluatedValue, element.Value, origin, file, element.Location.Line, condition.Length == 0 ? null : condition, projectValue);
    }

    /// <summary>
    /// Sets the property in the project file for every configuration (<paramref name="scope"/> null) or one, or removes it
    /// (<paramref name="value"/> null). Returns whether the file changed.
    /// </summary>
    public static bool Set(string path, string name, string? value, string? scope)
    {
        using var gate = StringCacheGate.Enter();
        using var collection = new ProjectCollection();
        var xml = ProjectRootElement.Open(path, collection, preserveFormatting: true);
        xml.Reload(throwIfUnsavedChanges: false, preserveFormatting: true); // always the file on disk
        var before = xml.RawXml; // Reload marks the element dirty, so a change is a different text, not HasUnsavedChanges
        SetIn(xml, name, value, scope);
        if (xml.RawXml == before)
        {
            return false;
        }

        xml.SaveFile();
        return true;
    }

    /// <summary>Sets or removes the property in an open project file, without saving it.</summary>
    internal static void SetIn(ProjectRootElement xml, string name, string? value, string? scope)
    {
        var existing = InScope(xml, name, scope).ToList();
        if (value is null)
        {
            foreach (var property in existing)
            {
                var group = property.Parent;
                group.RemoveChild(property);
                if (group.Count == 0)
                {
                    group.Parent.RemoveChild(group);
                }
            }
        }
        else if (existing.Count > 0)
        {
            // The last definition wins in MSBuild; it's the one to change.
            if (existing[^1].Value != value)
            {
                existing[^1].Value = value;
            }
        }
        else
        {
            var group = xml.PropertyGroups.FirstOrDefault(g => ScopeOf(g.Condition) is { } s && Same(s, scope));
            if (group is null)
            {
                group = xml.CreatePropertyGroupElement();
                var last = xml.PropertyGroups.LastOrDefault();
                if (last is null)
                {
                    xml.PrependChild(group);
                }
                else
                {
                    xml.InsertAfterChild(group, last);
                }

                if (scope is not null)
                {
                    group.Condition = ConfigurationCondition(scope);
                }
            }

            group.AddProperty(name, value);
        }
    }

    /// <summary>The project file's unconditioned definitions of <paramref name="name"/> in the scope's groups.</summary>
    private static IEnumerable<ProjectPropertyElement> InScope(ProjectRootElement xml, string name, string? scope) =>
        xml.Properties.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                  && string.IsNullOrWhiteSpace(p.Condition)
                                  && p.Parent is ProjectPropertyGroupElement g
                                  && ScopeOf(g.Condition) is { } s
                                  && Same(s, scope));

    private static bool Same(Scope s, string? scope) => scope is null ? s.All : !s.All && string.Equals(s.Configuration, scope, StringComparison.OrdinalIgnoreCase);

    private readonly record struct Scope(bool All, string? Configuration);

    /// <summary>
    /// The scope of a group condition: none (all configurations), <c>'$(Configuration)' == 'X'</c> or the classic IDE's
    /// <c>'$(Configuration)|$(Platform)' == 'X|AnyCPU'</c>; null for any other condition.
    /// </summary>
    private static Scope? ScopeOf(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return new Scope(true, null);
        }

        var compact = string.Concat(condition.Where(c => !char.IsWhiteSpace(c))).Replace('"', '\'');
        foreach (var prefix in new[] { "'$(Configuration)'=='", "'$(Configuration)|$(Platform)'=='" })
        {
            if (compact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && compact.EndsWith('\''))
            {
                var value = compact[prefix.Length..^1];
                var configuration = value.Split('|')[0];
                if (prefix.Contains("Platform", StringComparison.Ordinal) && value.Split('|') is [_, var platform] && !platform.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return new Scope(false, configuration);
            }
        }

        return null;
    }

    internal static string ConfigurationCondition(string configuration) => $"'$(Configuration)' == '{configuration}'";

    private static string[] Split(string value) =>
        [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
}
