using System;

class Program
{
    static void Main()
    {
        int[] ints = new int[3];
        bool[] bools = new bool[2];
        double[] doubles = new double[2];
        string[] strings = new string[2];
        Console.WriteLine(ints[0]);
        Console.WriteLine(bools[0]);
        Console.WriteLine(doubles[0]);
        Console.WriteLine(strings[0] == null);
        Console.WriteLine(ints.Length);
        int[] init = { 1, 2, 3 };
        Console.WriteLine(init[0]);
        Console.WriteLine(init[2]);
    }
}
