' Hot Reload while debugging (M55) in Visual Basic: the tests edit Greeter.Message while the program runs.
Module Program
    Sub Main()
        For i = 0 To 1999
            Console.WriteLine(Greeter.Message(i)) ' [vb-call]
            Threading.Thread.Sleep(50)
        Next
    End Sub
End Module

Module Greeter
    Function Message(i As Integer) As String
        Dim text = "hello " & i ' [vb-message]
        Return text
    End Function
End Module
