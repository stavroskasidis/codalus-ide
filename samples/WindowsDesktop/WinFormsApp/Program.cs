namespace WinFormsApp;

internal static class Program
{
    /// <summary>The application's entry point.</summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
