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
    Identifier,
    Operator,
    Keyword,
    Newline,
    StatementTerminal
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
    return Enum.GetName(typeof(Type), type)+"("+pos+")->'"+str+"'";
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

    LinkedList<Character> _currentBuffer;
    LinkedList<char> _toScan;
    Token.Position pos;

    public Scanner(IEnumerable<char> str) {
      _toScan = new LinkedList<char>(str);
      _currentBuffer = new LinkedList<Character>();
    }

    public void AddInput(IEnumerable<char> str) {
      _toScan.Concat(str);
    }

    public char Next() {
      if (_toScan.Count == 0) return '\0';
      var c = _toScan.First.Value;
      _currentBuffer.AddLast(new Character(){ c = c, pos = pos });
      pos.Update(c);
      _toScan.RemoveFirst();
      return c;
    }

    public void Back() {
      var c = _currentBuffer.Last.Value;
      _currentBuffer.RemoveLast();
      pos = c.pos;
      _toScan.AddFirst(c.c);
    }

    public char Peek() {
      var c = Next();
      Back();
      return c;
    }

    public delegate bool Predicate(char c);

    public char NextWhile(Predicate p) {
      char c;
      while (p(c = Next()));
      if (c != '\0') Back(); // Back off non-numeric
      return c;
    }

    public void Clear() {
      _currentBuffer.Clear();
    }

    public void BackAll() {
      while (_currentBuffer.Count() > 0) Back();
    }

    public string Buffer() {
      return new String(_currentBuffer.Select(x => x.c).ToArray());
    }

    public Token Emit(Token.Type type) {
      string s = Buffer();
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

  StateFunc NumberState(Scanner s) {
    var c = s.NextWhile(Char.IsNumber);
    if (c == '.') return null; // FloatingPoint number
    // Have lexed Integer.
    _toks.Add(s.Emit(Token.Type.Integer));
    return WhiteSpaceStateFactory(ExpressionState);
  }

  bool IsIdent(char c) {
    return Char.IsLetter(c) || Char.IsNumber(c) || c == '_';
  }

  StateFunc IdentState(Scanner s) {
    s.NextWhile(IsIdent);
    // Have lexed Ident.
    _toks.Add(s.Emit(Token.Type.Identifier));
    return WhiteSpaceStateFactory(ExpressionState);
  }

  StateFunc OperatorState(Scanner s) {
    // Prefix operators
    string[] ops = {
      "*",
      "/",
      "+",
      "-"
    };
    s.NextWhile(Char.IsSymbol);
    string maybeOp = s.Buffer();
    if (Array.IndexOf(ops, maybeOp) == -1) {
      s.BackAll();
      return null;
    }
    _toks.Add(s.Emit(Token.Type.Operator));
    return WhiteSpaceStateFactory(ExpressionState);
  }

  StateFunc SemiColonState(Scanner s) {
    _toks.Add(s.Emit(Token.Type.StatementTerminal));
    return StartState;
  }

  StateFunc ExpressionState(Scanner s) {
    var c = s.Next();
    if (c == '\0') {
      return null; // EOF reached
    } else if (Char.IsLetter(c)) {
      return IdentState;
    } else if (Char.IsNumber(c)) {
      return NumberState;
    } else if (c == ';') {
      return SemiColonState;
    } else {
      return OperatorState;
    }
  }

  StateFunc AssignState(Scanner s) {
    if (s.Next() == '=') {
      _toks.Add(s.Emit(Token.Type.Operator));
    } else return null;
    return WhiteSpaceStateFactory(ExpressionState);
  }

  StateFunc VarIdentState(Scanner s) {
    s.NextWhile(IsIdent);
    if (s.Count() > 0) {
      _toks.Add(s.Emit(Token.Type.Identifier));
    } else return null;
    return WhiteSpaceStateFactory(AssignState);
  }

  StateFunc VarState(Scanner s) {
    s.NextWhile(IsIdent);
    if (s.Buffer() == "var") {
      _toks.Add(s.Emit(Token.Type.Keyword));
      s.NextWhile(IsIdent);
    } else return null;
    return WhiteSpaceStateFactory(VarIdentState);
  }

  StateFunc StartState(Scanner s) {
    return WhiteSpaceStateFactory(VarState);
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

struct SyntaxTree {

  public interface INode {
    Token Token();
  }

  public class Node: INode {
    Token token;

    protected Node(Token tok) {
      token = tok;
    }

    public static Node Factory(Token tok) {
      if (tok.type == Token.Type.Integer || tok.type == Token.Type.FloatingPoint) {
        return ValueNode.Factory(tok);
      } else if (tok.type == Token.Type.Identifier) {
        return ReferenceNode.Factory(tok);
      } else if (tok.type == Token.Type.Operator) {
        return OperatorNode.Factory(tok);
      } else throw new Exception("Unknown token: "+tok);
    }

    Token INode.Token() {
      return token;
    }

    override public string ToString() {
      return ""+token;
    }
  }

  public enum OperatorAssociativity {
    Right,
    Left
  }

  public enum OperatorPrecedence {
    Dot,
    Assignment,
    ProductQuotientRemainder,
    SummationDifference
  };

  public interface IOperatorNode {
    OperatorAssociativity Associativity(); // Operator Associativity
    OperatorPrecedence Precedence(); // Operator Precedence
  }

  class SummationNode : OperatorNode, IOperatorNode {
    public SummationNode(Token t) : base(t) {}

    OperatorAssociativity IOperatorNode.Associativity() {
      return OperatorAssociativity.Left;
    }

    OperatorPrecedence IOperatorNode.Precedence() {
      return OperatorPrecedence.SummationDifference;
    }
  }

  class DifferenceNode : OperatorNode, IOperatorNode {
    public DifferenceNode(Token t) : base(t) {}

    OperatorAssociativity IOperatorNode.Associativity() {
      return OperatorAssociativity.Left;
    }

    OperatorPrecedence IOperatorNode.Precedence() {
      return OperatorPrecedence.SummationDifference;
    }
  }

  class ProductNode : OperatorNode, IOperatorNode {
    public ProductNode(Token t) : base(t) {}

    OperatorAssociativity IOperatorNode.Associativity() {
      return OperatorAssociativity.Left;
    }

    OperatorPrecedence IOperatorNode.Precedence() {
      return OperatorPrecedence.ProductQuotientRemainder;
    }
  }

  class QuotientNode : OperatorNode, IOperatorNode {
    public QuotientNode(Token t) : base(t) {}

    OperatorAssociativity IOperatorNode.Associativity() {
      return OperatorAssociativity.Left;
    }

    OperatorPrecedence IOperatorNode.Precedence() {
      return OperatorPrecedence.ProductQuotientRemainder;
    }
  }

  class RemainderNode : OperatorNode, IOperatorNode {
    public RemainderNode(Token t) : base(t) {}

    OperatorAssociativity IOperatorNode.Associativity() {
      return OperatorAssociativity.Left;
    }

    OperatorPrecedence IOperatorNode.Precedence() {
      return OperatorPrecedence.ProductQuotientRemainder;
    }
  }

  public class OperatorNode : Node {
    protected OperatorNode(Token tok) : base(tok) {}

    public static new OperatorNode Factory(Token tok) {
      switch(tok.str) {
        case "+":
          return new SummationNode(tok);
        case "-":
          return new DifferenceNode(tok);
        case "*":
          return new ProductNode(tok);
        case "/":
          return new QuotientNode(tok);
        case ".":
          return new SummationNode(tok);
        case "=":
          return new SummationNode(tok);
        default:
          throw new Exception("Unknown operator: "+tok);
      }
    }
  }

  public interface IValueNode {
    object GetValue();
  }

  public class IntegerNode : ValueNode, IValueNode {
    int val;
    public IntegerNode(Token tok, int v) : base (tok) {
      val = v;
    }

    object IValueNode.GetValue() {
      return val;
    }
  }

  public class FloatingPointNode : ValueNode, IValueNode {
    float val;
    public FloatingPointNode(Token tok, float v) : base (tok) {
      val = v;
    }

    object IValueNode.GetValue() {
      return val;
    }
  }

  public class ValueNode : Node {
    public ValueNode(Token tok) : base(tok) {}

    public static new ValueNode Factory(Token t) {
      if (t.type == Token.Type.Integer) {
        int i;
        if (int.TryParse(t.str, out i)) {
          return new IntegerNode(t, i);
        } else throw new Exception("Malformed Integer: "+t.str);
      } else if (t.type == Token.Type.FloatingPoint) {
        float i;
        if (float.TryParse(t.str, out i)) {
          return new FloatingPointNode(t, i);
        } else throw new Exception("Malformed Integer: "+t.str);
      } else throw new Exception("Unknown value: "+t);
    }
  }

  public interface IReferenceNode {
    object GetReferencedValue();
  }

  public class IdentifierNode : ReferenceNode, IReferenceNode {
    public IdentifierNode(Token tok) : base(tok) {}

    object IReferenceNode.GetReferencedValue() {
      return null;
    }
  }

  public class ReferenceNode : Node {
    public ReferenceNode(Token tok) : base(tok) {}

    public static new ReferenceNode Factory(Token t) {
      if (t.type == Token.Type.Identifier) {
        return new IdentifierNode(t);
      } else throw new Exception("Unknown reference: "+t);
    }
  }

  public INode node {get;}
  public List<SyntaxTree> children;

  public SyntaxTree(INode n) {
    node = n;
    children = new List<SyntaxTree>();
  }

  public void Add(SyntaxTree t) {
    children.Add(t);
  }

  override public string ToString() {
    string s = "";
    s += node;
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

class Parser : IEnumerable<SyntaxTree> {
  IEnumerable<Token> _toks;
  IEnumerator<Token> _toke;

  SyntaxTree _root;
  BlockingCollection<SyntaxTree> _trees;

  IEnumerator IEnumerable.GetEnumerator() {
    return _trees.GetConsumingEnumerable().GetEnumerator();
  }

  IEnumerator<SyntaxTree> IEnumerable<SyntaxTree>.GetEnumerator() {
    return _trees.GetConsumingEnumerable().GetEnumerator();
  }

  public Parser(IEnumerable<Token> toks) {
    _toks = toks;
    _trees = new BlockingCollection<SyntaxTree>();
    new Thread(new ThreadStart(this.Run)).Start();
  }

  delegate StateFunc StateFunc();

  StateFunc ExpressionState() {
    // Shunting Yard
    var output = new Stack<SyntaxTree>();
    var operators = new Stack<SyntaxTree>();

    foreach(var t in _toks) {

      if (t.type == Token.Type.Integer || t.type == Token.Type.FloatingPoint || t.type == Token.Type.Identifier) {
        output.Push(new SyntaxTree(SyntaxTree.Node.Factory(t)));
      } else if (t.type == Token.Type.Operator) {
        var tree = new SyntaxTree(SyntaxTree.Node.Factory(t));
        while (operators.Count > 0 && (tree.node as SyntaxTree.IOperatorNode).Precedence() > (operators.Peek().node as SyntaxTree.IOperatorNode).Precedence()) {
          var st = new SyntaxTree(SyntaxTree.Node.Factory(t));
          foreach (var tok in output.Take(2).Reverse()) {
            output.Pop(); // does not remove with take
            st.Add(tok);
          }
          output.Push(st);
        }
        operators.Push(tree);
      }
    }
    while (operators.Count > 0) {
      var st = operators.Pop();
      foreach (var tree in output.Take(2).Reverse()) {
        output.Pop(); // does not remove with take
        st.Add(tree);
      }
      output.Push(st);
    }
    foreach (var t in output) _trees.Add(t);
    return null;
  }

  public void Run() {
    StateFunc func = ExpressionState;
    while (func != null) func = func();
    _trees.CompleteAdding();
  }
}

class Lang {
  static IEnumerable<char> ReadChars() {
    string line;
    while ((line = Console.ReadLine()) != null) {
      foreach (var c in line) yield return c;
    }
  }

  static void Main(string[] args) {
    foreach (var t in new Parser(new Lexer(ReadChars()))) {
      Console.WriteLine(t);
    }
  }
}
