namespace WinFormsApp
{
    /// <summary>
    /// A form as Visual Studio's designer writes it (the Windows Forms designer's fixture, M41b): a menu, a tool strip and
    /// a status strip with their items, a context menu, collections (a list box's items, a list view's columns and items, a
    /// tab control's pages, a grid's columns), extender properties (a tool tip, a help provider), a data binding to a
    /// binding source, and components in the tray.
    /// </summary>
    public partial class InventoryForm : Form
    {
        public InventoryForm()
        {
            InitializeComponent();
        }

        private void OnNewItem(object? sender, EventArgs e)
        {
            itemBindingSource.AddNew();
        }

        private void OnExit(object? sender, EventArgs e)
        {
            Close();
        }
    }
}
