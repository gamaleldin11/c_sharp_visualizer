using System;

class Program
{
    static void Main()
    {
        int x = 10;
        x += 5;  Console.WriteLine(x);
        x -= 3;  Console.WriteLine(x);
        x *= 2;  Console.WriteLine(x);
        x /= 4;  Console.WriteLine(x);
        x %= 4;  Console.WriteLine(x);
        x <<= 4; Console.WriteLine(x);
        x >>= 2; Console.WriteLine(x);
        x |= 1;  Console.WriteLine(x);
        x &= 6;  Console.WriteLine(x);
        x ^= 3;  Console.WriteLine(x);
        byte b = 200;
        b += 100;
        Console.WriteLine(b);
        string s = "a";
        s += "b";
        s += 1;
        Console.WriteLine(s);
        double d = 1;
        d /= 4;
        Console.WriteLine(d);
    }
}
