using System;

class Program
{
    static void Main()
    {
        int div = (0 - 7) / 2;
        int rem = (0 - 7) % 3;
        double inf = 1.0 / 0.0;
        
        try
        {
            int zero = 0;
            int err = 1 / zero;
        }
        catch (Exception ex)
        {
            Console.WriteLine("DivByZero caught");
        }
    }
}
