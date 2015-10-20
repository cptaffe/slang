using System;
using System.Linq;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;

class Lexer : IEnumerable {
  public struct Token {
    public enum Type {
      Integer,
      FloatingPoint,
      String,
      Operator
    };
    public Type type;
    public string str;
  };
  BlockingCollection<Token> _toks;
  IEnumerable<char> _src;

  public Lexer(IEnumerable<char> src) {
    _src = src;
    _toks = new BlockingCollection<Token>();
    new Thread(new ThreadStart(this.Run)).Start();
  }

  class Scanner {
    Stack<char> _currentBuffer;
    LinkedList<char> _toScan;

    public Scanner(IEnumerable<char> str) {
      _toScan = new LinkedList<char>(str);
      _currentBuffer = new Stack<char>();
    }

    public void AddInput(IEnumerable<char> str) {
      _toScan.Concat(str);
    }

    public char Next() {
      if (_toScan.Count == 0) return '\0';
      _currentBuffer.Push(_toScan.First.Value);
      _toScan.RemoveFirst();
      Console.WriteLine("next: '"+_currentBuffer.Peek()+"'");
      return _currentBuffer.Peek();
    }

    public void Back() {
      _toScan.AddFirst(_currentBuffer.Pop());
    }

    public char Peek() {
      var c = Next();
      Back();
      return c;
    }

    public void Clear() {
      Console.WriteLine("'"+new String(_currentBuffer.ToArray())+"'");
      _currentBuffer.Clear();
    }

    public IEnumerable<char> Get() {
      return _currentBuffer.ToArray();
    }

    public string Emit() {
      var s = new String(Get().ToArray());
      Clear();
      return s;
    }

    public int Count() {
        return _currentBuffer.Count;
    }
  }

  delegate StateFunc StateFunc(Scanner s);

  StateFunc WhiteSpaceStateFactory(StateFunc n) {
    return delegate(Scanner s) {
      char c;
      while ((c = s.Next()) == ' ' || c == '\t' || c == '\n');
      if (c != '\0') s.Back();
      s.Clear(); // Whitespace is unimportant
      return n;
    };
  }

  StateFunc OperatorState(Scanner s) {
    char c;
    while ((c = s.Next()) == '+' || c == '-' || c == '*' || c == '/') {}
    if (c != '\0') s.Back(); // Back off nonoperator
    if (s.Count() == 0) return null;
    _toks.Add(new Token(){ str = s.Emit(), type = Token.Type.Operator });
    return WhiteSpaceStateFactory(NumberState);
  }

  StateFunc NumberState(Scanner s) {
    char c;
    while ((c = s.Next()) >= '0' && c <= '9') {}
    if (c != '\0') s.Back(); // Back off non-numeric
    if (s.Count() == 0) return null;
    if (c == '.') return null; // FloatingPoint number
    // Have lexed Integer.
    _toks.Add(new Token(){ str = s.Emit(), type = Token.Type.Integer });
    return WhiteSpaceStateFactory(OperatorState);
  }

  StateFunc StartState(Scanner s) {
    return NumberState;
  }

  void Run() {
    Scanner scan = new Scanner(_src);
    StateFunc func = StartState;
    while (func != null) func = func(scan);
    _toks.CompleteAdding();
  }

  IEnumerator IEnumerable.GetEnumerator() {
    return _toks.GetConsumingEnumerable().GetEnumerator();
  }
}

class Lang {
  static void Main(string[] args) {
    foreach (Lexer.Token t in new Lexer("3 + 4")) {
      Console.WriteLine("tok: "+"'"+t.str+"'");
    }
  }
}
