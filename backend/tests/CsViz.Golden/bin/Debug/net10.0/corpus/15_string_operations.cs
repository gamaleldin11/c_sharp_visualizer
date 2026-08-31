using System;

class Program
{
    static void Main()
    {
        string s = "Hello World";
        Console.WriteLine(s.Length);
        Console.WriteLine(s.Substring(0, 5));
        Console.WriteLine(s.Substring(6));
        Console.WriteLine(s.ToUpper());
        Console.WriteLine(s.ToLower());
        Console.WriteLine(s.IndexOf("World"));
        Console.WriteLine(s.Contains("lo W"));
        Console.WriteLine(s.StartsWith("Hell"));
        Console.WriteLine(s.EndsWith("rld"));
        Console.WriteLine(s[0]);
        Console.WriteLine("  pad  ".Trim());
        Console.WriteLine("a" + 1 + true);
        Console.WriteLine(s == "Hello World");
        Console.WriteLine(string.IsNullOrEmpty(""));
        foreach (char c in "abc") Console.WriteLine(c);
    }
}
