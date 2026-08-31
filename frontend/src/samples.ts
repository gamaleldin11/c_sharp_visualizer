export interface Sample {
  name: string;
  source: string;
}

export const SAMPLES: Sample[] = [
  {
    name: 'Custom Code',
    source: `using System;

class Program {
    static void Main() {
        Console.WriteLine("Hello, World!");
    }
}
`,
  },
  {
    name: 'Linked list',
    source: `class Node { public int Val; public Node Next; }

class Program {
    static int Sum(Node n) {
        int total = 0;
        while (n != null) {
            total = total + n.Val;
            n = n.Next;
        }
        return total;
    }

    static void Main() {
        Node a = new Node();
        a.Val = 10;
        Node b = new Node();
        b.Val = 32;
        a.Next = b;
        int s = Sum(a);
        System.Console.WriteLine(s);
    }
}
`,
  },
  {
    name: 'Recursion',
    source: `class Program {
    static int Fib(int n) {
        if (n < 2) { return n; }
        return Fib(n - 1) + Fib(n - 2);
    }

    static void Main() {
        int r = Fib(6);
        System.Console.WriteLine(r);
    }
}
`,
  },
  {
    name: 'Array sort',
    source: `class Program {
    static void Main() {
        int[] a = new int[5];
        a[0] = 5; a[1] = 3; a[2] = 4; a[3] = 1; a[4] = 2;

        for (int i = 0; i < 5; i = i + 1) {
            for (int j = 0; j < 4 - i; j = j + 1) {
                if (a[j] > a[j + 1]) {
                    int t = a[j];
                    a[j] = a[j + 1];
                    a[j + 1] = t;
                }
            }
        }

        for (int i = 0; i < 5; i = i + 1) {
            System.Console.WriteLine(a[i]);
        }
    }
}
`,
  },
  {
    name: 'Struct vs class',
    source: `struct PointS { public int X; }
class PointC { public int X; }

class Program {
    static void Main() {
        PointS s1 = new PointS();
        s1.X = 1;
        PointS s2 = s1;   // copies the value
        s2.X = 99;

        PointC c1 = new PointC();
        c1.X = 1;
        PointC c2 = c1;   // copies the reference
        c2.X = 99;

        System.Console.WriteLine(s1.X);
        System.Console.WriteLine(c1.X);
    }
}
`,
  },
];
