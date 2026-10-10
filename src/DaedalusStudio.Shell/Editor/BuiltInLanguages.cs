using DaedalusStudio.Workbench.Languages;

namespace DaedalusStudio.Shell.Editor;

/// <summary>
/// The languages the shell knows without plugins (ADR 0016): the files of .NET repositories and the common languages
/// next to them. Grammars and language configurations come from TextMateSharp's bundled VS Code grammars (MIT), looked up
/// by language id when a file first opens (<see cref="TextMateSupport"/>), so startup doesn't load them.
/// </summary>
public static class BuiltInLanguages
{
    public static void Register(LanguageRegistry registry)
    {
        static LanguageDefinition L(string id, string name, string[] extensions, string[]? fileNames = null, string[]? patterns = null, string? firstLine = null) =>
            new(id, name) { Extensions = extensions, FileNames = fileNames ?? [], FileNamePatterns = patterns ?? [], FirstLine = firstLine };

        foreach (var language in new[]
        {
            L(LanguageRegistry.PlainText, "Plain Text", [".txt", ".log"]),
            L("csharp", "C#", [".cs", ".csx"]),
            L("msbuild", "MSBuild", [".csproj", ".fsproj", ".vbproj", ".proj", ".props", ".targets", ".projitems", ".shproj", ".esproj", ".sqlproj", ".dcproj", ".wapproj", ".tasks"]),
            L("xml", "XML", [".xml", ".xsd", ".xsl", ".xslt", ".xaml", ".axaml", ".resx", ".runsettings", ".nuspec", ".config", ".slnx", ".svg", ".wsdl", ".plist", ".vsixmanifest", ".manifest", ".appxmanifest", ".dtd"]),
            L("json", "JSON", [".json", ".slnf", ".webmanifest", ".har", ".jsonl"], [".babelrc", ".eslintrc", "composer.lock", ".watchmanconfig"]),
            L("jsonc", "JSON with Comments", [".jsonc", ".code-workspace"], ["tsconfig.json", "jsconfig.json", ".devcontainer.json", "devcontainer.json", ".jshintrc", ".jscsrc", "launchSettings.json", "settings.json", "keybindings.json"], ["tsconfig.*.json", "jsconfig.*.json"]),
            L("yaml", "YAML", [".yml", ".yaml"], [".clang-format", ".clang-tidy"]),
            L("markdown", "Markdown", [".md", ".markdown", ".mdown", ".mkd"]),
            L("editorconfig", "EditorConfig", [], [".editorconfig"]),
            L("ini", "Ini", [".ini", ".cfg", ".conf"], [".gitconfig", ".npmrc"]),
            L("properties", "Properties", [".properties", ".gitattributes"], [".gitattributes"]),
            L("ignore", "Ignore", [".gitignore", ".dockerignore", ".npmignore"], [".gitignore", ".dockerignore", ".npmignore"]),
            L("powershell", "PowerShell", [".ps1", ".psm1", ".psd1", ".pssc", ".psrc"]),
            L("shellscript", "Shell Script", [".sh", ".bash", ".zsh", ".ksh"], [".bashrc", ".bash_profile", ".zshrc", ".zshenv", ".profile"], firstLine: @"^#!.*\b(bash|zsh|sh|ksh|dash)\b"),
            L("bat", "Batch", [".bat", ".cmd"]),
            L("dockerfile", "Dockerfile", [".dockerfile", ".containerfile"], ["Dockerfile", "Containerfile"], ["Dockerfile.*", "Containerfile.*"]),
            L("makefile", "Makefile", [".mk", ".mak"], ["Makefile", "makefile", "GNUmakefile"]),
            L("html", "HTML", [".html", ".htm", ".xhtml", ".shtml"]),
            L("css", "CSS", [".css"]),
            L("scss", "SCSS", [".scss"]),
            L("less", "Less", [".less"]),
            L("javascript", "JavaScript", [".js", ".mjs", ".cjs"], firstLine: @"^#!.*\bnode"),
            L("typescript", "TypeScript", [".ts", ".mts", ".cts"]),
            L("javascriptreact", "JavaScript JSX", [".jsx"]),
            L("typescriptreact", "TypeScript JSX", [".tsx"]),
            L("sql", "SQL", [".sql"]),
            L("python", "Python", [".py", ".pyi"], firstLine: @"^#!.*\bpython"),
            L("fsharp", "F#", [".fs", ".fsi", ".fsx", ".fsscript"]),
            L("vb", "Visual Basic", [".vb", ".vbs", ".bas"]),
            L("diff", "Diff", [".diff", ".patch", ".rej"]),
            L("cpp", "C++", [".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".h", ".inl"]),
            L("c", "C", [".c"]),
            L("java", "Java", [".java"]),
            L("go", "Go", [".go"]),
            L("rust", "Rust", [".rs"]),
            L("lua", "Lua", [".lua"]),
            L("ruby", "Ruby", [".rb", ".gemspec"], ["Gemfile", "Rakefile"]),
            L("php", "PHP", [".php"]),
            L("razor", "Razor", [".cshtml", ".razor"]),
        })
        {
            registry.Register(language);
        }
    }
}
