using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// A template's options from <c>dotnet new &lt;template&gt; --help</c>: New Project's fallback without the template cache
/// (ADR 0082). The framework cases come from the retired Templates plugin's tests.
/// </summary>
public sealed class TemplateHelpTests
{
    private const string Help = """
        Console App (C#)
        Options:
          -f, --framework <net10.0|net11.0|net6.0|net7.0|net8.0|net9.0>  The target framework for the project.
                                                                         Type: choice
                                                                           net11.0  Target net11.0
                                                                           net10.0  Target net10.0
                                                                         Default: net10.0
          --langVersion <langVersion>                                    Sets the LangVersion property in the created project file
                                                                         Type: text
        """;

    [Fact]
    public void The_default_comes_first_then_the_newest()
    {
        var frameworks = TemplateHelp.Frameworks(Help);
        Assert.Equal(["net10.0", "net11.0", "net9.0", "net8.0", "net7.0", "net6.0"], frameworks.Select(f => f.Framework));
        Assert.Equal([true, false, false, false, false, false], frameworks.Select(f => f.IsDefault));
    }

    [Fact]
    public void A_narrow_console_lists_the_choices_only_under_the_option()
    {
        const string help = """
            Template options:
              -f, --framework <choice>     The target framework for the project.
                                           Type: choice
                                             net11.0  Target net11.0
                                             net10.0  Target net10.0
                                           Default: net11.0
              --langVersion <langVersion>  Sets the LangVersion property in the created
            """;
        Assert.Equal([("net11.0", true), ("net10.0", false)], TemplateHelp.Frameworks(help));
    }

    [Fact]
    public void Net_standard_comes_after_net_and_templates_without_the_option_have_none()
    {
        var frameworks = TemplateHelp.Frameworks("  -f, --framework <net10.0|netstandard2.0|netstandard2.1|net11.0>  The target framework.\n");
        Assert.Equal(["net11.0", "net10.0", "netstandard2.1", "netstandard2.0"], frameworks.Select(f => f.Framework));
        Assert.Empty(TemplateHelp.Frameworks("Solution File (C#)\nOptions:\n  --format <sln|slnx>\n"));
        Assert.Equal(".NET 11.0", TemplateHelp.Display("net11.0"));
        Assert.Equal(".NET Standard 2.1", TemplateHelp.Display("netstandard2.1"));
        Assert.Equal(".NET Core 3.1", TemplateHelp.Display("netcoreapp3.1"));
        Assert.Equal("net48x", TemplateHelp.Display("net48x"));
    }

    [Fact]
    public void Bool_choice_and_text_options_with_defaults_and_choice_descriptions()
    {
        var options = TemplateHelp.Parameters(TemplateTestData.Text("blazor-help.txt"));
        Assert.Equal(["--interactivity", "--empty", "--auth", "--use-local-db", "--all-interactive", "--no-https", "--use-program-main", "--localhost-tld"], options.Select(o => o.CliOption));

        var interactivity = options[0];
        Assert.Equal((TemplateDataType.Choice, "Server"), (interactivity.DataType, interactivity.DefaultValue));
        Assert.Equal(["None", "Server", "WebAssembly", "Auto"], interactivity.Choices.Select(c => c.Value));
        Assert.Equal("Runs on the server", interactivity.Choices[1].Description);
        Assert.Equal("Chooses which interactive render mode to use for interactive components", interactivity.Label);

        var empty = options[1];
        Assert.Equal((TemplateDataType.Bool, "false"), (empty.DataType, empty.DefaultValue));
        Assert.Equal("Configures whether to omit sample pages and styling that demonstrate basic usage patterns", empty.Label);
        Assert.Equal("(InteractivityPlatform != \"None\")", options.Single(o => o.CliOption == "--all-interactive").EnabledCondition);

        const string text = """
            Template options:
              -p, --port <port>  The port to listen on.
                                 Type: int
                                 Default: 5000
              --title <title>    The title. It shows in the window.
                                 Type: string
            """;
        var parsed = TemplateHelp.Parameters(text);
        Assert.Equal([(TemplateDataType.Integer, "5000", "The port to listen on"), (TemplateDataType.Text, (string?)null, "The title")], parsed.Select(p => (p.DataType, p.DefaultValue, p.Label)));
    }
}
