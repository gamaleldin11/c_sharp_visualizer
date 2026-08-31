using System;

partial class Program
{
    static void Main()
    {
        Animal a = new Dog("Rex");
        Console.WriteLine(a.Speak());
        Console.WriteLine(a.Name);
        Animal b = new Cat("Tom");
        Console.WriteLine(b.Speak());
        Console.WriteLine(Describe(a));
        Console.WriteLine(Describe(b));
    }
}

partial class Program
{
    static string Describe(Animal a) { return a.Name + " says " + a.Speak(); }
}

class Animal
{
    public string Name;
    public Animal(string name) { Name = name; }
    public virtual string Speak() { return "..."; }
}

class Dog : Animal
{
    public Dog(string name) : base(name) { }
    public override string Speak() { return "Woof"; }
}

class Cat : Animal
{
    public Cat(string name) : base(name) { }
    public override string Speak() { return "Meow"; }
}
