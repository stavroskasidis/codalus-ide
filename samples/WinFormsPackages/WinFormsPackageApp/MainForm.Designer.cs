namespace WinFormsPackageApp;

partial class MainForm
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
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

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        saveButton = new Contoso.WinForms.ContosoButton();
        nameLabel = new Label();
        SuspendLayout();
        //
        // saveButton
        //
        saveButton.Location = new Point(12, 48);
        saveButton.Name = "saveButton";
        saveButton.Size = new Size(94, 29);
        saveButton.TabIndex = 1;
        saveButton.Text = "Save";
        saveButton.UseVisualStyleBackColor = true;
        //
        // nameLabel
        //
        nameLabel.AutoSize = true;
        nameLabel.Location = new Point(12, 9);
        nameLabel.Name = "nameLabel";
        nameLabel.Size = new Size(49, 20);
        nameLabel.TabIndex = 0;
        nameLabel.Text = "Name";
        //
        // MainForm
        //
        AutoScaleDimensions = new SizeF(8F, 20F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(400, 250);
        Controls.Add(saveButton);
        Controls.Add(nameLabel);
        Name = "MainForm";
        Skin = "Dark";
        Text = "Orders";
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private Contoso.WinForms.ContosoButton saveButton;
    private Label nameLabel;
}
