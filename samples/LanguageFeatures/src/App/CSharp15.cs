namespace App;

// C# 15 (.NET 11, M30b): the editor shows these without false errors.

/// <summary>A union type: a pet is a cat or a dog.</summary>
public union Pet(Cat, Dog);

public record Cat(string Name);

public record Dog(string Name, bool Good);

public static class CSharp15
{
    /// <summary>A switch over a union is exhaustive without a discard.</summary>
    public static string Describe(Pet pet) => pet switch
    {
        Cat cat => $"{cat.Name} the cat",
        Dog dog => $"{dog.Name} the {(dog.Good ? "good " : "")}dog",
    };

    /// <summary>Collection expression arguments: the list's capacity and the set's comparer.</summary>
    public static int Distinct(IEnumerable<string> names)
    {
        List<string> list = [with(capacity: 8), .. names];
        HashSet<string> set = [with(StringComparer.OrdinalIgnoreCase), .. list];
        return set.Count;
    }

    /// <summary>Labeled <c>break</c> and <c>continue</c> leave an outer loop.</summary>
    public static (int Row, int Column)? Find(int[][] grid, int value)
    {
        (int, int)? found = null;
        rows:
        for (var row = 0; row < grid.Length; row++)
        {
            for (var column = 0; column < grid[row].Length; column++)
            {
                if (grid[row][column] < 0)
                {
                    continue rows;
                }

                if (grid[row][column] == value)
                {
                    found = (row, column);
                    break rows;
                }
            }
        }

        return found;
    }
}
