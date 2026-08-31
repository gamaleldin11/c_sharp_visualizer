using System;

class Program
{
    static void Main()
    {
        Node head = new Node(1);
        head.Next = new Node(2);
        head.Next.Next = new Node(3);
        int sum = 0;
        Node cur = head;
        while (cur != null)
        {
            sum += cur.Value;
            Console.WriteLine(cur.Value);
            cur = cur.Next;
        }
        Console.WriteLine(sum);
        Console.WriteLine(head.Next.Next.Next == null);
    }
}

class Node
{
    public int Value;
    public Node Next;
    public Node(int v) { Value = v; }
}
