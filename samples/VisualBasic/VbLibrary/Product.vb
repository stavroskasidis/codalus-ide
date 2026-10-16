''' <summary>Something the shop sells.</summary>
Public Class Product
    Public Sub New(name As String, price As Decimal)
        Me.Name = name
        Me.Price = price
    End Sub

    ''' <summary>What it's called.</summary>
    Public ReadOnly Property Name As String

    ''' <summary>Its price, in the shop's currency.</summary>
    Public Property Price As Decimal
End Class
