using Avalonia.Media;

namespace Codalus.Shell.Editor;

/// <summary>How a minimap marks a line: not at all, or as a diff's added or removed line.</summary>
public enum MinimapMark
{
    None,
    Added,
    Removed,
}

/// <summary>
/// What a <see cref="Minimap"/> draws its lines with: the editor's semantic tokens (<see cref="SemanticColorizer"/>), or
/// a diff pane's grammar colors and changed lines (<see cref="DiffMinimapColors"/>).
/// </summary>
public interface IMinimapColors
{
    /// <summary>Changes each time the words' colors do (the minimap keeps what it drew of each line until then).</summary>
    int Version { get; }

    /// <summary>The color of the word at a zero-based line and character, or null for the text color.</summary>
    IBrush? WordBrush(int line, int character);

    /// <summary>How a zero-based line is marked. It's asked on every frame, for the lines shown only, so it must be cheap.</summary>
    MinimapMark MarkOf(int line);
}
