Imports System.Collections.ObjectModel

''' <summary>The products on the shelves, with an XML export.</summary>
Public Class Inventory
    Private ReadOnly _products As New List(Of Product)

    Public ReadOnly Property Products As ReadOnlyCollection(Of Product)
        Get
            Return _products.AsReadOnly()
        End Get
    End Property

    Public Sub Add(product As Product)
        If product Is Nothing Then
            Throw New ArgumentNullException(NameOf(product))
        End If

        _products.Add(product)
    End Sub

    Public Function Total() As Decimal
        Dim sum = 0D
        For Each product In _products
            sum += product.Price
        Next

        Return sum
    End Function

    ''' <summary>The inventory as XML, with an XML literal.</summary>
    Public Function ToXml() As XElement
        Return <inventory count=<%= _products.Count %>>
                   <%= From p In _products Select <product name=<%= p.Name %> price=<%= p.Price %>/> %>
               </inventory>
    End Function
End Class
