namespace WinFormsApp
{
    partial class OrderForm
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
            components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(OrderForm));
            customerLabel = new Label();
            customerTextBox = new TextBox();
            productComboBox = new ComboBox();
            quantityUpDown = new NumericUpDown();
            giftCheckBox = new CheckBox();
            shippingGroupBox = new GroupBox();
            expressRadioButton = new RadioButton();
            standardRadioButton = new RadioButton();
            detailsTable = new TableLayoutPanel();
            dateLabel = new Label();
            orderDatePicker = new DateTimePicker();
            notesSplit = new SplitContainer();
            historyListBox = new ListBox();
            notesTextBox = new TextBox();
            logoPictureBox = new PictureBox();
            buttonPanel = new Panel();
            cancelButton = new Button();
            placeOrderButton = new Button();
            counterControl = new Controls.CounterControl();
            orderToolTip = new ToolTip(components);
            ((System.ComponentModel.ISupportInitialize)quantityUpDown).BeginInit();
            shippingGroupBox.SuspendLayout();
            detailsTable.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)notesSplit).BeginInit();
            notesSplit.Panel1.SuspendLayout();
            notesSplit.Panel2.SuspendLayout();
            notesSplit.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)logoPictureBox).BeginInit();
            buttonPanel.SuspendLayout();
            SuspendLayout();
            // 
            // customerLabel
            // 
            customerLabel.AutoSize = true;
            customerLabel.Location = new Point(12, 15);
            customerLabel.Name = "customerLabel";
            customerLabel.Size = new Size(62, 15);
            customerLabel.TabIndex = 0;
            customerLabel.Text = "&Customer:";
            // 
            // customerTextBox
            // 
            customerTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            customerTextBox.Location = new Point(96, 12);
            customerTextBox.Name = "customerTextBox";
            customerTextBox.PlaceholderText = "Name or account number";
            customerTextBox.Size = new Size(276, 23);
            customerTextBox.TabIndex = 1;
            orderToolTip.SetToolTip(customerTextBox, "The customer who places the order.");
            customerTextBox.TextChanged += OnCustomerChanged;
            // 
            // productComboBox
            // 
            productComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            productComboBox.FormattingEnabled = true;
            productComboBox.Items.AddRange(new object[] { "Coffee", "Tea", "Hot chocolate" });
            productComboBox.Location = new Point(96, 41);
            productComboBox.Name = "productComboBox";
            productComboBox.Size = new Size(160, 23);
            productComboBox.TabIndex = 2;
            productComboBox.SelectedIndexChanged += OnProductChanged;
            // 
            // quantityUpDown
            // 
            quantityUpDown.Location = new Point(262, 41);
            quantityUpDown.Maximum = new decimal(new int[] { 50, 0, 0, 0 });
            quantityUpDown.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            quantityUpDown.Name = "quantityUpDown";
            quantityUpDown.Size = new Size(110, 23);
            quantityUpDown.TabIndex = 3;
            quantityUpDown.Value = new decimal(new int[] { 1, 0, 0, 0 });
            // 
            // giftCheckBox
            // 
            giftCheckBox.AutoSize = true;
            giftCheckBox.Checked = true;
            giftCheckBox.CheckState = CheckState.Checked;
            giftCheckBox.Location = new Point(96, 70);
            giftCheckBox.Name = "giftCheckBox";
            giftCheckBox.Size = new Size(83, 19);
            giftCheckBox.TabIndex = 4;
            giftCheckBox.Text = "Gift wrap it";
            giftCheckBox.UseVisualStyleBackColor = true;
            // 
            // shippingGroupBox
            // 
            shippingGroupBox.Controls.Add(expressRadioButton);
            shippingGroupBox.Controls.Add(standardRadioButton);
            shippingGroupBox.Location = new Point(12, 95);
            shippingGroupBox.Name = "shippingGroupBox";
            shippingGroupBox.Padding = new Padding(6);
            shippingGroupBox.Size = new Size(360, 52);
            shippingGroupBox.TabIndex = 5;
            shippingGroupBox.TabStop = false;
            shippingGroupBox.Text = "Shipping";
            // 
            // expressRadioButton
            // 
            expressRadioButton.AutoSize = true;
            expressRadioButton.Location = new Point(110, 22);
            expressRadioButton.Name = "expressRadioButton";
            expressRadioButton.Size = new Size(63, 19);
            expressRadioButton.TabIndex = 1;
            expressRadioButton.Text = "Express";
            expressRadioButton.UseVisualStyleBackColor = true;
            // 
            // standardRadioButton
            // 
            standardRadioButton.AutoSize = true;
            standardRadioButton.Checked = true;
            standardRadioButton.Location = new Point(12, 22);
            standardRadioButton.Name = "standardRadioButton";
            standardRadioButton.Size = new Size(72, 19);
            standardRadioButton.TabIndex = 0;
            standardRadioButton.TabStop = true;
            standardRadioButton.Text = "Standard";
            standardRadioButton.UseVisualStyleBackColor = true;
            // 
            // detailsTable
            // 
            detailsTable.ColumnCount = 2;
            detailsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84F));
            detailsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            detailsTable.Controls.Add(dateLabel, 0, 0);
            detailsTable.Controls.Add(orderDatePicker, 1, 0);
            detailsTable.Location = new Point(12, 153);
            detailsTable.Name = "detailsTable";
            detailsTable.RowCount = 1;
            detailsTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            detailsTable.Size = new Size(360, 30);
            detailsTable.TabIndex = 6;
            // 
            // dateLabel
            // 
            dateLabel.Anchor = AnchorStyles.Left;
            dateLabel.AutoSize = true;
            dateLabel.Location = new Point(3, 7);
            dateLabel.Name = "dateLabel";
            dateLabel.Size = new Size(34, 15);
            dateLabel.TabIndex = 0;
            dateLabel.Text = "Date:";
            // 
            // orderDatePicker
            // 
            orderDatePicker.Dock = DockStyle.Fill;
            orderDatePicker.Format = DateTimePickerFormat.Short;
            orderDatePicker.Location = new Point(87, 3);
            orderDatePicker.Name = "orderDatePicker";
            orderDatePicker.Size = new Size(270, 23);
            orderDatePicker.TabIndex = 1;
            // 
            // notesSplit
            // 
            notesSplit.BorderStyle = BorderStyle.FixedSingle;
            notesSplit.Location = new Point(12, 189);
            notesSplit.Name = "notesSplit";
            // 
            // notesSplit.Panel1
            // 
            notesSplit.Panel1.Controls.Add(historyListBox);
            // 
            // notesSplit.Panel2
            // 
            notesSplit.Panel2.Controls.Add(notesTextBox);
            notesSplit.Size = new Size(360, 100);
            notesSplit.SplitterDistance = 120;
            notesSplit.TabIndex = 7;
            // 
            // historyListBox
            // 
            historyListBox.Dock = DockStyle.Fill;
            historyListBox.FormattingEnabled = true;
            historyListBox.ItemHeight = 15;
            historyListBox.Location = new Point(0, 0);
            historyListBox.Name = "historyListBox";
            historyListBox.Size = new Size(118, 98);
            historyListBox.TabIndex = 0;
            // 
            // notesTextBox
            // 
            notesTextBox.BackColor = Color.FromArgb(255, 255, 224);
            notesTextBox.Dock = DockStyle.Fill;
            notesTextBox.Font = new Font("Segoe UI", 9F, FontStyle.Italic, GraphicsUnit.Point, 0);
            notesTextBox.ForeColor = SystemColors.GrayText;
            notesTextBox.Location = new Point(0, 0);
            notesTextBox.Multiline = true;
            notesTextBox.Name = "notesTextBox";
            notesTextBox.ScrollBars = ScrollBars.Vertical;
            notesTextBox.Size = new Size(234, 98);
            notesTextBox.TabIndex = 0;
            notesTextBox.Text = "Notes for the kitchen\r\n(one per line)";
            // 
            // logoPictureBox
            // 
            logoPictureBox.Image = (Image)resources.GetObject("logoPictureBox.Image");
            logoPictureBox.Location = new Point(12, 299);
            logoPictureBox.Name = "logoPictureBox";
            logoPictureBox.Size = new Size(16, 16);
            logoPictureBox.SizeMode = PictureBoxSizeMode.AutoSize;
            logoPictureBox.TabIndex = 8;
            logoPictureBox.TabStop = false;
            // 
            // buttonPanel
            // 
            buttonPanel.Controls.Add(cancelButton);
            buttonPanel.Controls.Add(placeOrderButton);
            buttonPanel.Dock = DockStyle.Bottom;
            buttonPanel.Location = new Point(0, 361);
            buttonPanel.Name = "buttonPanel";
            buttonPanel.Size = new Size(384, 40);
            buttonPanel.TabIndex = 10;
            // 
            // cancelButton
            // 
            cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.Location = new Point(297, 8);
            cancelButton.Name = "cancelButton";
            cancelButton.Size = new Size(75, 23);
            cancelButton.TabIndex = 1;
            cancelButton.Text = "Cancel";
            cancelButton.UseVisualStyleBackColor = true;
            // 
            // placeOrderButton
            // 
            placeOrderButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            placeOrderButton.BackColor = Color.SteelBlue;
            placeOrderButton.FlatAppearance.BorderSize = 0;
            placeOrderButton.FlatStyle = FlatStyle.Flat;
            placeOrderButton.ForeColor = Color.White;
            placeOrderButton.Location = new Point(196, 8);
            placeOrderButton.Name = "placeOrderButton";
            placeOrderButton.Size = new Size(95, 23);
            placeOrderButton.TabIndex = 0;
            placeOrderButton.Text = "&Place order";
            placeOrderButton.UseVisualStyleBackColor = false;
            placeOrderButton.Click += OnPlaceOrderClicked;
            // 
            // counterControl
            // 
            counterControl.Location = new Point(172, 295);
            counterControl.Name = "counterControl";
            counterControl.Size = new Size(200, 32);
            counterControl.TabIndex = 9;
            // 
            // OrderForm
            // 
            AcceptButton = placeOrderButton;
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            CancelButton = cancelButton;
            ClientSize = new Size(384, 401);
            Controls.Add(counterControl);
            Controls.Add(buttonPanel);
            Controls.Add(logoPictureBox);
            Controls.Add(notesSplit);
            Controls.Add(detailsTable);
            Controls.Add(shippingGroupBox);
            Controls.Add(giftCheckBox);
            Controls.Add(quantityUpDown);
            Controls.Add(productComboBox);
            Controls.Add(customerTextBox);
            Controls.Add(customerLabel);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "OrderForm";
            StartPosition = FormStartPosition.CenterParent;
            Text = "New order";
            Load += OrderForm_Load;
            ((System.ComponentModel.ISupportInitialize)quantityUpDown).EndInit();
            shippingGroupBox.ResumeLayout(false);
            shippingGroupBox.PerformLayout();
            detailsTable.ResumeLayout(false);
            detailsTable.PerformLayout();
            notesSplit.Panel1.ResumeLayout(false);
            notesSplit.Panel2.ResumeLayout(false);
            notesSplit.Panel2.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)notesSplit).EndInit();
            notesSplit.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)logoPictureBox).EndInit();
            buttonPanel.ResumeLayout(false);
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label customerLabel;
        private TextBox customerTextBox;
        private ComboBox productComboBox;
        private NumericUpDown quantityUpDown;
        private CheckBox giftCheckBox;
        private GroupBox shippingGroupBox;
        private RadioButton expressRadioButton;
        private RadioButton standardRadioButton;
        private TableLayoutPanel detailsTable;
        private Label dateLabel;
        private DateTimePicker orderDatePicker;
        private SplitContainer notesSplit;
        private ListBox historyListBox;
        private TextBox notesTextBox;
        private PictureBox logoPictureBox;
        private Panel buttonPanel;
        private Button cancelButton;
        private Button placeOrderButton;
        private Controls.CounterControl counterControl;
        private ToolTip orderToolTip;
    }
}
