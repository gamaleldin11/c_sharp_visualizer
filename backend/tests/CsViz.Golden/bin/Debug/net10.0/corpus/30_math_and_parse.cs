using System;

class Program
{
    static void Main()
    {
        Console.WriteLine(Math.Abs(-5));
        Console.WriteLine(Math.Max(3, 7));
        Console.WriteLine(Math.Min(3, 7));
        Console.WriteLine(Math.Sqrt(16.0));
        Console.WriteLine(Math.Pow(2.0, 10.0));
        Console.WriteLine(int.Parse("123") + 1);
        Console.WriteLine(double.Parse("1.5") * 2);
        Console.WriteLine(bool.Parse("true"));
        Console.WriteLine(char.IsDigit('7'));
        Console.WriteLine(char.IsLetter('7'));
    }
}
