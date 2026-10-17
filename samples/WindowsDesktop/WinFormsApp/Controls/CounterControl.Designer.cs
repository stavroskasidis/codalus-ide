namespace WinFormsApp.Controls;

partial class CounterControl
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

    #region Component Designer generated code

    /// <summary>Required method for Designer support - do not modify the contents of this method with the code editor.</summary>
    private void InitializeComponent()
    {
        countLabel = new Label();
        incrementButton = new Button();
        SuspendLayout();
        //
        // countLabel
        //
        countLabel.Location = new Point(3, 6);
        countLabel.Name = "countLabel";
        countLabel.Size = new Size(60, 20);
        countLabel.TabIndex = 0;
        //
        // incrementButton
        //
        incrementButton.Location = new Point(70, 3);
        incrementButton.Name = "incrementButton";
        incrementButton.Size = new Size(75, 23);
        incrementButton.TabIndex = 1;
        incrementButton.Text = "+1";
        incrementButton.UseVisualStyleBackColor = true;
        incrementButton.Click += OnIncrementClicked;
        //
        // CounterControl
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Controls.Add(countLabel);
        Controls.Add(incrementButton);
        Name = "CounterControl";
        Size = new Size(200, 32);
        ResumeLayout(false);
    }

    #endregion

    private Label countLabel;
    private Button incrementButton;
}
