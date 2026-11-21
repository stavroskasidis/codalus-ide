namespace WinFormsVendorApp;

partial class BoardForm
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
        board1 = new Contoso.WinForms.Board.ContosoBoard();
        boardColumn1 = new Contoso.WinForms.Board.ContosoBoardColumn();
        card1 = new Contoso.WinForms.Board.ContosoCard();
        brokenPanel1 = new Contoso.WinForms.Board.ContosoBrokenPanel();
        board1.SuspendLayout();
        SuspendLayout();
        //
        // board1
        //
        board1.Columns.AddRange(new Contoso.WinForms.Board.ContosoBoardColumn[] { boardColumn1 });
        board1.Controls.Add(card1);
        board1.Location = new Point(12, 12);
        board1.Name = "board1";
        board1.Size = new Size(300, 200);
        board1.TabIndex = 0;
        board1.Title = "Sprint";
        //
        // boardColumn1
        //
        boardColumn1.Caption = "Backlog";
        //
        // card1
        //
        card1.AutoSize = true;
        card1.Location = new Point(10, 10);
        card1.Name = "card1";
        card1.Size = new Size(40, 20);
        card1.TabIndex = 0;
        card1.Text = "Card";
        //
        // brokenPanel1
        //
        brokenPanel1.Location = new Point(330, 12);
        brokenPanel1.Name = "brokenPanel1";
        brokenPanel1.Size = new Size(150, 100);
        brokenPanel1.TabIndex = 1;
        //
        // BoardForm
        //
        AutoScaleDimensions = new SizeF(8F, 20F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(500, 250);
        Controls.Add(brokenPanel1);
        Controls.Add(board1);
        Name = "BoardForm";
        Text = "Board";
        board1.ResumeLayout(false);
        board1.PerformLayout();
        ResumeLayout(false);
    }

    #endregion

    private Contoso.WinForms.Board.ContosoBoard board1;
    private Contoso.WinForms.Board.ContosoBoardColumn boardColumn1;
    private Contoso.WinForms.Board.ContosoCard card1;
    private Contoso.WinForms.Board.ContosoBrokenPanel brokenPanel1;
}
