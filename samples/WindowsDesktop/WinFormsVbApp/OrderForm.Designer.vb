<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Class OrderForm
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()>
    Private Sub InitializeComponent()
        components = New System.ComponentModel.Container()
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(OrderForm))
        CustomerLabel = New Label()
        CustomerTextBox = New TextBox()
        ProductComboBox = New ComboBox()
        QuantityUpDown = New NumericUpDown()
        GiftCheckBox = New CheckBox()
        ShippingGroupBox = New GroupBox()
        ExpressRadioButton = New RadioButton()
        StandardRadioButton = New RadioButton()
        OrderDatePicker = New DateTimePicker()
        NotesTextBox = New TextBox()
        LogoPictureBox = New PictureBox()
        PlaceOrderButton = New Button()
        CancelOrderButton = New Button()
        OrderToolTip = New ToolTip(components)
        CType(QuantityUpDown, System.ComponentModel.ISupportInitialize).BeginInit()
        ShippingGroupBox.SuspendLayout()
        CType(LogoPictureBox, System.ComponentModel.ISupportInitialize).BeginInit()
        SuspendLayout()
        ' 
        ' CustomerLabel
        ' 
        CustomerLabel.AutoSize = True
        CustomerLabel.Location = New Point(12, 15)
        CustomerLabel.Name = "CustomerLabel"
        CustomerLabel.Size = New Size(62, 15)
        CustomerLabel.TabIndex = 0
        CustomerLabel.Text = "&Customer:"
        ' 
        ' CustomerTextBox
        ' 
        CustomerTextBox.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        CustomerTextBox.Location = New Point(96, 12)
        CustomerTextBox.Name = "CustomerTextBox"
        CustomerTextBox.PlaceholderText = "Name or ""account"" number"
        CustomerTextBox.Size = New Size(276, 23)
        CustomerTextBox.TabIndex = 1
        OrderToolTip.SetToolTip(CustomerTextBox, "The customer who places the order.")
        ' 
        ' ProductComboBox
        ' 
        ProductComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        ProductComboBox.FormattingEnabled = True
        ProductComboBox.Items.AddRange(New Object() {"Coffee", "Tea", "Hot chocolate"})
        ProductComboBox.Location = New Point(96, 41)
        ProductComboBox.Name = "ProductComboBox"
        ProductComboBox.Size = New Size(160, 23)
        ProductComboBox.TabIndex = 2
        ' 
        ' QuantityUpDown
        ' 
        QuantityUpDown.Location = New Point(262, 41)
        QuantityUpDown.Maximum = New Decimal(New Integer() {50, 0, 0, 0})
        QuantityUpDown.Minimum = New Decimal(New Integer() {1, 0, 0, 0})
        QuantityUpDown.Name = "QuantityUpDown"
        QuantityUpDown.Size = New Size(110, 23)
        QuantityUpDown.TabIndex = 3
        QuantityUpDown.Value = New Decimal(New Integer() {1, 0, 0, 0})
        ' 
        ' GiftCheckBox
        ' 
        GiftCheckBox.AutoSize = True
        GiftCheckBox.Checked = True
        GiftCheckBox.CheckState = CheckState.Checked
        GiftCheckBox.Location = New Point(96, 70)
        GiftCheckBox.Name = "GiftCheckBox"
        GiftCheckBox.Size = New Size(83, 19)
        GiftCheckBox.TabIndex = 4
        GiftCheckBox.Text = "Gift wrap it"
        GiftCheckBox.UseVisualStyleBackColor = True
        ' 
        ' ShippingGroupBox
        ' 
        ShippingGroupBox.Controls.Add(ExpressRadioButton)
        ShippingGroupBox.Controls.Add(StandardRadioButton)
        ShippingGroupBox.Location = New Point(12, 95)
        ShippingGroupBox.Name = "ShippingGroupBox"
        ShippingGroupBox.Size = New Size(360, 52)
        ShippingGroupBox.TabIndex = 5
        ShippingGroupBox.TabStop = False
        ShippingGroupBox.Text = "Shipping"
        ' 
        ' ExpressRadioButton
        ' 
        ExpressRadioButton.AutoSize = True
        ExpressRadioButton.Location = New Point(110, 22)
        ExpressRadioButton.Name = "ExpressRadioButton"
        ExpressRadioButton.Size = New Size(63, 19)
        ExpressRadioButton.TabIndex = 1
        ExpressRadioButton.Text = "Express"
        ExpressRadioButton.UseVisualStyleBackColor = True
        ' 
        ' StandardRadioButton
        ' 
        StandardRadioButton.AutoSize = True
        StandardRadioButton.Checked = True
        StandardRadioButton.Location = New Point(12, 22)
        StandardRadioButton.Name = "StandardRadioButton"
        StandardRadioButton.Size = New Size(72, 19)
        StandardRadioButton.TabIndex = 0
        StandardRadioButton.TabStop = True
        StandardRadioButton.Text = "Standard"
        StandardRadioButton.UseVisualStyleBackColor = True
        ' 
        ' OrderDatePicker
        ' 
        OrderDatePicker.Format = DateTimePickerFormat.Short
        OrderDatePicker.Location = New Point(96, 153)
        OrderDatePicker.Name = "OrderDatePicker"
        OrderDatePicker.Size = New Size(120, 23)
        OrderDatePicker.TabIndex = 6
        ' 
        ' NotesTextBox
        ' 
        NotesTextBox.BackColor = Color.FromArgb(CByte(255), CByte(255), CByte(224))
        NotesTextBox.Location = New Point(12, 182)
        NotesTextBox.Multiline = True
        NotesTextBox.Name = "NotesTextBox"
        NotesTextBox.Size = New Size(360, 60)
        NotesTextBox.TabIndex = 7
        NotesTextBox.Text = "Deliver to the back door." & vbCrLf & "Ring twice."
        ' 
        ' LogoPictureBox
        ' 
        LogoPictureBox.Image = CType(resources.GetObject("LogoPictureBox.Image"), Image)
        LogoPictureBox.Location = New Point(340, 153)
        LogoPictureBox.Name = "LogoPictureBox"
        LogoPictureBox.Size = New Size(32, 23)
        LogoPictureBox.SizeMode = PictureBoxSizeMode.Zoom
        LogoPictureBox.TabIndex = 8
        LogoPictureBox.TabStop = False
        ' 
        ' PlaceOrderButton
        ' 
        PlaceOrderButton.Anchor = AnchorStyles.Bottom Or AnchorStyles.Right
        PlaceOrderButton.BackColor = Color.SteelBlue
        PlaceOrderButton.Enabled = False
        PlaceOrderButton.ForeColor = Color.White
        PlaceOrderButton.Location = New Point(216, 252)
        PlaceOrderButton.Name = "PlaceOrderButton"
        PlaceOrderButton.Size = New Size(75, 23)
        PlaceOrderButton.TabIndex = 9
        PlaceOrderButton.Text = Global.WinFormsVbApp.My.Resources.Resources.PlaceOrder
        PlaceOrderButton.UseVisualStyleBackColor = False
        ' 
        ' CancelOrderButton
        ' 
        CancelOrderButton.Anchor = AnchorStyles.Bottom Or AnchorStyles.Right
        CancelOrderButton.DialogResult = DialogResult.Cancel
        CancelOrderButton.Location = New Point(297, 252)
        CancelOrderButton.Name = "CancelOrderButton"
        CancelOrderButton.Size = New Size(75, 23)
        CancelOrderButton.TabIndex = 10
        CancelOrderButton.Text = "Cancel"
        CancelOrderButton.UseVisualStyleBackColor = True
        ' 
        ' OrderForm
        ' 
        AcceptButton = PlaceOrderButton
        AutoScaleDimensions = New SizeF(7F, 15F)
        AutoScaleMode = AutoScaleMode.Font
        CancelButton = CancelOrderButton
        ClientSize = New Size(384, 287)
        Controls.Add(CancelOrderButton)
        Controls.Add(PlaceOrderButton)
        Controls.Add(LogoPictureBox)
        Controls.Add(NotesTextBox)
        Controls.Add(OrderDatePicker)
        Controls.Add(ShippingGroupBox)
        Controls.Add(GiftCheckBox)
        Controls.Add(QuantityUpDown)
        Controls.Add(ProductComboBox)
        Controls.Add(CustomerTextBox)
        Controls.Add(CustomerLabel)
        Name = "OrderForm"
        Text = "New order"
        CType(QuantityUpDown, System.ComponentModel.ISupportInitialize).EndInit()
        ShippingGroupBox.ResumeLayout(False)
        ShippingGroupBox.PerformLayout()
        CType(LogoPictureBox, System.ComponentModel.ISupportInitialize).EndInit()
        ResumeLayout(False)
        PerformLayout()
    End Sub

    Friend WithEvents CustomerLabel As Label
    Friend WithEvents CustomerTextBox As TextBox
    Friend WithEvents ProductComboBox As ComboBox
    Friend WithEvents QuantityUpDown As NumericUpDown
    Friend WithEvents GiftCheckBox As CheckBox
    Friend WithEvents ShippingGroupBox As GroupBox
    Friend WithEvents ExpressRadioButton As RadioButton
    Friend WithEvents StandardRadioButton As RadioButton
    Friend WithEvents OrderDatePicker As DateTimePicker
    Friend WithEvents NotesTextBox As TextBox
    Friend WithEvents LogoPictureBox As PictureBox
    Friend WithEvents PlaceOrderButton As Button
    Friend WithEvents CancelOrderButton As Button
    Friend WithEvents OrderToolTip As ToolTip
End Class
