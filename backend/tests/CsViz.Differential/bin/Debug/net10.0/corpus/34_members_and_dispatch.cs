using System;

class Program
{
    static void Main()
    {
        Counter c = new Counter();
        Console.WriteLine(c.Start);
        Console.WriteLine(c.Doubled);

        Holder h = new Holder();
        Console.WriteLine(h.Inner.Value);

        IShape s = new Square();
        Console.WriteLine(s.Area());
        Console.WriteLine(Describe(s));

        IShape r = new Rect();
        Console.WriteLine(r.Area());
        Console.WriteLine(Describe(r));
    }

    static string Describe(IShape shape) { return shape.Name() + " has area " + shape.Area(); }
}

interface IShape
{
    int Area();
    string Name();
}

class Square : IShape
{
    public int Side = 4;
    public int Area() { return Side * Side; }
    public string Name() { return "square"; }
}

class Rect : IShape
{
    public int W = 2;
    public int H = 5;
    public int Area() { return W * H; }
    public string Name() { return "rect"; }
}

// Field initializers, and an expression-bodied member reading one.
class Counter
{
    public int Start = 3;
    public int Doubled => Start * 2;
}

class Inner { public int Value = 11; }

class Holder { public Inner Inner = new Inner(); }
