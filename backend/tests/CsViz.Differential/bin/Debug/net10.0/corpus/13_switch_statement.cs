using System;

class Program
{
    static void Main()
    {
        for (int i = 0; i < 6; i++)
        {
            switch (i)
            {
                case 0:
                    Console.WriteLine("zero");
                    break;
                case 1:
                case 2:
                    Console.WriteLine("one or two");
                    break;
                case 3:
                    Console.WriteLine("three");
                    break;
                default:
                    Console.WriteLine("many");
                    break;
            }
        }
        string word = "b";
        switch (word)
        {
            case "a": Console.WriteLine("A"); break;
            case "b": Console.WriteLine("B"); break;
            default: Console.WriteLine("?"); break;
        }
    }
}
