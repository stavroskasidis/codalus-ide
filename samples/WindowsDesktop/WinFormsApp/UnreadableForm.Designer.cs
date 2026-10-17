namespace WinFormsApp
{
    partial class UnreadableForm
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
            closeButton = new Button();
            SuspendLayout();
            // 
            // closeButton
            // 
            closeButton.Location = new Point(12, 12);
            closeButton.Name = "closeButton";
            closeButton.Size = new Size(75, 23);
            closeButton.TabIndex = 0;
            closeButton.Text = "Close";
            closeButton.UseVisualStyleBackColor = true;
#pragma warning disable WFO3010 // A lambda the designer can't read: the fixture of a designer file opened as code.
            closeButton.Click += (sender, e) => Close();
#pragma warning restore WFO3010
            // 
            // UnreadableForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(200, 60);
            Controls.Add(closeButton);
            Name = "UnreadableForm";
            Text = "Unreadable";
            ResumeLayout(false);
        }

        #endregion

        private Button closeButton;
    }
}
