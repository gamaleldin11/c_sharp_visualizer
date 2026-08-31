using System;

class Program
{
    static void Main()
    {
        Counter.Increment();
        Counter.Increment();
        Counter.Increment();
        Console.WriteLine(Counter.Count);
        Counter.Reset();
        Console.WriteLine(Counter.Count);
        Console.WriteLine(Counter.Label);
    }
}

class Counter
{
    public static int Count;
    public const string Label = "counter";
    public static void Increment() { Count = Count + 1; }
    public static void Reset() { Count = 0; }
}
