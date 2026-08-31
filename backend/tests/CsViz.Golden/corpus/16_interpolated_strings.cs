using System;

class Program
{
    static void Main()
    {
        int x = 42;
        string name = "world";
        Console.WriteLine($"hello {name}");
        Console.WriteLine($"x is {x} and double is {x * 2}");
        Console.WriteLine($"{x}{name}");
        Console.WriteLine($"nested {(x > 10 ? "big" : "small")}");
        bool flag = true;
        Console.WriteLine($"flag={flag}");
        double d = 1.5;
        Console.WriteLine($"d={d}");
    }
}
