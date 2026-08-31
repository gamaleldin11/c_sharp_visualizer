using System;

class MyException : Exception { }

class Program
{
    static void Level2()
    {
        throw new MyException();
    }
    
    static void Level1()
    {
        Level2();
    }
    
    static void Main()
    {
        try
        {
            Level1();
        }
        catch (ArgumentException)
        {
            Console.WriteLine("Wrong catch");
        }
        catch (MyException)
        {
            Console.WriteLine("Caught!");
        }
    }
}
