using Codalus.Workbench.Languages;

namespace Codalus.Workbench.Tests;

public class LanguageRegistryTests
{
    private static LanguageRegistry Registry()
    {
        var registry = new LanguageRegistry();
        registry.Register(new LanguageDefinition("jsonc", "JSON with Comments") { FileNamePatterns = ["tsconfig.*.json", "settings/profile-?.json"] });
        registry.Register(new LanguageDefinition("dockerfile", "Dockerfile") { FileNamePatterns = ["Dockerfile.*"] });
        return registry;
    }

    [Theory]
    [InlineData("tsconfig.app.json", "jsonc")]
    [InlineData("TSCONFIG.Base.JSON", "jsonc")]
    [InlineData("tsconfig.json", null)]
    [InlineData("tsconfig.app.json.bak", null)]
    [InlineData("profile-1.json", "jsonc")]
    [InlineData("profile-12.json", null)]
    [InlineData("Dockerfile.dev", "dockerfile")]
    [InlineData("Dockerfile", null)]
    public void File_name_patterns_match_star_and_question_mark_by_the_last_segment(string name, string? language) =>
        Assert.Equal(language, Registry().ForFile(Path.Combine("work", name))?.Id);
}
