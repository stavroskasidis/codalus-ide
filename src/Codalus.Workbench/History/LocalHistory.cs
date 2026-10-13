using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Codalus.Workbench.History;

/// <summary>A saved version of a file in the local history.</summary>
public sealed record HistoryEntry(string Path, DateTimeOffset Time, string File)
{
    public string Read() => System.IO.File.ReadAllText(File);
}

/// <summary>
/// Local history (M12b): a copy of each file every time it is saved, kept apart from Git under the state folder
/// (<c>history/&lt;hash of the path&gt;/</c>), at most <see cref="MaxEntries"/> per file and only for files up to
/// <see cref="MaxFileSize"/>. Saving the same text twice records it once. Thread-safe.
/// </summary>
public sealed class LocalHistory(string directory)
{
    public const int MaxEntries = 50;
    public const int MaxFileSize = 2 * 1024 * 1024;
    private const string TimeFormat = "yyyyMMdd'T'HHmmssfff";
    private readonly Lock _gate = new();

    public string Directory { get; } = directory;

    /// <summary>Records <paramref name="text"/> as the newest version of <paramref name="path"/>; false when skipped.</summary>
    public bool Record(string path, string text, DateTimeOffset? time = null)
    {
        if (text.Length > MaxFileSize)
        {
            return false;
        }

        lock (_gate)
        {
            var folder = FolderOf(path);
            System.IO.Directory.CreateDirectory(folder);
            File.WriteAllText(System.IO.Path.Combine(folder, "path.txt"), path);
            var entries = EntriesLocked(path);
            if (entries.Count > 0 && entries[0].Read() == text)
            {
                return false;
            }

            var stamp = (time ?? DateTimeOffset.UtcNow).UtcDateTime;
            var file = System.IO.Path.Combine(folder, stamp.ToString(TimeFormat, CultureInfo.InvariantCulture) + ".txt");
            for (var n = 1; File.Exists(file); n++)
            {
                file = System.IO.Path.Combine(folder, stamp.AddMilliseconds(n).ToString(TimeFormat, CultureInfo.InvariantCulture) + ".txt");
            }

            File.WriteAllText(file, text);
            foreach (var old in EntriesLocked(path).Skip(MaxEntries))
            {
                File.Delete(old.File);
            }

            return true;
        }
    }

    /// <summary>The saved versions of a file, newest first.</summary>
    public IReadOnlyList<HistoryEntry> Entries(string path)
    {
        lock (_gate)
        {
            return EntriesLocked(path);
        }
    }

    private List<HistoryEntry> EntriesLocked(string path)
    {
        var folder = FolderOf(path);
        if (!System.IO.Directory.Exists(folder))
        {
            return [];
        }

        return [.. System.IO.Directory.EnumerateFiles(folder, "*.txt")
            .Where(f => !System.IO.Path.GetFileName(f).Equals("path.txt", StringComparison.Ordinal))
            .Select(f => (File: f, Ok: DateTime.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(f), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t), Time: t))
            .Where(e => e.Ok)
            .OrderByDescending(e => e.Time)
            .Select(e => new HistoryEntry(path, new DateTimeOffset(e.Time, TimeSpan.Zero), e.File))];
    }

    private string FolderOf(string path)
    {
        var key = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? path.ToUpperInvariant() : path;
        return System.IO.Path.Combine(Directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24]);
    }
}
