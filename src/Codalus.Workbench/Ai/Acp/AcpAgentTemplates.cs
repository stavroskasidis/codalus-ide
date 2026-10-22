using Codalus.Platform;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>How a template signs its agent in (ADR 0083).</summary>
public enum AcpSignInKind
{
    /// <summary>The agent's own method, through <c>authenticate</c> (it opens the browser itself).</summary>
    AgentMethod,

    /// <summary>The agent's terminal method: its own login command in Codalus's terminal, then it starts again.</summary>
    TerminalMethod,

    /// <summary>An API key kept in the OS credential store and given to the agent as a variable (<c>secretEnv</c>).</summary>
    ApiKeyEnv,

    /// <summary>An API key sent once in <c>authenticate</c>'s <c>_meta</c>; the agent keeps it in its own store (Codex).</summary>
    ApiKeyOnce,

    /// <summary>The agent CLI's own login command in Codalus's terminal (OpenCode, goose), judged by testing again.</summary>
    TerminalCommand,

    /// <summary>Nothing to do: the agent is signed in on this computer already, and the test checks it.</summary>
    AlreadySignedIn,
}

/// <summary>How the connection test knows the sign-in worked (ADR 0083).</summary>
public enum AcpSignInCheck
{
    /// <summary>The agent refuses <c>session/new</c> with <c>auth_required</c> while signed out (Codex, Gemini CLI).</summary>
    SessionNew,

    /// <summary>The agent pushes who is signed in on <c>_auth/status_update</c> (Claude's adapter); silence is "not confirmed".</summary>
    AuthStatusPush,

    /// <summary>Not known to refuse anything while signed out: the result is "not confirmed", and the first prompt tells.</summary>
    Unknown,
}

/// <summary>Where a template is offered: a button in AI Chat (one click) or only in More agents….</summary>
public enum AcpTemplateTier
{
    OneClick,
    More,
}

/// <summary>A way to sign an agent in, as the Connect dialog offers it.</summary>
/// <param name="Id">The option's id (<c>subscription</c>, <c>apikey</c>, <c>already</c>…).</param>
/// <param name="Kind">How it signs in.</param>
/// <param name="Name">Its radio button's text.</param>
/// <param name="Description">Its line under it.</param>
public sealed record AcpSignInOption(string Id, AcpSignInKind Kind, string Name, string Description)
{
    /// <summary>The agent's method id it uses (agent, terminal, or the <c>authenticate</c> after a key), matched against the agent's <c>authMethods</c>.</summary>
    public string? MethodId { get; init; }

    /// <summary>The method the agent offers instead in an SSH session or with <c>NO_BROWSER</c> (Claude's <c>claude-login</c>, its own TUI).</summary>
    public string? RemoteMethodId { get; init; }

    /// <summary>
    /// A terminal method's arguments as the pinned agent gives them (read from its code), for the dialog's "What runs"
    /// before the agent has started; the test refuses to run a method whose arguments differ from what was shown.
    /// </summary>
    public IReadOnlyList<string> TerminalArgs { get; init; } = [];

    /// <summary>The arguments of <see cref="RemoteMethodId"/>'s method.</summary>
    public IReadOnlyList<string> RemoteTerminalArgs { get; init; } = [];

    /// <summary>The variable an <see cref="AcpSignInKind.ApiKeyEnv"/> key is given to the agent as.</summary>
    public string? EnvVar { get; init; }

    /// <summary>What the key is (the password box's name: "Anthropic API key").</summary>
    public string? KeyName { get; init; }

    /// <summary>Where the agent keeps an <see cref="AcpSignInKind.ApiKeyOnce"/> key itself (Codex: <c>~/.codex</c>).</summary>
    public string? KeyStore { get; init; }

    /// <summary>A <see cref="AcpSignInKind.TerminalCommand"/>'s arguments to the agent's own CLI.</summary>
    public IReadOnlyList<string> CommandArgs { get; init; } = [];

    /// <summary>
    /// Variables the agent starts without after this sign-in, because they would outrank it: a Claude subscription
    /// signed in through Claude Code's own login doesn't use <c>ANTHROPIC_API_KEY</c>, <c>ANTHROPIC_AUTH_TOKEN</c> or
    /// <c>ANTHROPIC_BASE_URL</c> from the user's environment (D43).
    /// </summary>
    public IReadOnlyList<string> UnsetEnv { get; init; } = [];

    /// <summary>The key is typed in the dialog (both key kinds).</summary>
    public bool TakesKey => Kind is AcpSignInKind.ApiKeyEnv or AcpSignInKind.ApiKeyOnce;
}

/// <summary>A way to install what an agent needs, through the user's own package managers, shown before it runs.</summary>
/// <param name="Id">The option's id (<c>winget</c>, <c>snap</c>, <c>brew</c>, <c>apt</c>, <c>npm</c>, <c>page</c>, <c>codalus-node</c>…).</param>
/// <param name="Name">Its radio button's text.</param>
/// <param name="Description">Its line under it.</param>
/// <param name="Kind">Run in the dialog, in the Terminal (it asks for a password), or a page to open.</param>
/// <param name="Command">The exact command line as the user would type it; for a page, its URL.</param>
public sealed record AcpInstallOption(string Id, string Name, string Description, InstallKind Kind, string Command)
{
    /// <summary>The program and arguments a Run or Terminal option starts.</summary>
    public string? Program { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Codalus's own Node.js, downloaded once and verified (D-node: an opt-in only where nothing else can install one).</summary>
    public bool IsCodalusNode { get; init; }

    /// <summary>On Windows, the PATH is read again from the registry when it ends (winget's installers change it there).</summary>
    public bool ReadsWindowsPath { get; init; }
}

/// <summary>
/// An agent the Connect dialog sets up (ADR 0083): the run part (<see cref="AcpKnownAgent"/>, its pinned command), what it
/// needs, how it signs in and how the test checks it, and how its own CLI is installed. Committed: nothing is fetched.
/// </summary>
public sealed record AcpAgentTemplate(AcpKnownAgent Agent, string Vendor, string RegistryId, AcpTemplateTier Tier, AcpSignInCheck SignInCheck, IReadOnlyList<AcpSignInOption> SignIn)
{
    public string Id => Agent.Id;

    public string Name => Agent.Name;

    public string Version => Agent.Version;

    /// <summary>The Node.js major the package needs (npm's <c>engines</c>), or null when any Node.js with npx does (not stated).</summary>
    public int? NodeMajor { get; init; }

    /// <summary>The agent's own CLI must be installed (pi, OpenCode, goose), at <see cref="CliMinimum"/> or later when given.</summary>
    public bool NeedsCli { get; init; }

    public string? CliMinimum { get; init; }

    /// <summary>How its own CLI is installed, pinned (npm with an exact version, Homebrew), or its page.</summary>
    public Func<PlatformKind, IReadOnlyList<AcpInstallOption>> CliInstall { get; init; } = _ => [];

    /// <summary>The agent advertises <c>logout</c>, so Disconnect can offer "Also sign out" (Claude, Codex).</summary>
    public bool CanSignOut { get; init; }

    /// <summary>A warning under its sign-in choices (Gemini CLI's sign-in reset), or null.</summary>
    public string? SignInWarning { get; init; }

    public string DocsUrl { get; init; } = "";

    public string PrivacyUrl { get; init; } = "";

    /// <summary>The <c>template</c> its entry records: <c>claude@0.94.0</c>.</summary>
    public string Tag => Id + "@" + Version;

    /// <summary>The npx package with its version, or null for an agent that runs its own CLI.</summary>
    public string? Package => Agent.Package;
}

/// <summary>
/// The agent templates (ADR 0083, D43), pinned to the ACP registry on <see cref="AcpAgentCatalog.RegistryChecked"/>: one
/// click for Claude, Codex, Gemini CLI and GitHub Copilot; pi, OpenCode and goose in More agents…. Sign-in methods are the
/// pinned packages' own (read from their code; <c>tests/Shared/AcpRecordings</c> keeps their <c>initialize</c>).
/// </summary>
public static class AcpAgentTemplates
{
    /// <summary>The variables a Claude subscription sign-in starts without (D43): they would outrank Claude Code's own login.</summary>
    public static IReadOnlyList<string> ClaudeProviderVariables { get; } = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"];

    /// <summary>pi's own CLI, pinned (pi-acp needs 0.81 or later; this version needs Node.js 22.19).</summary>
    public const string PiCliPackage = "@earendil-works/pi-coding-agent@1.4.0";

    /// <summary>OpenCode's CLI from npm, pinned to the version the registry lists.</summary>
    public const string OpenCodeCliPackage = "opencode-ai@1.19.4";

    private static readonly AcpInstallOption GoosePage =
        new("page", "goose's install page", "Download goose for your OS yourself.", InstallKind.OpenPage, "https://block.github.io/goose/docs/getting-started/installation");

    private static AcpSignInOption Already(string agent) =>
        new("already", AcpSignInKind.AlreadySignedIn, Localizer.Get("msg.acp.template.already", "I'm signed in already"), Localizer.Format("msg.acp.template.already.description", "{0} is signed in on this computer. The test checks it.", agent));

    /// <summary>The templates, one-click ones first, in the order AI Chat shows them.</summary>
    public static IReadOnlyList<AcpAgentTemplate> All { get; } =
    [
        new(Known("claude"), "Anthropic", "claude-acp", AcpTemplateTier.OneClick, AcpSignInCheck.AuthStatusPush,
        [
            new("subscription", AcpSignInKind.TerminalMethod, Localizer.Get("msg.acp.template.claude.subscription", "Claude subscription"), Localizer.Get("msg.acp.template.claude.subscription.description", "Pro or Max plan. Claude signs you in in the Terminal, with your browser."))
            {
                MethodId = "claude-ai-login",
                TerminalArgs = ["--cli", "auth", "login", "--claudeai"],
                RemoteMethodId = "claude-login",
                RemoteTerminalArgs = ["--cli"],
                UnsetEnv = ClaudeProviderVariables,
            },
            new("console", AcpSignInKind.TerminalMethod, Localizer.Get("msg.acp.template.claude.console", "Anthropic Console account"), Localizer.Get("msg.acp.template.claude.console.description", "Pay as you go. Signs in in the Terminal, with your browser."))
            {
                MethodId = "console-login",
                TerminalArgs = ["--cli", "auth", "login", "--console"],
                RemoteMethodId = "claude-login",
                RemoteTerminalArgs = ["--cli"],
            },
            new("apikey", AcpSignInKind.ApiKeyEnv, Localizer.Get("msg.acp.template.claude.apikey", "API key"), Localizer.Get("msg.acp.template.claude.apikey.description", "A key from the Anthropic Console.")) { EnvVar = "ANTHROPIC_API_KEY", KeyName = Localizer.Get("msg.acp.template.key.anthropicApiKey", "Anthropic API key") },
            Already("Claude Code"),
        ])
        {
            NodeMajor = 22,
            CanSignOut = true,
            DocsUrl = "https://github.com/agentclientprotocol/claude-agent-acp",
            PrivacyUrl = "https://www.anthropic.com/legal/privacy",
        },
        new(Known("codex"), "OpenAI", "codex-acp", AcpTemplateTier.OneClick, AcpSignInCheck.SessionNew,
        [
            new("chatgpt", AcpSignInKind.AgentMethod, Localizer.Get("msg.acp.template.codex.chatgpt", "ChatGPT"), Localizer.Get("msg.acp.template.codex.chatgpt.description", "Sign in with your ChatGPT plan, with your browser.")) { MethodId = "chat-gpt" },
            new("apikey", AcpSignInKind.ApiKeyOnce, Localizer.Get("msg.acp.template.codex.apikey", "API key"), Localizer.Get("msg.acp.template.codex.apikey.description", "An OpenAI API key.")) { MethodId = "api-key", KeyName = Localizer.Get("msg.acp.template.key.openaiApiKey", "OpenAI API key"), KeyStore = "~/.codex" },
            Already("Codex"),
        ])
        {
            CanSignOut = true,
            DocsUrl = "https://github.com/agentclientprotocol/codex-acp",
            PrivacyUrl = "https://openai.com/policies/privacy-policy",
        },
        new(Known("gemini"), "Google", "gemini", AcpTemplateTier.OneClick, AcpSignInCheck.SessionNew,
        [
            new("google", AcpSignInKind.AgentMethod, Localizer.Get("msg.acp.template.gemini.google", "Google account"), Localizer.Get("msg.acp.template.gemini.google.description", "Log in with Google, with your browser.")) { MethodId = "oauth-personal" },
            new("apikey", AcpSignInKind.ApiKeyEnv, Localizer.Get("msg.acp.template.gemini.apikey", "Gemini API key"), Localizer.Get("msg.acp.template.gemini.apikey.description", "A key from Google AI Studio.")) { EnvVar = "GEMINI_API_KEY", MethodId = "gemini-api-key", KeyName = Localizer.Get("msg.acp.template.key.geminiApiKey", "Gemini API key") },
            Already("Gemini CLI"),
        ])
        {
            NodeMajor = 20,
            SignInWarning = Localizer.Get("msg.acp.template.gemini.warning", "Signing in a different way resets Gemini CLI's saved Google sign-in and changes its own settings file (~/.gemini/settings.json)."),
            DocsUrl = "https://github.com/google-gemini/gemini-cli",
            PrivacyUrl = "https://policies.google.com/privacy",
        },
        new(Known("copilot"), "GitHub", "github-copilot-cli", AcpTemplateTier.OneClick, AcpSignInCheck.Unknown,
        [
            new("apikey", AcpSignInKind.ApiKeyEnv, Localizer.Get("msg.acp.template.copilot.apikey", "GitHub token"), Localizer.Get("msg.acp.template.copilot.apikey.description", "A fine-grained personal access token with the Copilot Requests permission.")) { EnvVar = "GH_TOKEN", KeyName = Localizer.Get("msg.acp.template.key.githubToken", "GitHub token") },
            Already("GitHub Copilot CLI"),
        ])
        {
            DocsUrl = "https://github.com/github/copilot-cli",
            PrivacyUrl = "https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement",
        },
        new(Known("pi"), "pi", "pi-acp", AcpTemplateTier.More, AcpSignInCheck.Unknown,
        [
            new("terminal", AcpSignInKind.TerminalMethod, Localizer.Get("msg.acp.template.pi.terminal", "Set up pi in the Terminal"), Localizer.Get("msg.acp.template.pi.terminal.description", "pi asks for its provider and key, or signs you in, in the Terminal.")) { MethodId = "pi_terminal_login", TerminalArgs = ["--terminal-login"] },
            Already("pi"),
        ])
        {
            NodeMajor = 22,
            NeedsCli = true,
            CliMinimum = "0.81.0",
            CliInstall = _ => [NpmGlobal("pi", PiCliPackage)],
            DocsUrl = "https://github.com/svkozak/pi-acp",
            PrivacyUrl = "https://github.com/svkozak/pi-acp",
        },
        new(Known("opencode"), "OpenCode", "opencode", AcpTemplateTier.More, AcpSignInCheck.Unknown,
        [
            new("terminal", AcpSignInKind.TerminalCommand, Localizer.Get("msg.acp.template.opencode.terminal", "Sign in in the Terminal"), Localizer.Get("msg.acp.template.opencode.terminal.description", "Runs opencode auth login, OpenCode's own sign-in.")) { CommandArgs = ["auth", "login"] },
            Already("OpenCode"),
        ])
        {
            NeedsCli = true,
            CliInstall = _ => [NpmGlobal("opencode", OpenCodeCliPackage)],
            DocsUrl = "https://opencode.ai/docs",
            PrivacyUrl = "https://opencode.ai",
        },
        new(Known("goose"), "Block", "goose", AcpTemplateTier.More, AcpSignInCheck.Unknown,
        [
            new("terminal", AcpSignInKind.TerminalCommand, Localizer.Get("msg.acp.template.goose.terminal", "Set up goose in the Terminal"), Localizer.Get("msg.acp.template.goose.terminal.description", "Runs goose configure, goose's own setup of its provider.")) { CommandArgs = ["configure"] },
            Already("goose"),
        ])
        {
            NeedsCli = true,
            CliInstall = platform => platform == PlatformKind.MacOS
                ? [new("brew", "Homebrew", "goose from Homebrew (block-goose-cli).", InstallKind.Run, "brew install block-goose-cli") { Program = "brew", Arguments = ["install", "block-goose-cli"] }, GoosePage]
                : [GoosePage],
            DocsUrl = "https://block.github.io/goose/",
            PrivacyUrl = "https://block.github.io/goose/",
        },
    ];

    /// <summary>The template of <paramref name="id"/>, or null.</summary>
    public static AcpAgentTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>The template an entry was connected from (its <c>template</c>, <c>claude@0.94.0</c>), or null for a hand-written entry.</summary>
    public static AcpAgentTemplate? FromTag(string? tag) =>
        tag is { Length: > 0 } && tag.IndexOf('@', StringComparison.Ordinal) is var at and > 0 ? Find(tag[..at]) : null;

    private static AcpKnownAgent Known(string id) => AcpAgentCatalog.Agents.Single(a => a.Id == id);

    /// <summary>A pinned global npm install (run in the Terminal with sudo when npm's global folder isn't writable, decided at install).</summary>
    private static AcpInstallOption NpmGlobal(string cli, string package) =>
        new("npm", "npm", $"{cli} from npm, pinned to {package[(package.LastIndexOf('@') + 1)..]}.", InstallKind.Run, "npm install -g " + package) { Program = "npm", Arguments = ["install", "-g", package] };

    /// <summary>
    /// Which agents can be set up on which OS without opening a page, as the user guide's table says (checked by a test):
    /// Node.js from the OS's own package manager where it has a new enough one, otherwise Codalus's own Node.js as an
    /// opt-in (D-node); the agents' own CLIs from npm (pinned) or Homebrew; goose from Homebrew only.
    /// </summary>
    public static IReadOnlyList<(string Agent, string Windows, string MacOS, string Linux)> Coverage { get; } =
    [
        ("Claude", "winget", "Homebrew, or Codalus's Node.js", "snap (Ubuntu), apt or dnf with Node.js 22, or Codalus's Node.js"),
        ("Codex", "winget", "Homebrew, or Codalus's Node.js", "snap (Ubuntu), apt or dnf, or Codalus's Node.js"),
        ("Gemini CLI", "winget", "Homebrew, or Codalus's Node.js", "snap (Ubuntu), apt or dnf with Node.js 20, or Codalus's Node.js"),
        ("GitHub Copilot", "winget", "Homebrew, or Codalus's Node.js", "snap (Ubuntu), apt or dnf, or Codalus's Node.js"),
        ("pi", "winget, then npm", "Homebrew or Codalus's Node.js, then npm", "Node.js as for Claude, then npm"),
        ("OpenCode", "winget, then npm", "Homebrew or Codalus's Node.js, then npm", "Node.js as for Codex, then npm"),
        ("goose", "manual (its install page)", "Homebrew", "manual (its install page)"),
    ];
}

/// <summary>What the Node.js install options depend on, found by <see cref="AcpAgentConnector"/> (tests give their own).</summary>
/// <param name="Platform">The OS.</param>
/// <param name="Distribution"><c>/etc/os-release</c>'s ID and ID_LIKE (Linux), or null.</param>
/// <param name="HasWinget">winget is on the PATH (Windows).</param>
/// <param name="HasBrew">Homebrew is on the PATH (macOS).</param>
/// <param name="HasSnap">snap is on the PATH (Linux).</param>
/// <param name="AptCandidate">The major version apt would install (<c>apt-cache policy nodejs</c>), or null.</param>
/// <param name="DnfCandidate">The major version dnf would install, or null.</param>
/// <param name="CodalusNodeAvailable">Codalus's own Node.js has a build for this OS and CPU.</param>
public sealed record AcpNodeFacts(PlatformKind Platform, string? Distribution, bool HasWinget, bool HasBrew, bool HasSnap, int? AptCandidate, int? DnfCandidate, bool CodalusNodeAvailable);

/// <summary>
/// How Node.js is installed for an agent (ADR 0083), through the user's own package managers only, each with its exact
/// command: winget on Windows (then the PATH is read again from the registry), Homebrew on macOS, snap on Ubuntu, apt or
/// dnf only when the version they would install is new enough (and npm with it on Debian and Ubuntu, where npx is a
/// package of its own), and nodejs.org's page everywhere. Codalus's own Node.js is offered only where none of those can
/// (D-node), as an opt-in.
/// </summary>
public static class AcpNodeInstall
{
    public const string Page = "https://nodejs.org/en/download";

    /// <summary>The snap channel offered on Ubuntu (Node.js 24, the current LTS).</summary>
    public const string SnapChannel = "24/stable";

    /// <summary>The options for <paramref name="facts"/> when Node.js <paramref name="major"/> or later is needed (null: any), and why a package manager isn't offered.</summary>
    public static (IReadOnlyList<AcpInstallOption> Options, string? Note) Options(AcpNodeFacts facts, int? major)
    {
        var options = new List<AcpInstallOption>();
        string? note = null;
        var needed = major ?? 18;
        switch (facts.Platform)
        {
            case PlatformKind.Windows when facts.HasWinget:
                string[] arguments = ["install", "--exact", "--id", "OpenJS.NodeJS.LTS", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"];
                options.Add(new("winget", "winget", "Node.js LTS for this computer, from the winget repository.", InstallKind.Run, "winget " + string.Join(' ', arguments))
                {
                    Program = "winget",
                    Arguments = arguments,
                    ReadsWindowsPath = true,
                });
                break;
            case PlatformKind.Windows:
                note = "winget isn't installed, so it isn't offered.";
                break;
            case PlatformKind.MacOS when facts.HasBrew:
                options.Add(new("brew", "Homebrew", "Node.js from Homebrew.", InstallKind.Run, "brew install node") { Program = "brew", Arguments = ["install", "node"] });
                break;
            case PlatformKind.MacOS:
                note = "Homebrew isn't installed, so brew install node isn't offered.";
                break;
            case PlatformKind.Linux:
                var ids = (facts.Distribution ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var debian = ids.Any(i => i is "debian" or "ubuntu");
                var fedora = ids.Any(i => i is "fedora" or "rhel" or "centos");
                if (ids.Contains("ubuntu") && facts.HasSnap)
                {
                    var snap = $"sudo snap install node --classic --channel={SnapChannel}";
                    options.Add(new("snap", "Snap", "Node.js 24 from the Snap Store.", InstallKind.Terminal, snap) { Program = "sudo", Arguments = ["snap", "install", "node", "--classic", "--channel=" + SnapChannel] });
                }

                if (debian && facts.AptCandidate >= needed)
                {
                    options.Add(new("apt", "apt", $"Node.js {facts.AptCandidate} and npm from the distribution's packages.", InstallKind.Terminal, "sudo apt-get install -y nodejs npm") { Program = "sudo", Arguments = ["apt-get", "install", "-y", "nodejs", "npm"] });
                }
                else if (debian)
                {
                    note = facts.AptCandidate is { } apt
                        ? $"apt isn't offered: the distribution's nodejs package is {apt}, and {needed} or later is needed."
                        : "apt isn't offered: the distribution has no nodejs package.";
                }

                if (fedora && facts.DnfCandidate >= needed)
                {
                    options.Add(new("dnf", "dnf", $"Node.js {facts.DnfCandidate} from the distribution's packages.", InstallKind.Terminal, "sudo dnf install -y nodejs npm") { Program = "sudo", Arguments = ["dnf", "install", "-y", "nodejs", "npm"] });
                }
                else if (fedora)
                {
                    note = facts.DnfCandidate is { } dnf
                        ? $"dnf isn't offered: the distribution's nodejs package is {dnf}, and {needed} or later is needed."
                        : "dnf isn't offered: the distribution has no nodejs package.";
                }

                break;
        }

        var packageManager = options.Count > 0;
        options.Add(new("page", "nodejs.org", "Download an installer or an archive yourself.", InstallKind.OpenPage, Page));
        if (!packageManager && facts.CodalusNodeAvailable)
        {
            // D-node: only where nothing of the user's can install a new enough Node.js.
            options.Add(new("codalus-node", $"Codalus's Node.js {Node.NodeRuntime.Version}", "The checksum-verified Node.js Codalus already uses for npm scripts, for external agents only. Your own stays as it is.", InstallKind.Run, $"https://nodejs.org/dist/v{Node.NodeRuntime.Version}/") { IsCodalusNode = true });
        }

        return (options, note);
    }

    /// <summary>The major version in <c>apt-cache policy nodejs</c>'s <c>Candidate:</c> line (<c>18.19.1+dfsg-6ubuntu5</c> → 18), or null.</summary>
    public static int? AptCandidate(string policy) =>
        policy.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Candidate:", StringComparison.Ordinal)) is { } line
            ? Major(line["Candidate:".Length..].Trim())
            : null;

    /// <summary>The major version in <c>dnf info nodejs</c>'s <c>Version</c> line, or null.</summary>
    public static int? DnfCandidate(string info) =>
        info.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Version", StringComparison.Ordinal) && l.Contains(':', StringComparison.Ordinal)) is { } line
            ? Major(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim())
            : null;

    /// <summary>The major version of a version string (<c>v22.11.0</c>, <c>1:20.19.2-1</c>, <c>18.19.1+dfsg</c>), or null (<c>(none)</c>).</summary>
    public static int? Major(string version)
    {
        var text = version.Trim().TrimStart('v');
        if (text.IndexOf(':', StringComparison.Ordinal) is var epoch and >= 0)
        {
            text = text[(epoch + 1)..];
        }

        var digits = new string([.. text.TakeWhile(char.IsAsciiDigit)]);
        return digits.Length > 0 && int.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out var major) ? major : null;
    }
}
