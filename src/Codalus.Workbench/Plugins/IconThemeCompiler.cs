using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Codalus.Rpc;

namespace Codalus.Workbench.Plugins;

/// <summary>An icon theme's SVG icon as drawing operations (eng/icons/CompileIcons.cs's format), or why it has none.</summary>
/// <param name="Path">The SVG file.</param>
/// <param name="Size">Its design size (the view box's width).</param>
/// <param name="Ops">Its operations: fills, figures, lines, curves, arcs.</param>
/// <param name="Numbers">The operations' numbers.</param>
/// <param name="Colors">Each fill's ARGB color (0: the foreground).</param>
/// <param name="Error">Why it can't be drawn, when it can't.</param>
public sealed record CompiledSvgIcon(string Path, double Size, string Ops, float[] Numbers, uint[] Colors, string? Error);

/// <summary>
/// Converts the SVG icons of plugins' icon themes (M22b) in <c>codalus-pluginhost --compile-icons</c>, never in the
/// shell, and keeps the result in the cache folder under a key of the files' paths, sizes and times, so a theme is
/// converted once.
/// </summary>
public static class IconThemeCompiler
{
    public static async Task<IReadOnlyList<CompiledSvgIcon>> CompileAsync(IReadOnlyList<string> svgs, string cacheDirectory, ProcessLaunchInfo host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(svgs);
        ArgumentNullException.ThrowIfNull(host);
        if (svgs.Count == 0)
        {
            return [];
        }

        var folder = Path.Combine(cacheDirectory, "icon-themes");
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, Key(svgs) + ".json");
        if (!File.Exists(output))
        {
            var list = output + ".list";
            var partial = output + ".partial";
            await File.WriteAllLinesAsync(list, svgs, cancellationToken).ConfigureAwait(false);
            try
            {
                var start = new ProcessStartInfo(host.FileName) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var argument in host.Arguments.Concat(["--compile-icons", partial, list]))
                {
                    start.ArgumentList.Add(argument);
                }

                using var process = Process.Start(start) ?? throw new InvalidOperationException("The plugin host didn't start.");
                var errors = process.StandardError.ReadToEndAsync(cancellationToken);
                _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    throw;
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"The icon converter failed ({process.ExitCode}): {await errors.ConfigureAwait(false)}");
                }

                File.Move(partial, output, overwrite: true);
            }
            finally
            {
                File.Delete(list);
            }
        }

        return Read(await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false));
    }

    private static List<CompiledSvgIcon> Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var icons = new List<CompiledSvgIcon>();
        foreach (var icon in document.RootElement.GetProperty("icons").EnumerateArray())
        {
            var path = icon.GetProperty("path").GetString()!;
            icons.Add(icon.TryGetProperty("error", out var error)
                ? new CompiledSvgIcon(path, 0, string.Empty, [], [], error.GetString())
                : new CompiledSvgIcon(
                    path,
                    icon.GetProperty("size").GetDouble(),
                    icon.GetProperty("ops").GetString()!,
                    [.. icon.GetProperty("numbers").EnumerateArray().Select(n => n.GetSingle())],
                    [.. icon.GetProperty("colors").EnumerateArray().Select(c => c.GetUInt32())],
                    null));
        }

        return icons;
    }

    /// <summary>The files' paths, sizes and times: a changed or replaced icon makes a new key.</summary>
    private static string Key(IReadOnlyList<string> svgs)
    {
        var text = new StringBuilder();
        foreach (var svg in svgs)
        {
            var info = new FileInfo(svg);
            text.Append(svg).Append('|').Append(info.Exists ? info.Length : -1).Append('|')
                .Append(info.Exists ? info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "-").Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}
