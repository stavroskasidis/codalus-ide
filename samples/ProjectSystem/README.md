# ProjectSystem fixture

Exercises the project system: solution folders (nested), solution items, multi-targeting, file nesting, linked files, explicit and conditional items, project and package references.

- `src/Items/Items.csproj` (M26, not in the solution; the item tests open it on its own): one file per common Build Action, a linked file, a `.resx` with its generated class, and a `.tt` text template with an include.
- `FolderOnly/`: a folder with two projects and no solution file, for folder mode, with `.daedalus/tasks.json` and `.daedalus/launch.json`.
