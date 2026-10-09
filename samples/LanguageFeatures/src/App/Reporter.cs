internal static class Reporter
{
    public static void Run()
    {
        try
        {
            Console.WriteLine(int.Parse("42", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (FormatException ex)
        {
            // Rethrowing the caught exception resets its stack trace: CA2200 (a warning by default).
            throw ex;
        }
    }
}
