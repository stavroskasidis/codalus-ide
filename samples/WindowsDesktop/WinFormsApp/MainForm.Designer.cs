namespace WinFormsApp;

partial class MainForm
{
    /// <summary>Required designer variable.</summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>Clean up any resources being used.</summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }

        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>Required method for Designer support - do not modify the contents of this method with the code editor.</summary>
    private void InitializeComponent()
    {
        greetingLabel = new Label();
        counterControl = new Controls.CounterControl();
        resetButton = new Button();
        SuspendLayout();
        //
        // greetingLabel
        //
        greetingLabel.AutoSize = true;
        greetingLabel.Location = new Point(12, 9);
        greetingLabel.Name = "greetingLabel";
        greetingLabel.Size = new Size(0, 15);
        greetingLabel.TabIndex = 0;
        //
        // counterControl
        //
        counterControl.Location = new Point(12, 36);
        counterControl.Name = "counterControl";
        counterControl.Size = new Size(200, 32);
        counterControl.TabIndex = 1;
        //
        // resetButton
        //
        resetButton.Location = new Point(12, 80);
        resetButton.Name = "resetButton";
        resetButton.Size = new Size(75, 23);
        resetButton.TabIndex = 2;
        resetButton.Text = "Reset";
        resetButton.UseVisualStyleBackColor = true;
        resetButton.Click += OnResetClicked;
        //
        // MainForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(320, 120);
        Controls.Add(greetingLabel);
        Controls.Add(counterControl);
        Controls.Add(resetButton);
        Name = "MainForm";
        Text = "Counter";
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private Label greetingLabel;
    private Controls.CounterControl counterControl;
    private Button resetButton;
}
