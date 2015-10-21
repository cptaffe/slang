using System;
using System.Linq;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;

struct Token {
  public enum Type {
    Integer,
    FloatingPoint,
    String,
    Operator,
    Newline
  };
  public Type type;
  public string str;

  public struct Position {
    public int i, line, col;

    override public string ToString() {
      return line+","+col;
    }

    public void Update(char c) {
      i++;
      if (c == '\n') {
        col = 0;
        line++;
      } else {
        col++;
      }
    }
  }
  public Position pos;

  override public string ToString() {
    return "("+pos+")->'"+str+"'";
  }
}

class Lexer : IEnumerable<Token> {
  BlockingCollection<Token> _toks;
  IEnumerable<char> _src;

  public Lexer(IEnumerable<char> src) {
    _src = src;
    _toks = new BlockingCollection<Token>();
    new Thread(new ThreadStart(this.Run)).Start();
  }

  class Scanner {

    struct Character {
      public char c;
      public Token.Position pos;
    }

    Stack<Character> _currentBuffer;
    LinkedList<char> _toScan;
    Token.Position pos;

    public Scanner(IEnumerable<char> str) {
      _toScan = new LinkedList<char>(str);
      _currentBuffer = new Stack<Character>();
    }

    public void AddInput(IEnumerable<char> str) {
      _toScan.Concat(str);
    }

    public char Next() {
      if (_toScan.Count == 0) return '\0';
      var c = _toScan.First.Value;
      _currentBuffer.Push(new Character(){ c = c, pos = pos });
      pos.Update(c);
      _toScan.RemoveFirst();
      return c;
    }

    public void Back() {
      var c = _currentBuffer.Pop();
      pos = c.pos;
      _toScan.AddFirst(c.c);
    }

    public char Peek() {
      var c = Next();
      Back();
      return c;
    }

    public void Clear() {
      _currentBuffer.Clear();
    }

    public Token Emit(Token.Type type) {
      string s = "";
      foreach (var c in _currentBuffer) {
        s += c.c;
      }
      Token.Position pos = _currentBuffer.ToArray()[0].pos;
      Clear();
      return new Token(){ str = s, pos = pos, type = type };
    }

    public int Count() {
        return _currentBuffer.Count;
    }
  }

  delegate StateFunc StateFunc(Scanner s);

  StateFunc WhiteSpaceStateFactory(StateFunc n) {
    return delegate(Scanner s) {
      char c;
      while ((c = s.Next()) == ' ' || c == '\t' || c == '\n' || c == '\r') {
        if (c == '\n') {
          _toks.Add(s.Emit(Token.Type.Newline));
        } else s.Clear(); // Whitespace is unimportant
      }
      if (c != '\0') s.Back();
      return n;
    };
  }

  StateFunc OperatorState(Scanner s) {
    char c;
    while ((c = s.Next()) == '+' || c == '-' || c == '*' || c == '/') {}
    if (c != '\0') s.Back(); // Back off nonoperator
    if (s.Count() == 0) return null;
    _toks.Add(s.Emit(Token.Type.Operator));
    return WhiteSpaceStateFactory(NumberState);
  }

  StateFunc NumberState(Scanner s) {
    char c;
    while ((c = s.Next()) >= '0' && c <= '9') {}
    if (c != '\0') s.Back(); // Back off non-numeric
    if (s.Count() == 0) return null;
    if (c == '.') return null; // FloatingPoint number
    // Have lexed Integer.
    _toks.Add(s.Emit(Token.Type.Integer));
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

  IEnumerator<Token> IEnumerable<Token>.GetEnumerator() {
    return _toks.GetConsumingEnumerable().GetEnumerator();
  }
}

class Parser {
  IEnumerable<Token> _toks;
  IEnumerator<Token> _toke;

  public struct SyntaxTree {
    public Token tok;
    public List<SyntaxTree> children;

    public SyntaxTree(Token t) {
      tok = t;
      children = new List<SyntaxTree>();
    }

    public void Add(SyntaxTree t) {
      children.Add(t);
    }

    override public string ToString() {
      string s = "";
      s += tok;
      if (children.Count > 0) {
        s += "{";
        foreach (var t in children) {
          s += t+", ";
        }
        s += "}";
      }
      return s;
    }
  }

  SyntaxTree _root;
  BlockingCollection<SyntaxTree> _trees;

  public Parser(IEnumerable<Token> toks) {
    _toks = toks;
    _trees = new BlockingCollection<SyntaxTree>();
    new Thread(new ThreadStart(this.Run)).Start();
  }

  delegate StateFunc StateFunc();

  int OperatorPrecedence(Token t) {
    if (t.str == "+" || t.str == "-") {
      return 2;
    } else if (t.str == "*" || t.str == "/") {
      return 1;
    }
    throw new Exception("Unknown Operator: "+t);
  }

  StateFunc ExpressionState() {
    // Shunting Yard
    var output = new Stack<SyntaxTree>();
    var operators = new Stack<Token>();

    foreach(var t in _toks) {
      if (t.type == Token.Type.Integer) {
        output.Push(new SyntaxTree(t));
      } else if (t.type == Token.Type.Operator) {
        while (operators.Count > 0 && OperatorPrecedence(t) > (OperatorPrecedence(operators.Peek()))) {
          var st = new SyntaxTree(operators.Pop());
          if (output.Count < 2) return null;
          st.Add(output.Pop());
          st.Add(output.Pop());
          output.Push(st);
        }
        operators.Push(t);
      }
    }
    while (operators.Count > 0) {
      var st = new SyntaxTree(operators.Pop());
      if (output.Count < 2) return null;
      st.Add(output.Pop());
      st.Add(output.Pop());
      output.Push(st);
    }
    foreach (var t in output) {
      Console.WriteLine(t);
    }
    return null;
  }

  public void Run() {
    StateFunc func = ExpressionState;
    while (func != null) func = func();
    _trees.CompleteAdding();
  }
}

class Lang {
  static void Main(string[] args) {
    new Parser(new Lexer("3*5 + 4 * 8\n"));
  }
}
