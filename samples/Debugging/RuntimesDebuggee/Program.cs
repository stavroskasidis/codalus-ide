using System;
using System.Collections.Generic;

namespace RuntimesDebuggee
{
    public sealed class Person
    {
        public Person(string name, int age)
        {
            Name = name;
            Age = age;
        }

        public string Name { get; }

        public int Age { get; }
    }

    public static class Program
    {
        public static int Main()
        {
            Console.WriteLine("runtime=" + Environment.Version.Major + "." + Environment.Version.Minor);
            var numbers = new List<int> { 1, 2, 3, 4, 5 };
            var total = 0;
            for (var i = 0; i < numbers.Count; i++)
            {
                total += numbers[i]; // [loop]
            }

            var person = new Person("Ada", 36);
            Console.WriteLine("total=" + total + " " + person.Name + " is " + person.Age); // [print]
            return total;
        }
    }
}
