using System;

class Base { public virtual void Speak() { Console.WriteLine("Base"); } }
class Derived : Base { public override void Speak() { Console.WriteLine("Derived"); } }

class Program
{
    static void Main()
    {
        Base b = new Derived();
        b.Speak();
    }
}
