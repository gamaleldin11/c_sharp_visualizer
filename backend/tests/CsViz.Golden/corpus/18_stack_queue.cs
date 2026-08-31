using System;
using System.Collections.Generic;

class Program
{
    static void Main()
    {
        var stack = new Stack<int>();
        stack.Push(1);
        stack.Push(2);
        stack.Push(3);
        Console.WriteLine(stack.Count);
        Console.WriteLine(stack.Peek());
        Console.WriteLine(stack.Pop());
        Console.WriteLine(stack.Pop());
        var queue = new Queue<string>();
        queue.Enqueue("a");
        queue.Enqueue("b");
        Console.WriteLine(queue.Count);
        Console.WriteLine(queue.Peek());
        Console.WriteLine(queue.Dequeue());
        Console.WriteLine(queue.Dequeue());
    }
}
