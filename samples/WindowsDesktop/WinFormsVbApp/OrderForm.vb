''' <summary>
''' A Visual Basic form as the classic IDE's designer writes it for .NET (the Windows Forms designer's fixture): common
''' controls, a group box, an image from the form's resources, a string from the project's resources, and events hooked
''' by Handles clauses.
''' </summary>
Public Class OrderForm
    Private Sub OrderForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        OrderDatePicker.Value = Date.Today
    End Sub

    Private Sub CustomerTextBox_TextChanged(sender As Object, e As EventArgs) Handles CustomerTextBox.TextChanged
        PlaceOrderButton.Enabled = CustomerTextBox.TextLength > 0
    End Sub

    Private Sub OnPlaceOrder(sender As Object, e As EventArgs) Handles PlaceOrderButton.Click, CancelOrderButton.Click
        DialogResult = If(sender Is PlaceOrderButton, DialogResult.OK, DialogResult.Cancel)
    End Sub
End Class
