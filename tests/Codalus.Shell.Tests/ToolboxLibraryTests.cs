using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// #460: the Toolbox with a control library's items as the classic IDE shows them: each in the tab its
/// <c>[ToolboxTabName]</c> names (across the library's packages), with its own <c>[ToolboxBitmap]</c> picture (a
/// <c>.bmp</c> made transparent where it has its bottom-left pixel's color), two items of a group with one name showing
/// their namespaces; the items unchecked in Choose Toolbox Items left out; Sort Items Alphabetically, Show All (the
/// list grayed under the message without a designer) and Collapse All (kept as items come again).
/// </summary>
public sealed class ToolboxLibraryTests
{
    [AvaloniaFact]
    public void A_libraries_items_go_to_their_tabs_with_their_pictures_and_namespaces_on_a_clash()
    {
        var toolbox = new ToolboxViewModel { IsAvailable = true };
        toolbox.SetProjectItems(
        [
            new WinFormsToolboxItem("Shop.Controls.Zoom", "Zoom", "Shop", true),
            new WinFormsToolboxItem("Shop.Controls.Gauge", "Gauge", "Shop", true),
            new WinFormsToolboxItem("Lib.Layout.StackPanel", "StackPanel", "Lib.Win", true) { Tab = "Lib: Layout", Bitmap = Bmp16() },
            new WinFormsToolboxItem("Lib.Grid.GridControl", "GridControl", "Lib.Win.Grid", true) { Tab = "Lib: Data" },
            new WinFormsToolboxItem("Lib.Utils.StackPanel", "StackPanel", "Lib.Win.Utils", true) { Tab = "Lib: Layout" },
            new WinFormsToolboxItem("Lib.Data.Source", "Source", "Lib.Data", false),
            new WinFormsToolboxItem("Extras.Dial", "Dial", "Extras", true) { Assembly = @"C:\libs\Extras.dll" },
        ]);

        // Groups in the order their first items come, a tab across packages, then the framework's.
        Assert.Equal(["Shop Components", "Lib: Layout", "Lib: Data", "Lib.Data Components", "Extras Components", "Common Controls"], toolbox.Categories.Select(c => c.Title).Take(6));
        var layout = toolbox.Categories.Single(c => c.Title == "Lib: Layout").Items.Where(i => !i.IsPointer).ToList();
        Assert.Equal(["StackPanel (Lib.Layout)", "StackPanel (Lib.Utils)"], layout.Select(i => i.DisplayName));
        Assert.Equal("Gauge", toolbox.Items.Single(i => i.Type == "Shop.Controls.Gauge").DisplayName);
        Assert.Equal(@"C:\libs\Extras.dll", toolbox.Items.Single(i => i.Name == "Dial").Assembly);
        Assert.Equal(@"C:\libs\Extras.dll", toolbox.AssemblyOf("Extras.Dial"));
        Assert.Null(toolbox.AssemblyOf("Shop.Controls.Gauge"));

        // The library's picture: 16 pixels, its magenta background (the bottom-left pixel's color) transparent; the others keep their icons.
        var picture = Assert.IsType<WriteableBitmap>(layout[0].Picture);
        Assert.Equal((16, 16), (picture.PixelSize.Width, picture.PixelSize.Height));
        Assert.Equal(0, Alpha(picture, 0, 0));
        Assert.Equal(255, Alpha(picture, 8, 8));
        Assert.Same(picture, ToolboxPictures.From(Bmp16())); // decoded once
        Assert.True((layout[0].HasPicture, layout[0].HasIcon) == (true, false));
        Assert.All(toolbox.Items.Where(i => i != layout[0]), i => Assert.Null(i.Picture));

        // Their own order, then Sort Items Alphabetically (Zoom came first).
        Assert.Equal(["Zoom", "Gauge"], toolbox.Categories[0].Items.Where(i => !i.IsPointer).Select(i => i.Name));
        toolbox.SortAlphabetically = true;
        Assert.Equal(["Gauge", "Zoom"], toolbox.Categories[0].Items.Where(i => !i.IsPointer).Select(i => i.Name));

        // Unchecked in Choose Toolbox Items: left out (a group left empty goes), still offered to Choose Toolbox Items.
        toolbox.Hidden = new HashSet<string>(["Lib.Grid.GridControl", "Shop.Controls.Zoom"], StringComparer.Ordinal);
        Assert.DoesNotContain(toolbox.Categories, c => c.Title == "Lib: Data");
        Assert.DoesNotContain(toolbox.Items, i => i.Type == "Shop.Controls.Zoom");
        Assert.Contains(toolbox.ProjectItems, i => i.Type == "Lib.Grid.GridControl");

        // Collapse All, kept when the items come again.
        toolbox.CollapseAll();
        Assert.All(toolbox.Categories, c => Assert.False(c.IsExpanded));
        toolbox.SetProjectItems([.. toolbox.ProjectItems, new WinFormsToolboxItem("Shop.Controls.Meter", "Meter", "Shop", true)]);
        Assert.All(toolbox.Categories, c => Assert.False(c.IsExpanded));
        toolbox.Categories[0].IsExpanded = true;
        toolbox.Filter = "Meter";
        toolbox.Filter = string.Empty;
        Assert.True(toolbox.Categories[0].IsExpanded);
        Assert.False(toolbox.Categories[1].IsExpanded);
    }

    [AvaloniaFact]
    public void Show_all_keeps_the_list_grayed_under_the_message_without_a_designer()
    {
        var toolbox = new ToolboxViewModel();
        var view = new ToolboxView { DataContext = toolbox };
        var window = new Avalonia.Controls.Window { Content = view, Width = 300, Height = 500 };
        window.Show();
        Assert.False(toolbox.IsListVisible);
        Assert.True(toolbox.HasMessage);

        var changes = 0;
        toolbox.StateChanged += (_, _) => changes++;
        toolbox.ShowAll = true;
        Assert.Equal(1, changes);
        Assert.True((toolbox.IsListVisible, toolbox.HasListMessage) == (true, true));
        Assert.False(view.CategoryList.IsEnabled);

        toolbox.IsAvailable = true;
        Assert.True((toolbox.IsListVisible, toolbox.HasListMessage) == (true, false));
        Assert.True(view.CategoryList.IsEnabled);
        window.Close();
    }

    /// <summary>A 16-pixel 24-bit .bmp: a magenta background (the color Windows Forms makes transparent) with a blue square in the middle.</summary>
    private static string Bmp16()
    {
        const int Size = 16;
        var stride = Size * 3; // 48, a multiple of 4
        var data = new byte[54 + (stride * Size)];
        "BM"u8.CopyTo(data);
        BitConverter.GetBytes(data.Length).CopyTo(data, 2);
        BitConverter.GetBytes(54).CopyTo(data, 10);
        BitConverter.GetBytes(40).CopyTo(data, 14);
        BitConverter.GetBytes(Size).CopyTo(data, 18);
        BitConverter.GetBytes(Size).CopyTo(data, 22);
        BitConverter.GetBytes((short)1).CopyTo(data, 26);
        BitConverter.GetBytes((short)24).CopyTo(data, 28);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var at = 54 + (y * stride) + (x * 3);
                var inside = x is >= 4 and < 12 && y is >= 4 and < 12;
                (data[at], data[at + 1], data[at + 2]) = inside ? ((byte)200, (byte)110, (byte)30) : ((byte)255, (byte)0, (byte)255);
            }
        }

        return Convert.ToBase64String(data);
    }

    private static byte Alpha(WriteableBitmap bitmap, int x, int y)
    {
        using var buffer = bitmap.Lock();
        return Marshal.ReadByte(buffer.Address, (y * buffer.RowBytes) + (x * 4) + 3);
    }
}
