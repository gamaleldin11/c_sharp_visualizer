using System;

partial class Program
{
    static void Main()
    {
        for (int i = 0; i < 10; i++) Console.WriteLine(Fib(i));
        Console.WriteLine(Fact(10));
    }
}

partial class Program
{
    static int Fib(int n) { if (n < 2) return n; return Fib(n - 1) + Fib(n - 2); }
    static long Fact(int n) { if (n <= 1) return 1L; return n * Fact(n - 1); }
}
