using System;

partial class Program
{
    static void Main()
    {
        try
        {
            Console.WriteLine("try");
            throw new Exception("boom");
        }
        catch (Exception)
        {
            Console.WriteLine("catch");
        }
        finally
        {
            Console.WriteLine("finally");
        }

        Console.WriteLine(SafeDivide(10, 2));
        Console.WriteLine(SafeDivide(10, 0));

        try
        {
            int[] a = new int[2];
            Console.WriteLine(a[5]);
        }
        catch (IndexOutOfRangeException)
        {
            Console.WriteLine("index read");
        }

        try
        {
            int[] b = new int[2];
            b[9] = 1;
        }
        catch (IndexOutOfRangeException)
        {
            Console.WriteLine("index write");
        }

        try
        {
            Node n = null;
            Console.WriteLine(n.V);
        }
        catch (NullReferenceException)
        {
            Console.WriteLine("null deref");
        }
    }
}

class Node { public int V; }

partial class Program
{
    static int SafeDivide(int a, int b)
    {
        try { return a / b; }
        catch (DivideByZeroException) { return -1; }
    }
}
