namespace WinFormsApp
{
    /// <summary>
    /// A form as the classic IDE's designer writes it (the Windows Forms designer's fixture, M41a): common controls, nested
    /// containers, events, an image from the form's resources and the app's user control.
    /// </summary>
    public partial class OrderForm : Form
    {
        public OrderForm()
        {
            InitializeComponent();
        }

        private void OrderForm_Load(object? sender, EventArgs e)
        {
            orderDatePicker.Value = DateTime.Today;
        }

        private void OnCustomerChanged(object? sender, EventArgs e)
        {
            placeOrderButton.Enabled = customerTextBox.TextLength > 0;
        }

        private void OnProductChanged(object? sender, EventArgs e)
        {
            historyListBox.Items.Add(productComboBox.Text);
        }

        private void OnPlaceOrderClicked(object? sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
        }
    }
}
