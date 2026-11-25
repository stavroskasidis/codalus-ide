Friend Module Program
    ''' <summary>The application's entry point.</summary>
    <STAThread>
    Friend Sub Main()
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)
        Application.Run(New OrderForm())
    End Sub
End Module
