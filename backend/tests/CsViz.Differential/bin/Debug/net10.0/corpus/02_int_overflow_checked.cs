using System;

class Program
{
    static void Main()
    {
        checked
        {
            try
            {
                int max = 2147483647;
                int overflow = max + 1;
                Console.WriteLine(overflow);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Overflow caught");
            }
        }
    }
}
