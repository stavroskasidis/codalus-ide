#nullable disable

using System;
using System.Windows.Forms;

namespace WinFormsApp
{
    /// <summary>
    /// A localizable form as Visual Studio for .NET Framework writes it (<c>this.</c>, full type names, its properties in
    /// <c>AboutForm.resx</c> through <c>resources.ApplyResources</c>): the Windows Forms designer's fixture (M41a).
    /// </summary>
    public partial class AboutForm : Form
    {
        public AboutForm()
        {
            InitializeComponent();
        }

        private void okButton_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}
