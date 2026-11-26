# 0111. The UI's text: variable fonts, smoothing, its font and its scale

- **Status:** accepted (2026-11-26; the maintainer's answers of 2026-11-26)
- **Date:** 2026-11-26
- **Deciders:** maintainer
- **Builds on:** M23 (Fonts and Colors), M29 and M34d (the UI's 9 pt text, the `Shell.FontSize` tokens), D27 (Linux on X11 and on Wayland through XWayland), `ShellSystemFonts` (the shell's own system font collection)

## Context

A user's report (#556), on Fedora with KDE Plasma's Wayland session and fractional scaling: the UI's text (menus, tabs, the Solution Explorer) looks blurry next to the system's other apps, and only the editor's font can be changed. It proposed native Wayland, better scaling detection, and settings for the UI's font, its size and a scale.

The maintainer's screenshots narrowed it: the blurry text was the bold and semibold text (the Settings page's title and settings' names, the active tab's title), while the regular text beside it was sharp. Reproduced in the container:

- **Variable fonts were laid out at the wrong weight.** Fedora ships Noto Sans (and GNOME's Cantarell) as variable fonts: one file with a weight axis. fontconfig matches the named instance for a weight and Skia draws that instance's outlines, but Avalonia's HarfBuzz font is made from the file's tables with no variation set, so it lays every weight out at the default instance's advances. A variable family's text was 108.9 px wide at Regular, SemiBold and Bold alike: bold glyphs at Regular's widths, crowded into each other, which reads as smeared. Every instance also reports the file's default weight, so the shell's collection took the "nearest" face for a weight it hadn't seen (another instance) or made a synthetic of the file's default instance.
- **Text was smoothed per subpixel whatever the system did.** Avalonia smooths per subpixel unless told otherwise. fontconfig says grayscale on most Linux desktops (GNOME's and KDE's defaults), so every native app beside Codalus drew grayscale text, and Codalus's had colored fringes. That reads as blur, more so on a dark theme and on a surface the compositor scales.
- **Native Wayland isn't available.** Avalonia 12.1.6 has no Wayland backend; only its X11 backend runs on Linux (D27 moves to Wayland when Avalonia's is stable). Under XWayland, a window is sharp at a fractional scale only when the compositor lets X11 apps scale themselves (KDE's "Apply scaling themselves", GNOME's Xwayland native scaling), which then tells them the scale through `Xft.dpi`. Avalonia already reads that. Otherwise the compositor scales the window's bitmap up, and no X11 app can avoid it.
- **Scale.** Avalonia's X11 backend multiplies the scale it finds by `AVALONIA_GLOBAL_SCALE_FACTOR`, read once as it starts. Windows and macOS have no such factor: the window's scale is the system's.

## Decision

### Variable fonts laid out at their weight

- The shell's collection tells a variable font by its `fvar` table (`ShellSystemFonts.IsVariable`), and sets the HarfBuzz font's `wght` axis to the weight of the instance the platform matched, which Skia's typeface reports (not always the one asked for: fontconfig gives a font's nearest named instance, and CoreText matched another for Kefa III's SemiBold) (`VariableFonts.Apply`, `hb_font_set_variations` on the font behind Avalonia's internal `HarfBuzzTypeface`, reached through `UnsafeAccessor`). HarfBuzz clamps it to the axis.
- A variable font's instance is filed only under the style asked for: no nearest match and no synthetic from the file. fontconfig matched the instance for the weight.
- A static family without a semibold face still gets its regular face emboldened, as before: the bold face is wider, and the setup window's text was cut off with it.
- `TODO(issue)`: drop `VariableFonts` once Avalonia applies a face's variation itself.

### Text rendering follows the system (`workbench.textRendering`)

- `system`, the default (the maintainer's choice, over grayscale everywhere and over grayscale on Linux only):
  - Linux reads fontconfig for the default sans-serif font after its rules (`FcFontMatch`, so a desktop's edits apply): antialias, `rgba` (subpixel for RGB only, the one order Skia draws) and `hintstyle` (none, slight, medium and full as Avalonia's None, Light and Strong).
  - Windows reads its font smoothing: off, standard (grayscale) or ClearType (subpixel, in RGB order).
  - macOS is grayscale, as it has drawn text since 10.14.
- `grayscale`, `subpixel` and `aliased` override the system's way; the hinting stays the system's.
- `TextOptions` isn't a styled property, so `TextRendering` sets it on every top level as its template is applied, before its first frame (windows, and popups that are windows of their own), and on a change sets it again and redraws each visual of each open one.

### The UI's font and size (`workbench.fontFamily`, `workbench.fontSize`)

- The family: empty for the system's UI font (`$Default`), or a comma-separated list. It sets `Shell.FontFamily` (the window and popup styles) and Fluent's `ContentControlThemeFontFamily`.
- The size: the body text's, in pixels, 8 to 24, default 12. The four `Shell.FontSize` tokens follow in proportion to their defaults (11, 12, 14 and 18 at 12), to the half pixel, and so do the rows that hold a line of text (`Shell.Size.Row` 22, `Shell.Size.TreeRow` 24), to the whole pixel, so a larger font isn't cut off.
- Both apply at once. The views' last hard-coded 12 px and 11 px UI text sizes now use the tokens. Monospace text, the theme pictures and the designers' compact lists keep their sizes.
- Fonts and Colors gets **UI Font…** beside the editor's button, now **Editor Font…** (the maintainer's choice, over the Settings page alone): it opens Settings on the two settings, the classic IDE's Environment font beside its Text Editor font.

### The UI's scale (`workbench.uiScale`, Linux only)

- A factor over the display's scale, 0.5 to 4, default 1, applied at the next start (the maintainer's choice, over a live layout transform on every OS, whose text is drawn transformed and which every window and popup would have to wrap, and over no scale).
- `Program.Main` reads it from the user's settings file before the windowing system starts and sets `AVALONIA_GLOBAL_SCALE_FACTOR`; `App.RegisterServices` removes the variable once Avalonia read it, so no process the IDE starts (hosts, the terminal, the user's own Avalonia app) inherits it. A variable of Avalonia's the user set (`AVALONIA_GLOBAL_SCALE_FACTOR`, `AVALONIA_SCREEN_SCALE_FACTORS`) wins.
- The window is drawn at that many pixels, so text stays as sharp as at the system's own scale.
- It's registered on every OS, so the catalogue is the same everywhere, and says "Linux only". A change on Linux offers **Restart**.

### In the first-run setup

The maintainer's request, the same day: the setup offers them too. This extends [ADR 0096](0096-the-first-run-setup-asks-about-ai.md), which kept the Appearance step to the editor's font size ("a zoom for the UI is a feature of its own").

- Under the editor's font size, **The IDE's text**: the UI font (the system's UI font, then the installed families), the UI text size (8 to 24) with a smaller and a larger button, and Text smoothing as segments (As the system, Grayscale, Subpixel; Aliased stays in Options, and a saved Aliased is kept unless one is chosen). They preview on the setup window and the IDE behind it, as a theme does (`UiFont.Preview`, `TextRendering.Preview`), and Next saves each that changed; Skip setup drops the preview.
- On Linux, **UI scale** as segments (100% to 200%), saved with the step and applied at the next start, which the step says.
- The summary lists the UI font and size, the smoothing and, on Linux, the scale.

### Wayland's own scaling

- The user guide's Troubleshooting says what makes XWayland windows sharp at a fractional scale: KDE's Display Configuration → Legacy Applications (X11) → **Apply scaling themselves**, and GNOME's `xwayland-native-scaling` experimental feature. Codalus reads the scale those give through `Xft.dpi`.
- Native Wayland stays D27's: when Avalonia has a stable backend.

## Consequences

- On a Linux desktop whose UI font is variable, bold and semibold text is laid out at its own widths. On a desktop that smooths in grayscale, Codalus does too. Windows keeps ClearType where it's on, and macOS draws grayscale.
- Users can pick the UI's font and size, and on Linux its scale, without touching the editor's.
- `UiTextTests` cover the variable font fixture (Inter, cut to ASCII, OFL-1.1, in `samples/Fonts`), an installed variable family when the machine has one (CI's Linux runners don't; Windows has Bahnschrift), the choices of each OS's smoothing, the settings' effect on open windows, and the scale's reading.
