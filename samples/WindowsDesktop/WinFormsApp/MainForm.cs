using WinFormsApp.Properties;

namespace WinFormsApp;

/// <summary>The main form: a greeting from the resources and the counter control.</summary>
public partial class MainForm : Form
{
    public MainForm()
    {
        InitializeComponent();
        greetingLabel.Text = Resources.Greeting;
    }

    private void OnResetClicked(object? sender, EventArgs e) => counterControl.Reset();
}
