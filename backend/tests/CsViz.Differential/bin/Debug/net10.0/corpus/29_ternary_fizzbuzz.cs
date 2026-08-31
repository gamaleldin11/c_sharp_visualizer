using System;

partial class Program
{
    static void Main()
    {
        for (int i = 1; i <= 15; i++)
        {
            string s = i % 15 == 0 ? "FizzBuzz" : i % 3 == 0 ? "Fizz" : i % 5 == 0 ? "Buzz" : "" + i;
            Console.WriteLine(s);
        }
        Console.WriteLine(Max(Max(1, 5), Max(3, 2)));
    }
}

partial class Program
{
    static int Max(int a, int b) { return a > b ? a : b; }
}
