using DaedalusStudio.Workbench.Publishing;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>Publish profiles (M17c, ADR 0042): the <c>.pubxml</c> format, its unknown properties kept, and a publish's properties.</summary>
public sealed class PublishProfilesTests
{
    [Fact]
    public void A_profile_reads_and_writes_the_pubxml_format_keeping_what_it_doesnt_know()
    {
        const string visualStudio = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project>
              <PropertyGroup>
                <DeleteExistingFiles>true</DeleteExistingFiles>
                <ExcludeApp_Data>false</ExcludeApp_Data>
                <LaunchSiteAfterPublish>true</LaunchSiteAfterPublish>
                <LastUsedBuildConfiguration>Release</LastUsedBuildConfiguration>
                <LastUsedPlatform>Any CPU</LastUsedPlatform>
                <PublishProvider>FileSystem</PublishProvider>
                <PublishUrl>bin\Release\net10.0\publish\</PublishUrl>
                <WebPublishMethod>FileSystem</WebPublishMethod>
                <_TargetId>Folder</_TargetId>
                <SiteUrlToLaunchAfterPublish />
                <TargetFramework>net10.0</TargetFramework>
                <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
                <ProjectGuid>8f1d2a3b-4c5d-4e6f-8a9b-0c1d2e3f4a5b</ProjectGuid>
                <SelfContained>true</SelfContained>
                <PublishSingleFile>true</PublishSingleFile>
              </PropertyGroup>
            </Project>
            """;
        var profile = PublishProfiles.Parse("FolderProfile", visualStudio);
        Assert.Equal(PublishTarget.Folder, profile.Target);
        Assert.Equal("Release", profile.Configuration);
        Assert.Equal("net10.0", profile.TargetFramework);
        Assert.Equal("linux-x64", profile.RuntimeIdentifier);
        Assert.True(profile.SelfContained);
        Assert.True(profile.SingleFile);
        Assert.False(profile.Trimmed);
        Assert.Equal(@"bin\Release\net10.0\publish\", profile.Folder);
        Assert.True(profile.DeleteExistingFiles);
        Assert.Null(profile.Problem());

        profile.Trimmed = true;
        profile.SingleFile = false;
        var written = PublishProfiles.Write(profile);
        Assert.Contains("<_TargetId>Folder</_TargetId>", written, StringComparison.Ordinal);
        Assert.Contains("<ProjectGuid>8f1d2a3b-4c5d-4e6f-8a9b-0c1d2e3f4a5b</ProjectGuid>", written, StringComparison.Ordinal);
        Assert.Contains("<PublishTrimmed>true</PublishTrimmed>", written, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishSingleFile", written, StringComparison.Ordinal);
        var again = PublishProfiles.Parse("FolderProfile", written);
        Assert.True(again.Trimmed);
        Assert.Equal("linux-x64", again.RuntimeIdentifier);

        // A new container profile.
        var container = new PublishProfile { Name = "Image", Target = PublishTarget.Container, ContainerRepository = "web", ContainerImageTag = "1.0" };
        var text = PublishProfiles.Write(container);
        Assert.Contains("<PublishProvider>ContainerRegistry</PublishProvider>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishUrl", text, StringComparison.Ordinal);
        var read = PublishProfiles.Parse("Image", text);
        Assert.Equal(PublishTarget.Container, read.Target);
        Assert.Equal("web", read.ContainerRepository);
        Assert.Equal("1.0", read.ContainerImageTag);
    }

    [Fact]
    public void A_publish_names_the_profile_and_what_it_needs()
    {
        var profile = new PublishProfile { Name = "Aot", NativeAot = true };
        Assert.Contains("runtime", profile.Problem(), StringComparison.Ordinal);
        profile.RuntimeIdentifier = "linux-x64";
        Assert.Null(profile.Problem());
        profile.SingleFile = true;
        Assert.NotNull(profile.Problem());

        var properties = PublishProfiles.Properties(profile);
        Assert.Equal("Aot", properties["PublishProfile"]);
        Assert.Equal("true", properties["_IsPublishing"]);
        Assert.Equal("Release", properties["Configuration"]);
        Assert.False(properties.ContainsKey("LocalRegistry"));
        Assert.Equal("linux-x64", properties["RuntimeIdentifier"]); // for the restore too, as 'dotnet publish -r'
        Assert.Equal("true", properties["SelfContained"]);

        var image = new PublishProfile { Name = "Image", Target = PublishTarget.Container, ContainerRepository = "web" };
        Assert.Equal("Podman", PublishProfiles.Properties(image, "podman")["LocalRegistry"]);
        Assert.Equal("Docker", PublishProfiles.Properties(image, "docker")["LocalRegistry"]);
        image.ContainerRegistry = "registry.example.com";
        Assert.False(PublishProfiles.Properties(image, "docker").ContainsKey("LocalRegistry"));
        Assert.Matches("^(win|linux|osx)-(x64|arm64|x86|arm)$", PublishProfiles.CurrentRuntimeIdentifier());
    }
}
