using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>
/// The quick pick's groups (the device picker's platforms): while nothing is typed a header row starts each group, and
/// headers are never selected or accepted; typing ranks the items alone.
/// </summary>
public sealed class QuickPickGroupTests
{
    [AvaloniaFact]
    public void Headers_start_each_group_and_selection_steps_over_them()
    {
        using var pick = new QuickPickViewModel();
        QuickPickItem[] items =
        [
            new("Pixel 8") { Group = "Android (net10.0-android)" },
            new("Tablet") { Group = "Android (net10.0-android)" },
            new("iPhone 17") { Group = "iOS (net10.0-ios)" },
            new("Open the Devices window") { Group = string.Empty },
        ];
        var picked = pick.ShowAsync(items, "Run on", activeIndex: 2);

        Assert.Equal(
            ["# Android (net10.0-android)", "Pixel 8", "Tablet", "# iOS (net10.0-ios)", "iPhone 17", "# ", "Open the Devices window"],
            pick.Rows.Select(r => r.IsHeader ? "# " + r.Label : r.Label));
        Assert.False(pick.Rows[5].HasHeaderText); // a bare separator
        Assert.False(pick.HasMore);

        // The active item is selected where it is; Up and Down, Home and End step over headers.
        Assert.Equal("iPhone 17", pick.Selected?.Label);
        pick.Move(-1);
        Assert.Equal("Tablet", pick.Selected?.Label);
        pick.Move(1);
        Assert.Equal("iPhone 17", pick.Selected?.Label);
        pick.Move(1);
        Assert.Equal("Open the Devices window", pick.Selected?.Label);
        pick.Move(1);
        Assert.Equal("Open the Devices window", pick.Selected?.Label);
        pick.MoveToEdge(last: false);
        Assert.Equal("Pixel 8", pick.Selected?.Label);
        pick.Move(-1);
        Assert.Equal("Pixel 8", pick.Selected?.Label);

        // A header can't be accepted.
        pick.Accept(pick.Rows[3]);
        Assert.False(picked.IsCompleted);

        // Typing ranks the items alone.
        pick.Query = "iphone";
        Assert.Equal("iPhone 17", Assert.Single(pick.Rows).Label);
        pick.Accept();
        Assert.Same(items[2], picked.Result);
    }
}
