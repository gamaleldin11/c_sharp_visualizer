using System;

class Program
{
    static void Main()
    {
        int i = 5;
        Console.WriteLine(i++);
        Console.WriteLine(i);
        Console.WriteLine(++i);
        Console.WriteLine(i);
        Console.WriteLine(i--);
        Console.WriteLine(--i);
        int[] a = new int[3];
        int j = 0;
        a[j++] = 10;
        a[j++] = 20;
        Console.WriteLine(a[0]);
        Console.WriteLine(a[1]);
        Console.WriteLine(j);
        byte b = 254;
        b++;
        Console.WriteLine(b);
        b++;
        Console.WriteLine(b);
    }
}
