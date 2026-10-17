namespace WinFormsApp
{
    partial class MainForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.countButton = new System.Windows.Forms.Button();
            this.countLabel = new System.Windows.Forms.Label();
            this.optionsGroupBox = new System.Windows.Forms.GroupBox();
            this.loudCheckBox = new System.Windows.Forms.CheckBox();
            this.optionsGroupBox.SuspendLayout();
            this.SuspendLayout();
            //
            // countButton
            //
            this.countButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.countButton.Location = new System.Drawing.Point(197, 12);
            this.countButton.Name = "countButton";
            this.countButton.Size = new System.Drawing.Size(75, 23);
            this.countButton.TabIndex = 0;
            this.countButton.Text = "Count";
            this.countButton.UseVisualStyleBackColor = true;
            this.countButton.Click += new System.EventHandler(this.countButton_Click);
            //
            // countLabel
            //
            this.countLabel.AutoSize = true;
            this.countLabel.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.countLabel.Location = new System.Drawing.Point(12, 17);
            this.countLabel.Name = "countLabel";
            this.countLabel.Size = new System.Drawing.Size(109, 16);
            this.countLabel.TabIndex = 1;
            this.countLabel.Text = "Not clicked yet";
            //
            // optionsGroupBox
            //
            this.optionsGroupBox.Controls.Add(this.loudCheckBox);
            this.optionsGroupBox.Location = new System.Drawing.Point(12, 48);
            this.optionsGroupBox.Name = "optionsGroupBox";
            this.optionsGroupBox.Size = new System.Drawing.Size(260, 56);
            this.optionsGroupBox.TabIndex = 2;
            this.optionsGroupBox.TabStop = false;
            this.optionsGroupBox.Text = "Options";
            //
            // loudCheckBox
            //
            this.loudCheckBox.AutoSize = true;
            this.loudCheckBox.Location = new System.Drawing.Point(6, 24);
            this.loudCheckBox.Name = "loudCheckBox";
            this.loudCheckBox.Size = new System.Drawing.Size(50, 17);
            this.loudCheckBox.TabIndex = 0;
            this.loudCheckBox.Text = "Loud";
            this.loudCheckBox.UseVisualStyleBackColor = true;
            //
            // MainForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(284, 116);
            this.Controls.Add(this.optionsGroupBox);
            this.Controls.Add(this.countLabel);
            this.Controls.Add(this.countButton);
            this.Name = "MainForm";
            this.Text = "WinForms on .NET Framework";
            this.optionsGroupBox.ResumeLayout(false);
            this.optionsGroupBox.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button countButton;
        private System.Windows.Forms.Label countLabel;
        private System.Windows.Forms.GroupBox optionsGroupBox;
        private System.Windows.Forms.CheckBox loudCheckBox;
    }
}
