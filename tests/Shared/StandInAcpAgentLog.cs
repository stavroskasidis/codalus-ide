using System.Text.Json;

namespace Codalus.Tests;

/// <summary>Reading the stand-in ACP agent's log (<c>ACP_FAKE_LOG</c>, tests/Shared/StandInAcpAgent.cs) from a test.</summary>
public static class StandInAcpAgentLog
{
    /// <summary>
    /// The log, one JSON message per line (none before the stand-in starts), read beside it. The stand-in appends a line
    /// for every message it receives, and File.ReadAllLines' own open, which shares only reading, made that append fail
    /// on Windows, which ended the stand-in mid-test (Windows CI, develop's run 43390605331, while a test polled the log
    /// for a prompt). Only complete lines: the last one may still be being written.
    /// </summary>
    public static List<JsonElement> Read(string path)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        var lines = text.Split('\n');
        return [.. lines.Take(lines.Length - 1).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Select(l => JsonElement.Parse(l))];
    }
}
