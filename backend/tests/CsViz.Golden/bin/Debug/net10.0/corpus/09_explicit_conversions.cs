using System;

class Program
{
    static void Main()
    {
        double d = 3.9;
        Console.WriteLine((int)d);
        Console.WriteLine((int)-3.9);
        Console.WriteLine((int)3.5);
        float f = 1.5f;
        Console.WriteLine((int)f);
        int i = 300;
        Console.WriteLine((byte)i);
        Console.WriteLine(unchecked((sbyte)200));
        Console.WriteLine(unchecked((short)70000));
        Console.WriteLine((double)7 / 2);
        Console.WriteLine((long)3.99);
    }
}
