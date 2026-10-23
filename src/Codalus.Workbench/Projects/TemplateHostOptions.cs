using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Projects;

/// <summary>The host options chosen on the Additional information page.</summary>
public sealed record HostOptionValues
{
    /// <summary>Configure for HTTPS (on unless unchecked; locked on by <see cref="TemplateHostOptions.ForcesHttps"/>).</summary>
    public bool Https { get; init; } = true;

    /// <summary>The authentication type (a value of <see cref="TemplateHostOptions.AuthOrder"/>), or null for the template's default.</summary>
    public string? Auth { get; init; }

    /// <summary>Enable container support (needs a trusted location).</summary>
    public bool Docker { get; init; }

    /// <summary>Enlist in Aspire orchestration (needs a trusted location).</summary>
    public bool Aspire { get; init; }
}

/// <summary>
/// The options the IDE adds to a template's page itself, as the classic IDE does (ADR 0082): Authentication type with
/// None, Individual Accounts, Microsoft identity platform and Windows (the template's <c>auth</c> values None,
/// Individual, SingleOrg and Windows; IndividualB2C and MultiOrg aren't offered); Individual Accounts and Microsoft
/// identity platform need HTTPS, so Configure for HTTPS is checked and locked for them; Individual Accounts uses LocalDB
/// on Windows (where it exists) and the template's SQLite elsewhere. Container support and Aspire aren't CLI options:
/// the shell adds them after the project.
/// </summary>
public static class TemplateHostOptions
{
    /// <summary>The template <c>auth</c> values offered, in the classic IDE's order.</summary>
    public static IReadOnlyList<string> AuthOrder { get; } = ["None", "Individual", "SingleOrg", "Windows"];

    /// <summary>Whether an authentication type needs HTTPS (Configure for HTTPS is then checked and disabled).</summary>
    public static bool ForcesHttps(string? auth) => auth is "Individual" or "SingleOrg";

    public static string AuthLabel(string auth) => auth switch
    {
        "None" => Localizer.Get("templates.auth.none", "None"),
        "Individual" => Localizer.Get("templates.auth.individual", "Individual Accounts"),
        "SingleOrg" => Localizer.Get("templates.auth.singleOrg", "Microsoft identity platform"),
        "Windows" => Localizer.Get("templates.auth.windows", "Windows"),
        _ => auth,
    };

    /// <summary>What an authentication type means here, shown under the choice (null: nothing to say).</summary>
    public static string? AuthNote(string? auth, bool windows) => auth switch
    {
        // TODO(issue): register the app with the Microsoft identity platform, as the classic IDE's provisioning does.
        "SingleOrg" => Localizer.Get("templates.auth.singleOrgNote", "The project gets placeholder settings in appsettings.json to fill in with your app registration."),
        "Windows" when !windows => Localizer.Get("templates.auth.windowsNote", "Needs Kerberos set up on Linux and macOS."),
        _ => null,
    };

    /// <summary>The <c>dotnet new</c> arguments of the host options a member offers.</summary>
    public static IReadOnlyList<string> Arguments(MemberHostOptions options, HostOptionValues values, PathRules rules)
    {
        var arguments = new List<string>();
        var auth = values.Auth is { } chosen && options.AuthChoices?.Contains(chosen, StringComparer.Ordinal) == true ? chosen : null;
        if (auth is not null and not "None" && options.AuthOption is { } authOption)
        {
            arguments.AddRange([authOption, auth]);
        }

        if (auth == "Individual" && rules.IsWindows && options.LocalDbOption is { } localDb)
        {
            arguments.AddRange([localDb, "true"]);
        }

        if (!values.Https && !ForcesHttps(auth) && options.DisableHttpsOption is { } noHttps)
        {
            arguments.AddRange([noHttps, "true"]);
        }

        return arguments;
    }
}
