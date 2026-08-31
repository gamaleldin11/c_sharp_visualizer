using System;

class Program
{
    static void Main()
    {
        int a = 1;
        Bump(ref a);
        Console.WriteLine(a);
        Bump(ref a);
        Console.WriteLine(a);

        int b;
        Init(out b);
        Console.WriteLine(b);

        int x = 1, y = 2;
        Swap(ref x, ref y);
        Console.WriteLine(x);
        Console.WriteLine(y);

        Console.WriteLine(Id(5));
        Console.WriteLine(Id("text"));
        Console.WriteLine(First(new int[] { 7, 8, 9 }));
    }

    static void Bump(ref int n) { n = n + 1; }

    static void Init(out int n) { n = 7; }

    static void Swap(ref int p, ref int q) { int t = p; p = q; q = t; }

    static T Id<T>(T value) { return value; }

    static T First<T>(T[] items) { return items[0]; }
}
