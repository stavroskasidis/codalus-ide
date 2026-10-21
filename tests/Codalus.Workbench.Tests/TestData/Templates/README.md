# Recorded template data (New Project, ADR 0082)

Recorded from the .NET SDK 10.0.401 and 11.0.100-rc.2 on Linux, trimmed to what `ProjectTemplateCatalogTests` needs:

- `templatecache-11.0.json`, `templatecache-10.0.json`: the template engine's caches (`~/.templateengine/dotnetcli/<sdk>/templatecache.json`)
  with a few templates kept, both channels' xUnit, console's string `"isHidden":"true"`, an item template and a
  template the list doesn't show. `MountPointUri` reads `{packages}/<name>.nupkg`; the tests zip `Packages/<name>/`
  into it at run time.
- `Packages/`: each template's `.template.config/template.json` and `ide.host.json` with their exact spellings (comments,
  `PersistenceScope` and `persistenceScope`), console's French strings, and a few content files, in both package layouts
  (`content/…` and the root).
- `list-project.txt`, `list-project-columns-all.txt`: `dotnet new list --type project` (with `--columns-all`), trimmed.
- `blazor-help.txt`: `dotnet new blazor --help`.
- `AspireStandIn/`: stand-ins for the Aspire templates (`aspire-apphost`, `aspire-servicedefaults`), installed into a test
  hive from the folder, so Enlist in Aspire orchestration is tested offline.

The template files come from the .NET SDK and ASP.NET Core template packages (MIT license, © .NET Foundation and
Contributors).
