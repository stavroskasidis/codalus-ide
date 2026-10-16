using WpfApp.ViewModels;
using Xunit;

namespace WpfApp.Tests;

public class MainViewModelTests
{
    [Fact]
    public void Add_command_adds_the_new_title()
    {
        var model = new MainViewModel { NewTitle = "Write tests" };
        Assert.True(model.AddCommand.CanExecute(null));
        model.AddCommand.Execute(null);
        Assert.Contains(model.Items, i => i.Title == "Write tests");
        Assert.Equal(string.Empty, model.NewTitle);
    }

    [Fact]
    public void Remaining_count_leaves_out_done_items() => Assert.Equal(1, new MainViewModel().RemainingCount);
}
