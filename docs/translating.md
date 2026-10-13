# Translating Daedalus Studio

Daedalus Studio's text is in JSON catalogues (M23b): `src/DaedalusStudio.Shell/Localization/<locale>.json`, a flat `{ "key": "text" }` object per language. `en.json` is the source every translation follows. A string missing from a translation shows in English, so a partial translation works.

## For translators

Translations are planned on hosted Weblate, which reads and writes these files (D11; the project isn't set up there yet). Until then, a translation is a pull request:
1. Copy `en.json` to `<locale>.json`, named by its BCP 47 name: `de`, `pt-BR`, `zh-Hans`.
2. Translate the values; leave the keys.
3. Keep `{0}`-style placeholders, `$(icon)` names and the leading `_` of menu mnemonics (`_File`).
4. Run Daedalus Studio with `--locale <locale>` to see it.

A regional file falls back to its language: `pt-BR`, then `pt`, then English. Right-to-left languages (Arabic, Hebrew, Persian, Urdu and others) lay the window out right to left.

Plugins carry their own translations next to their manifests (`daedalus-plugin.nls.<locale>.json`, see [Plugins](plugins.md#translating-the-manifest)).

## For developers

**Keys.** Where the text comes from decides its key:
- **Commands, settings, menus and tool windows** are translated as they register, by id. Their English stays in the code, and `en.json` gets it from there:
  - `command.<id>`, `category.<category>`
  - `setting.<key>.title`, `.description`, `.enum.<value>`, `settingCategory.<category>`
  - `menu.<menu or submenu location>`, `toolWindow.<id>`
- **Views** use `{l:T <view>.<words>}` (`xmlns:l="using:DaedalusStudio.Shell.Infrastructure"`). Their English lives only in `en.json`. `python3 eng/localize-xaml.py <view.axaml>…` moves a view's literal text there.
- **Code**: `Localizer.Get("<key>", "English")` for a string, and `Localizer.Format("<key>", "English {0}", value)` for one with placeholders.

**Tests.** `LocalizationCatalogTests` keeps `en.json` equal to what the code and the views have, and stops any view gaining a hard-coded string (`tests/DaedalusStudio.Shell.Tests/localization-literals.txt`, the count left in each view, may only go down). After changing UI text, run the shell tests with `DAEDALUS_UPDATE_LOCALIZATION=1` to regenerate both files, and commit them.

**Checking.** The pseudo-languages show what isn't translatable yet:
- `daedalus --locale qps-ploc` accents and lengthens every string that goes through the catalogues, so English text stands out. Clipping shows where text grows.
- `daedalus --locale qps-plocm` does the same right to left.
- `--screenshot <png>` captures the window.

**Still in English** (M23c): most views' text, notifications and other messages built in code, and plugins' own runtime text (the plugin API has no translation call yet).
