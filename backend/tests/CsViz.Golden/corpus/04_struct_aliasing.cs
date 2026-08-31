using System;

struct MyStruct { public int X; }
class MyClass { public int X; }

class Program
{
    static void Main()
    {
        MyStruct s1 = new MyStruct { X = 1 };
        MyStruct s2 = s1;
        s2.X = 2; // s1.X is still 1
        
        MyClass c1 = new MyClass { X = 1 };
        MyClass c2 = c1;
        c2.X = 2; // c1.X is now 2

        // Printed so the differential oracle covers this file too. Without output, a program
        // can be silently wrong and still "pass" - which is exactly how the object
        // initializer bug survived here.
        Console.WriteLine(s1.X);
        Console.WriteLine(s2.X);
        Console.WriteLine(c1.X);
        Console.WriteLine(c2.X);
    }
}
