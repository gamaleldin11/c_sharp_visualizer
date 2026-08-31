using System;

class Program
{
    static void Main()
    {
        int[] a = { 5, 3, 8, 1, 9, 2, 7 };
        for (int i = 0; i < a.Length - 1; i++)
        {
            for (int j = 0; j < a.Length - 1 - i; j++)
            {
                if (a[j] > a[j + 1])
                {
                    int t = a[j];
                    a[j] = a[j + 1];
                    a[j + 1] = t;
                }
            }
        }
        foreach (int v in a) Console.Write(v + " ");
        Console.WriteLine();
    }
}
