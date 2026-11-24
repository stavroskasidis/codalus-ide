Namespace Controls
    <Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
    Partial Class BadgeControl
        Inherits System.Windows.Forms.UserControl

        'UserControl overrides dispose to clean up the component list.
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
            CountLabel = New Label()
            SuspendLayout()
            ' 
            ' CountLabel
            ' 
            CountLabel.Dock = DockStyle.Fill
            CountLabel.Location = New Point(0, 0)
            CountLabel.Name = "CountLabel"
            CountLabel.Size = New Size(48, 24)
            CountLabel.TabIndex = 0
            CountLabel.Text = "0"
            CountLabel.TextAlign = ContentAlignment.MiddleCenter
            ' 
            ' BadgeControl
            ' 
            AutoScaleDimensions = New SizeF(7F, 15F)
            AutoScaleMode = AutoScaleMode.Font
            Controls.Add(CountLabel)
            Name = "BadgeControl"
            Size = New Size(48, 24)
            ResumeLayout(False)
        End Sub

        Friend WithEvents CountLabel As Label
    End Class
End Namespace
