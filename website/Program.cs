using DaedalusStudio.Website.Site;

// The website. Develop with live reload:  dotnet watch --project website
// Export for GitHub Pages:                dotnet run --project website --no-launch-profile -- --export <dir>
//                                           [--stable <tag>] [--nightly <tag>] [--ref <branch>] [--base-url <url>] [--repo owner/name]
// Without --stable and --nightly the download links lead to the releases page.
return await SiteHost.RunAsync(args);
