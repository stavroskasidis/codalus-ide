Imports VbLibrary
Imports Xunit

Namespace VbTests
    Public Class InventoryTests
        <Fact>
        Sub Total_adds_the_prices()
            Dim inventory As New Inventory()
            inventory.Add(New Product("Tea", 3D))
            inventory.Add(New Product("Bread", 2.5D))
            Assert.Equal(5.5D, inventory.Total())
        End Sub

        <Fact>
        Sub ToXml_lists_every_product()
            Dim inventory As New Inventory()
            inventory.Add(New Product("Tea", 3D))
            Assert.Equal("1", inventory.ToXml().@count)
        End Sub
    End Class
End Namespace
