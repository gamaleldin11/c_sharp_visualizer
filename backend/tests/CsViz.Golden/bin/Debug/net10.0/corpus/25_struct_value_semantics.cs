using System;

partial class Program
{
    static void Main()
    {
        Point p1 = new Point(1, 2);
        Point p2 = p1;
        p2.X = 99;
        Console.WriteLine(p1.X);
        Console.WriteLine(p2.X);
        Boxed b1 = new Boxed(1);
        Boxed b2 = b1;
        b2.X = 99;
        Console.WriteLine(b1.X);
        Console.WriteLine(b2.X);
        Point p3 = new Point(0, 0);
        Mutate(p3);
        Console.WriteLine(p3.X);
    }
}

partial class Program
{
    static void Mutate(Point p) { p.X = 42; }
}

struct Point
{
    public int X;
    public int Y;
    public Point(int x, int y) { X = x; Y = y; }
}

class Boxed
{
    public int X;
    public Boxed(int x) { X = x; }
}
