using System;
using System.Windows.Forms;

namespace WinFormsApp
{
    public partial class MainForm : Form
    {
        private int _count;

        public MainForm()
        {
            InitializeComponent();
        }

        private void countButton_Click(object sender, EventArgs e)
        {
            _count++;
            this.countLabel.Text = "Clicked " + _count + " time(s)";
        }
    }
}
