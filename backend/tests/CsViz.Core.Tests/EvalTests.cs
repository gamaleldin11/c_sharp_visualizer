using System;
using Xunit;
using CsViz.Core.Values;

namespace CsViz.Core.Tests;

public class EvalTests
{
    [Fact]
    public void Test_LocalAssignment_And_Math()
    {
        string code = @"
class Program {
    static void Main() {
        int x = 5;
        int y = x + 10;
    }
}";
        var (eval, rootOp) = TestHelper.CompileAndSetup(code);
        eval.Run(rootOp);
        
        Assert.Empty(eval.ValueStack);
        Assert.Empty(eval.ContStack);
    }
    
    [Fact]
    public void Test_WhileLoop()
    {
        string code = @"
class Program {
    static void Main() {
        int i = 0;
        while (i < 5) {
            i = i + 1;
        }
    }
}";
        var (eval, rootOp) = TestHelper.CompileAndSetup(code);
        eval.Run(rootOp);
        
        Assert.Empty(eval.ValueStack);
    }

    [Fact]
    public void Test_Array_And_Math()
    {
        string code = @"
using System;
class Program {
    static void Main() {
        int[] arr = new int[3];
        arr[0] = 5;
        arr[1] = Math.Max(10, arr[0]);
    }
}";
        var (eval, rootOp) = TestHelper.CompileAndSetup(code);
        eval.Run(rootOp);
        
        Assert.Empty(eval.ValueStack);
    }
    
    [Fact]
    public void Test_Virtual_Dispatch()
    {
        string code = @"
class Base {
    public virtual void Modify(int[] arr) { arr[0] = 1; }
}
class Derived : Base {
    public override void Modify(int[] arr) { arr[0] = 2; }
}
class Program {
    static void Main() {
        int[] arr = new int[1];
        Base b = new Derived();
        b.Modify(arr);
        // arr[0] should be 2
    }
}";
        var (eval, rootOp) = TestHelper.CompileAndSetup(code);
        eval.Run(rootOp);
        
        Assert.Empty(eval.ValueStack);
    }
    
    [Fact]
    public void Test_List()
    {
        string code = @"
using System.Collections.Generic;
class Program {
    static void Main() {
        List<int> list = new List<int>();
        list.Add(5);
        list.Add(10);
        int c = list.Count;
        int sum = list[0] + list[1];
        list.Clear();
    }
}";
        var (eval, rootOp) = TestHelper.CompileAndSetup(code);
        eval.Run(rootOp);
        
        Assert.Empty(eval.ValueStack);
    }
    
    [Fact]
    public void Test_Dictionary_And_ForEach()
    {
        string code = @"
using System.Collections.Generic;
class Program {
    static void Main() {
        Dictionary<int, int> dict = new Dictionary<int, int>();
        dict.Add(1, 10);
        dict.Add(2, 20);
        int sum = 0;
        foreach (KeyValuePair<int, int> kvp in dict) {
            sum = sum + kvp.Value;
        }
        
        Stack<int> s = new Stack<int>();
        s.Push(sum);
        int top = s.Pop();
    }
}";
        var (eval, rootOp) = TestHelper.CompileAndSetup(code);
        eval.Run(rootOp);
        
        Assert.Empty(eval.ValueStack);
    }
}
