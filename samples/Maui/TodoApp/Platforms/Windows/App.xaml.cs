namespace TodoApp.WinUI;

/// <summary>The Windows target's application (WinUI), which starts the MAUI app.</summary>
public partial class App : MauiWinUIApplication
{
	public App()
	{
		this.InitializeComponent();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
