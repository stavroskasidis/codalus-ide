Imports CsLibrary
Imports VbLibrary

Module Program
    Sub Main(args As String())
        Dim inventory As New Inventory()
        inventory.Add(New Product("Tea", 3D))
        inventory.Add(New Product("Bread", 2.5D))

        ' A C# type from another project.
        Dim formatter As New PriceFormatter("EUR")
        For Each product In inventory.Products
            Console.WriteLine(formatter.Format(product.Name, product.Price)) ' [vb-loop]
        Next

        Console.WriteLine(inventory.ToXml().ToString())
    End Sub
End Module
