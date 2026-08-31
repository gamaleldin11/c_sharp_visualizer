using System;

class Program
{
    static void Main()
    {
        for (int i = 0; i < 2; i++) { int v = i * 10; Console.WriteLine(v); }
        for (int i = 0; i < 2; i++) { int v = i * 100; Console.WriteLine(v); }
        {
            int inner = 1;
            Console.WriteLine(inner);
        }
        {
            int inner = 2;
            Console.WriteLine(inner);
        }
        int n = 0;
        while (n < 3) { int loopLocal = n * 2; Console.WriteLine(loopLocal); n++; }
    }
}
