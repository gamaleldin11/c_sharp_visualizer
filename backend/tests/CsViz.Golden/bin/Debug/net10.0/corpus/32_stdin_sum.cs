using System;

class Program
{
    static void Main()
    {
        string line;
        int total = 0;
        while ((line = Console.ReadLine()) != null)
        {
            Console.WriteLine("read: " + line);
            total += int.Parse(line);
        }
        Console.WriteLine("total " + total);
    }
}
