using System;

class Program
{
    static void Main()
    {
        string a = null;
        string b = a ?? "fallback";
        Console.WriteLine(b);
        string c = "set";
        Console.WriteLine(c ?? "unused");
        Console.WriteLine(a == null);
        Console.WriteLine(c != null);
        Node n = null;
        Console.WriteLine(n == null);
        n = new Node();
        Console.WriteLine(n == null);
    }
}

class Node { public int V; }
