using System;
using System.Collections.Generic;

class Program
{
    static void Main()
    {
        var dict = new Dictionary<int, string>();
        dict.Add(1, "One");
        dict.Add(2, "Two");
        
        foreach (var kvp in dict)
        {
            Console.WriteLine(kvp.Key);
            Console.WriteLine(kvp.Value);
        }
    }
}
