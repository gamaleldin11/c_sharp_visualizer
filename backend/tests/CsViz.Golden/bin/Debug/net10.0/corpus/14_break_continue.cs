using System;

class Program
{
    static void Main()
    {
        for (int i = 0; i < 10; i++)
        {
            if (i == 3) continue;
            if (i == 6) break;
            Console.WriteLine(i);
        }
        int n = 0;
        while (true)
        {
            n++;
            if (n > 3) break;
            Console.WriteLine("w" + n);
        }
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (j == 1) break;
                Console.WriteLine(i + "," + j);
            }
        }
        int k = 0;
        do
        {
            k++;
            if (k == 2) continue;
            Console.WriteLine("d" + k);
        } while (k < 4);
    }
}
