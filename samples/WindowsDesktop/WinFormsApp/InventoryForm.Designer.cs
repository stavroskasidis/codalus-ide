namespace WinFormsApp
{
    partial class InventoryForm
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(InventoryForm));
            ListViewItem listViewItem1 = new ListViewItem("Hammer");
            ListViewItem listViewItem2 = new ListViewItem("Saw");
            mainMenuStrip = new MenuStrip();
            fileToolStripMenuItem = new ToolStripMenuItem();
            newToolStripMenuItem = new ToolStripMenuItem();
            toolStripSeparator1 = new ToolStripSeparator();
            exitToolStripMenuItem = new ToolStripMenuItem();
            helpToolStripMenuItem = new ToolStripMenuItem();
            mainToolStrip = new ToolStrip();
            newToolStripButton = new ToolStripButton();
            toolStripSeparator2 = new ToolStripSeparator();
            searchToolStripLabel = new ToolStripLabel();
            mainStatusStrip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel();
            itemsContextMenuStrip = new ContextMenuStrip(components);
            copyToolStripMenuItem = new ToolStripMenuItem();
            categoryListBox = new ListBox();
            itemsListView = new ListView();
            nameColumnHeader = new ColumnHeader();
            countColumnHeader = new ColumnHeader();
            iconImageList = new ImageList(components);
            detailsTabControl = new TabControl();
            generalTabPage = new TabPage();
            nameTextBox = new TextBox();
            itemBindingSource = new BindingSource(components);
            stockTabPage = new TabPage();
            stockGridView = new DataGridView();
            locationColumn = new DataGridViewTextBoxColumn();
            quantityColumn = new DataGridViewTextBoxColumn();
            inventoryToolTip = new ToolTip(components);
            inventoryHelpProvider = new HelpProvider();
            inventoryErrorProvider = new ErrorProvider(components);
            mainMenuStrip.SuspendLayout();
            mainToolStrip.SuspendLayout();
            mainStatusStrip.SuspendLayout();
            itemsContextMenuStrip.SuspendLayout();
            detailsTabControl.SuspendLayout();
            generalTabPage.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)itemBindingSource).BeginInit();
            stockTabPage.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)stockGridView).BeginInit();
            ((System.ComponentModel.ISupportInitialize)inventoryErrorProvider).BeginInit();
            SuspendLayout();
            // 
            // mainMenuStrip
            // 
            mainMenuStrip.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, helpToolStripMenuItem });
            mainMenuStrip.Location = new Point(0, 0);
            mainMenuStrip.Name = "mainMenuStrip";
            mainMenuStrip.Size = new Size(584, 24);
            mainMenuStrip.TabIndex = 0;
            mainMenuStrip.Text = "menuStrip1";
            // 
            // fileToolStripMenuItem
            // 
            fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { newToolStripMenuItem, toolStripSeparator1, exitToolStripMenuItem });
            fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            fileToolStripMenuItem.Size = new Size(37, 20);
            fileToolStripMenuItem.Text = "&File";
            // 
            // newToolStripMenuItem
            // 
            newToolStripMenuItem.Name = "newToolStripMenuItem";
            newToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.N;
            newToolStripMenuItem.Size = new Size(141, 22);
            newToolStripMenuItem.Text = "&New";
            newToolStripMenuItem.Click += OnNewItem;
            // 
            // toolStripSeparator1
            // 
            toolStripSeparator1.Name = "toolStripSeparator1";
            toolStripSeparator1.Size = new Size(138, 6);
            // 
            // exitToolStripMenuItem
            // 
            exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            exitToolStripMenuItem.Size = new Size(141, 22);
            exitToolStripMenuItem.Text = "E&xit";
            exitToolStripMenuItem.Click += OnExit;
            // 
            // helpToolStripMenuItem
            // 
            helpToolStripMenuItem.Name = "helpToolStripMenuItem";
            helpToolStripMenuItem.Size = new Size(44, 20);
            helpToolStripMenuItem.Text = "&Help";
            // 
            // mainToolStrip
            // 
            mainToolStrip.Items.AddRange(new ToolStripItem[] { newToolStripButton, toolStripSeparator2, searchToolStripLabel });
            mainToolStrip.Location = new Point(0, 24);
            mainToolStrip.Name = "mainToolStrip";
            mainToolStrip.Size = new Size(584, 25);
            mainToolStrip.TabIndex = 1;
            mainToolStrip.Text = "toolStrip1";
            // 
            // newToolStripButton
            // 
            newToolStripButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            newToolStripButton.Image = (Image)resources.GetObject("newToolStripButton.Image");
            newToolStripButton.ImageTransparentColor = Color.Magenta;
            newToolStripButton.Name = "newToolStripButton";
            newToolStripButton.Size = new Size(23, 22);
            newToolStripButton.Text = "&New";
            newToolStripButton.Click += OnNewItem;
            // 
            // toolStripSeparator2
            // 
            toolStripSeparator2.Name = "toolStripSeparator2";
            toolStripSeparator2.Size = new Size(6, 25);
            // 
            // searchToolStripLabel
            // 
            searchToolStripLabel.Name = "searchToolStripLabel";
            searchToolStripLabel.Size = new Size(45, 22);
            searchToolStripLabel.Text = "Search:";
            // 
            // mainStatusStrip
            // 
            mainStatusStrip.Items.AddRange(new ToolStripItem[] { statusLabel });
            mainStatusStrip.Location = new Point(0, 339);
            mainStatusStrip.Name = "mainStatusStrip";
            mainStatusStrip.Size = new Size(584, 22);
            mainStatusStrip.TabIndex = 5;
            mainStatusStrip.Text = "statusStrip1";
            // 
            // statusLabel
            // 
            statusLabel.Name = "statusLabel";
            statusLabel.Size = new Size(39, 17);
            statusLabel.Text = "Ready";
            // 
            // itemsContextMenuStrip
            // 
            itemsContextMenuStrip.Items.AddRange(new ToolStripItem[] { copyToolStripMenuItem });
            itemsContextMenuStrip.Name = "itemsContextMenuStrip";
            itemsContextMenuStrip.Size = new Size(103, 26);
            // 
            // copyToolStripMenuItem
            // 
            copyToolStripMenuItem.Name = "copyToolStripMenuItem";
            copyToolStripMenuItem.Size = new Size(102, 22);
            copyToolStripMenuItem.Text = "&Copy";
            // 
            // categoryListBox
            // 
            categoryListBox.ContextMenuStrip = itemsContextMenuStrip;
            categoryListBox.FormattingEnabled = true;
            categoryListBox.Items.AddRange(new object[] { "Tools", "Garden", "Paint" });
            categoryListBox.Location = new Point(12, 56);
            categoryListBox.Name = "categoryListBox";
            categoryListBox.Size = new Size(120, 94);
            categoryListBox.TabIndex = 2;
            inventoryToolTip.SetToolTip(categoryListBox, "The item categories.");
            // 
            // itemsListView
            // 
            itemsListView.Columns.AddRange(new ColumnHeader[] { nameColumnHeader, countColumnHeader });
            itemsListView.Items.AddRange(new ListViewItem[] { listViewItem1, listViewItem2 });
            itemsListView.Location = new Point(138, 56);
            itemsListView.Name = "itemsListView";
            itemsListView.Size = new Size(434, 94);
            itemsListView.SmallImageList = iconImageList;
            itemsListView.TabIndex = 3;
            itemsListView.UseCompatibleStateImageBehavior = false;
            itemsListView.View = View.Details;
            // 
            // nameColumnHeader
            // 
            nameColumnHeader.Text = "Name";
            nameColumnHeader.Width = 200;
            // 
            // countColumnHeader
            // 
            countColumnHeader.Text = "Count";
            // 
            // iconImageList
            // 
            iconImageList.ImageSize = new Size(24, 24);
            iconImageList.TransparentColor = Color.Transparent;
            // 
            // detailsTabControl
            // 
            detailsTabControl.Controls.Add(generalTabPage);
            detailsTabControl.Controls.Add(stockTabPage);
            detailsTabControl.Location = new Point(12, 156);
            detailsTabControl.Name = "detailsTabControl";
            detailsTabControl.SelectedIndex = 0;
            detailsTabControl.Size = new Size(560, 177);
            detailsTabControl.TabIndex = 4;
            // 
            // generalTabPage
            // 
            generalTabPage.Controls.Add(nameTextBox);
            generalTabPage.Location = new Point(4, 24);
            generalTabPage.Name = "generalTabPage";
            generalTabPage.Padding = new Padding(3);
            generalTabPage.Size = new Size(552, 149);
            generalTabPage.TabIndex = 0;
            generalTabPage.Text = "General";
            generalTabPage.UseVisualStyleBackColor = true;
            // 
            // nameTextBox
            // 
            nameTextBox.DataBindings.Add(new Binding("Text", itemBindingSource, "Name", true));
            inventoryHelpProvider.SetHelpString(nameTextBox, "The item's name, as it shows on the shelf.");
            nameTextBox.Location = new Point(6, 6);
            nameTextBox.Name = "nameTextBox";
            inventoryHelpProvider.SetShowHelp(nameTextBox, true);
            nameTextBox.Size = new Size(200, 23);
            nameTextBox.TabIndex = 0;
            inventoryToolTip.SetToolTip(nameTextBox, "The item's name.");
            // 
            // itemBindingSource
            // 
            itemBindingSource.DataSource = typeof(InventoryItem);
            // 
            // stockTabPage
            // 
            stockTabPage.Controls.Add(stockGridView);
            stockTabPage.Location = new Point(4, 24);
            stockTabPage.Name = "stockTabPage";
            stockTabPage.Padding = new Padding(3);
            stockTabPage.Size = new Size(552, 149);
            stockTabPage.TabIndex = 1;
            stockTabPage.Text = "Stock";
            stockTabPage.UseVisualStyleBackColor = true;
            // 
            // stockGridView
            // 
            stockGridView.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            stockGridView.Columns.AddRange(new DataGridViewColumn[] { locationColumn, quantityColumn });
            stockGridView.Dock = DockStyle.Fill;
            stockGridView.Location = new Point(3, 3);
            stockGridView.Name = "stockGridView";
            stockGridView.Size = new Size(546, 143);
            stockGridView.TabIndex = 0;
            // 
            // locationColumn
            // 
            locationColumn.HeaderText = "Location";
            locationColumn.Name = "locationColumn";
            // 
            // quantityColumn
            // 
            quantityColumn.HeaderText = "Quantity";
            quantityColumn.Name = "quantityColumn";
            // 
            // inventoryErrorProvider
            // 
            inventoryErrorProvider.ContainerControl = this;
            // 
            // InventoryForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(584, 361);
            Controls.Add(detailsTabControl);
            Controls.Add(mainStatusStrip);
            Controls.Add(itemsListView);
            Controls.Add(categoryListBox);
            Controls.Add(mainToolStrip);
            Controls.Add(mainMenuStrip);
            MainMenuStrip = mainMenuStrip;
            Name = "InventoryForm";
            Text = "Inventory";
            mainMenuStrip.ResumeLayout(false);
            mainMenuStrip.PerformLayout();
            mainToolStrip.ResumeLayout(false);
            mainToolStrip.PerformLayout();
            mainStatusStrip.ResumeLayout(false);
            mainStatusStrip.PerformLayout();
            itemsContextMenuStrip.ResumeLayout(false);
            detailsTabControl.ResumeLayout(false);
            generalTabPage.ResumeLayout(false);
            generalTabPage.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)itemBindingSource).EndInit();
            stockTabPage.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)stockGridView).EndInit();
            ((System.ComponentModel.ISupportInitialize)inventoryErrorProvider).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private MenuStrip mainMenuStrip;
        private ToolStripMenuItem fileToolStripMenuItem;
        private ToolStripMenuItem newToolStripMenuItem;
        private ToolStripSeparator toolStripSeparator1;
        private ToolStripMenuItem exitToolStripMenuItem;
        private ToolStripMenuItem helpToolStripMenuItem;
        private ToolStrip mainToolStrip;
        private ToolStripButton newToolStripButton;
        private ToolStripSeparator toolStripSeparator2;
        private ToolStripLabel searchToolStripLabel;
        private StatusStrip mainStatusStrip;
        private ToolStripStatusLabel statusLabel;
        private ContextMenuStrip itemsContextMenuStrip;
        private ToolStripMenuItem copyToolStripMenuItem;
        private ListBox categoryListBox;
        private ListView itemsListView;
        private ColumnHeader nameColumnHeader;
        private ColumnHeader countColumnHeader;
        private ImageList iconImageList;
        private TabControl detailsTabControl;
        private TabPage generalTabPage;
        private TextBox nameTextBox;
        private BindingSource itemBindingSource;
        private TabPage stockTabPage;
        private DataGridView stockGridView;
        private DataGridViewTextBoxColumn locationColumn;
        private DataGridViewTextBoxColumn quantityColumn;
        private ToolTip inventoryToolTip;
        private HelpProvider inventoryHelpProvider;
        private ErrorProvider inventoryErrorProvider;
    }
}
