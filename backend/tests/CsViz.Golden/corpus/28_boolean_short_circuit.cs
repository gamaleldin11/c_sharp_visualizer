using System;

partial class Program
{
    static void Main()
    {
        Console.WriteLine(Log("a", true) && Log("b", true));
        Console.WriteLine(Log("c", false) && Log("d", true));
        Console.WriteLine(Log("e", true) || Log("f", true));
        Console.WriteLine(Log("g", false) || Log("h", false));
        int x = 5;
        Console.WriteLine(x > 0 && x < 10);
        Console.WriteLine(x < 0 || x > 3);
        Console.WriteLine(true & false);
        Console.WriteLine(true ^ true);
    }
}

partial class Program
{
    static bool Log(string tag, bool value) { Console.WriteLine("eval " + tag); return value; }
}
