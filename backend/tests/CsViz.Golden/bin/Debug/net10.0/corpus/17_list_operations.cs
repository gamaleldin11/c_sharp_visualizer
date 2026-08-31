using System;
using System.Collections.Generic;

class Program
{
    static void Main()
    {
        var list = new List<int>();
        for (int i = 0; i < 6; i++) list.Add(i * i);
        Console.WriteLine(list.Count);
        Console.WriteLine(list[3]);
        list[3] = 99;
        Console.WriteLine(list[3]);
        foreach (int v in list) Console.WriteLine(v);
        list.Clear();
        Console.WriteLine(list.Count);
    }
}
