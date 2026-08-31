using System;

class Program
{
    static void Main()
    {
        byte a = 200, b = 100;
        Console.WriteLine(a + b);
        short s = 30000;
        Console.WriteLine(s + s);
        char c = 'A';
        Console.WriteLine(c + 1);
        Console.WriteLine((char)(c + 1));
        long big = 2147483647L + 1L;
        Console.WriteLine(big);
        Console.WriteLine(7 / 2);
        Console.WriteLine(7 % 2);
        Console.WriteLine(7 / 2.0);
        Console.WriteLine(-7 / 2);
        Console.WriteLine(-7 % 2);
    }
}
