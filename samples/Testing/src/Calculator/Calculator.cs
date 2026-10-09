namespace Calculator;

/// <summary>The code under test. The tests cover Add fully, Divide partly (not the zero branch) and Unused not at all.</summary>
public static class Arithmetic
{
    public static int Add(int a, int b) => a + b;

    public static int Divide(int a, int b)
    {
        if (b == 0)
        {
            throw new DivideByZeroException();
        }

        return a / b;
    }

    public static int Unused(int a)
    {
        return a * 3;
    }
}
