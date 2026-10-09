# Solutions and projects

Daedalus Studio opens `.sln` and `.slnx` solutions and SDK-style projects (`.csproj`, and the structure of `.fsproj` and `.vbproj`). Legacy, non-SDK-style project files are shown but not loaded.

## Solution Explorer

The tree shows the solution, its solution folders, projects, project folders and files (nested files, like `Form.Designer.cs`, sit under their parents). Each project also has its **Dependencies**:
- frameworks
- packages
- projects
- assemblies
- analyzers

| To | Do |
|---|---|
| Open a file in a preview tab (replaced by the next preview) | Single-click it. |
| Keep a file open | Double-click it, or edit it. |
| Create a file or folder | Use the context menu. New C# files get the right namespace. |
| Rename | Press `F2`. Renaming a project folder updates the solution and project references. |
| Delete | Press `Delete`. Files go to the OS trash. |
| Move or copy | Cut, Copy and Paste, or drag and drop. |
| Change the solution | Add Existing Project, New Solution Folder, Remove from Solution. |
| Add a project reference | Add Project Reference on a project. Remove one by deleting it under Dependencies. |
| Make a project the one F5 and Ctrl+F5 start | **Set as Startup Project** (the startup project is bold). |

Daedalus Studio watches the files: changes made outside the IDE (a `git pull`, another editor) are picked up, and the affected projects are evaluated again.

## Configurations and target frameworks

- The status bar shows the solution configuration (`Debug | Any CPU`). Click it to switch. Projects are evaluated again with the configuration they map to.
- For a project that targets several frameworks, the editor's project-context picker (top of the editor) chooses which framework the language service uses.

## Restore

Packages are restored in the background when a trusted solution opens (setting **Projects → Restore on Open**). Project → Restore Packages restores again; the output is in View → Output → Restore.

## New projects and solutions

The built-in **Templates** plugin creates projects and solutions with `dotnet new` (File → New Project or New Solution, or the command palette).
