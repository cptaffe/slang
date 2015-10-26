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
      var t = new Token(){ str = s, pos = pos, type = type };
      Console.WriteLine("tok: "+t);
      return t;
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

  StateFunc MaybeDotState(Scanner s) {
    if (s.Peek() == '.') {
      _toks.Add(s.Emit(Token.Type.Operator));
      return WhiteSpaceStateFactory(IdentStateFuncFactory(false));
    } else return WhiteSpaceStateFactory(ExpressionState);
  }

  StateFunc IdentStateFuncFactory(bool orKeyword) {
    return delegate(Scanner s) {
      s.NextWhile(IsIdent);
      // Have lexed Ident.
      string id = s.Buffer();
      if (id.Length == 0) throw new Exception("Expected Ident, found nothing");
      // Keywords
      string[] keywords = {
        "as"
      };
      if (Array.IndexOf(keywords, id) != -1) {
        if (orKeyword) {
          s.BackAll();
          return KeywordState;
        } else throw new Exception("Expected Ident, found reserved word '"+id+"'");
      } else {
        _toks.Add(s.Emit(Token.Type.Identifier));
        return WhiteSpaceStateFactory(MaybeDotState);
      }
    };
  }

  StateFunc IdentState(Scanner s) {
    return IdentStateFuncFactory(false);
  }

  StateFunc IdentOrKeywordState(Scanner s) {
    return IdentStateFuncFactory(true);
  }

  bool IsIdent(char c) {
    return Char.IsLetter(c) || Char.IsNumber(c) || c == '_';
  }

  bool IsOperatorSymbol(char c) {
    return c == '*' || c == '/' || c == '+' || c == '-' || c == ',' || c == '=' || c == ';';
  }

  StateFunc OperatorState(Scanner s) {
    // General operators
    string[] ops = {
      "*", // Multiplication
      "/", // Division
      "+", // Addition
      "-", // Subtraction
      ",", // Comma Operator
      "=", // Assignment Operator
    };
    s.NextWhile(IsOperatorSymbol);
    string maybeOp = s.Buffer();
    if (Array.IndexOf(ops, maybeOp) != -1) {
      _toks.Add(s.Emit(Token.Type.Operator));
      return WhiteSpaceStateFactory(ExpressionState);
    } else {
      if (maybeOp == ";") {
        // Semicolon Operator terminates an expression
        _toks.Add(s.Emit(Token.Type.StatementTerminal));
        return WhiteSpaceStateFactory(StartState);
      } else throw new Exception("Expected Operator, found '"+maybeOp+"'");
    }
  }

  StateFunc ExpressionState(Scanner s) {
    var c = s.Next();
    if (c == '\0') {
      return null; // EOF reached
    } else if (Char.IsLetter(c)) {
      return IdentOrKeywordState;
    } else if (Char.IsNumber(c)) {
      return NumberState;
    } else if (IsOperatorSymbol(c)) {
      return OperatorState;
    } else throw new Exception("Unexpected character '"+c+"'");
  }

  StateFunc AsState(Scanner s) {
    _toks.Add(s.Emit(Token.Type.Keyword));
    return WhiteSpaceStateFactory(IdentState);
  }

  StateFunc KeywordState(Scanner s) {
    s.NextWhile(IsIdent);
    var str = s.Buffer();
    switch (str) {
      case "as":
        return AsState;
      default:
        throw new Exception("Unknown keword '"+str+"'");
    }
  }

  StateFunc StartState(Scanner s) {
    return WhiteSpaceStateFactory(ExpressionState);
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

  public class RootNode : INode {
    Token INode.Token() {
      throw new Exception("Root Nodes have no Tokens");
    }

    override public string ToString() {
      return "Root";
    }
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
      } else if (tok.type == Token.Type.Keyword) {
        return KeywordNode.Factory(tok);
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
    Assignment,
    SummationDifference,
    ProductQuotientRemainder,
    Dot
  };

  public interface IOperator {
    OperatorAssociativity Associativity(); // Operator Associativity
    OperatorPrecedence Precedence(); // Operator Precedence
    int Arguments();
  }

  class SummationNode : OperatorNode, IOperator {
    public SummationNode(Token t) : base(t) {}

    OperatorAssociativity IOperator.Associativity() {
      return OperatorAssociativity.Left;
    }

    OperatorPrecedence IOperator.Precedence() {
      return OperatorPrecedence.SummationDifference;
    }

    int IOperator.Arguments() {
      return 2;
    }
  }

  class DifferenceNode : OperatorNode, IOperator {
    public DifferenceNode(Token t) : base(t) {}

    OperatorAssociativity IOperator.Associativity() {
      return OperatorAssociativity.Left;
    }

    OperatorPrecedence IOperator.Precedence() {
      return OperatorPrecedence.SummationDifference;
    }

    int IOperator.Arguments() {
      return 2;
    }
  }

  class ProductNode : OperatorNode, IOperator {
    public ProductNode(Token t) : base(t) {}

    OperatorAssociativity IOperator.Associativity() {
      return OperatorAssociativity.Left;
    }

    OperatorPrecedence IOperator.Precedence() {
      return OperatorPrecedence.ProductQuotientRemainder;
    }

    int IOperator.Arguments() {
      return 2;
    }
  }

  class QuotientNode : OperatorNode, IOperator {
    public QuotientNode(Token t) : base(t) {}

    OperatorAssociativity IOperator.Associativity() {
      return OperatorAssociativity.Left;
    }

    OperatorPrecedence IOperator.Precedence() {
      return OperatorPrecedence.ProductQuotientRemainder;
    }

    int IOperator.Arguments() {
      return 2;
    }
  }

  class RemainderNode : OperatorNode, IOperator {
    public RemainderNode(Token t) : base(t) {}

    OperatorAssociativity IOperator.Associativity() {
      return OperatorAssociativity.Left;
    }

    OperatorPrecedence IOperator.Precedence() {
      return OperatorPrecedence.ProductQuotientRemainder;
    }

    int IOperator.Arguments() {
      return 2;
    }
  }

  public class DotNode : OperatorNode, IOperator {
    public DotNode(Token t) : base(t) {}

    OperatorAssociativity IOperator.Associativity() {
      return OperatorAssociativity.Left;
    }

    OperatorPrecedence IOperator.Precedence() {
      return OperatorPrecedence.Dot;
    }

    int IOperator.Arguments() {
      return 2;
    }
  }

  public class AssignmentNode : OperatorNode, IOperator {
    public AssignmentNode(Token t) : base(t) {}

    OperatorAssociativity IOperator.Associativity() {
      return OperatorAssociativity.Right;
    }

    OperatorPrecedence IOperator.Precedence() {
      return OperatorPrecedence.Assignment;
    }

    int IOperator.Arguments() {
      return 2;
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
          return new DotNode(tok);
        case "=":
          return new AssignmentNode(tok);
        default:
          throw new Exception("Unknown operator: "+tok);
      }
    }
  }

  public interface IValue {
    object GetValue();
  }

  public class IntegerNode : ValueNode, IValue {
    int val;
    public IntegerNode(Token tok, int v) : base (tok) {
      val = v;
    }

    object IValue.GetValue() {
      return val;
    }
  }

  public class FloatingPointNode : ValueNode, IValue {
    float val;
    public FloatingPointNode(Token tok, float v) : base (tok) {
      val = v;
    }

    object IValue.GetValue() {
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

  public interface IReference {
    object GetReferencedValue();
  }

  public class IdentifierNode : ReferenceNode, IReference {
    public IdentifierNode(Token tok) : base(tok) {}

    object IReference.GetReferencedValue() {
      throw new Exception("Unimplemented");
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

  public interface IKeyword {}

  public class VarNode : KeywordNode, IKeyword {
    public VarNode(Token t) : base(t) {}
  }

  public class KeywordNode : Node {
    public KeywordNode(Token t) : base(t) {}

    public static new KeywordNode Factory(Token t) {
      if (t.type == Token.Type.Keyword) {
        if (t.str == "var") {
          return new VarNode(t);
        } else throw new Exception("Unknown keyword: "+t);
      } else throw new Exception("Not a keyword: "+t);
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

  BlockingCollection<SyntaxTree> _trees;

  IEnumerator IEnumerable.GetEnumerator() {
    return _trees.GetConsumingEnumerable().GetEnumerator();
  }

  IEnumerator<SyntaxTree> IEnumerable<SyntaxTree>.GetEnumerator() {
    return _trees.GetConsumingEnumerable().GetEnumerator();
  }

  public class Exception : System.Exception {
    SyntaxTree tree;

    public Exception(SyntaxTree t, string s) : base(s) {
      tree = t;
    }

    override public string ToString() {
      return tree+": "+base.ToString();
    }
  }

  public Parser(IEnumerable<Token> toks) {
    _toks = toks;
    _trees = new BlockingCollection<SyntaxTree>();
    new Thread(new ThreadStart(this.Run)).Start();
  }

  delegate StateFunc StateFunc();

  interface INode {
    SyntaxTree GrabChildren(Stack<SyntaxTree> t);
  }

  class Node : INode {
    public SyntaxTree tree {get;}
    int children;
    public Node(SyntaxTree t) {
      tree = t;
      if (t.node as SyntaxTree.IOperator != null) {
        children = (t.node as SyntaxTree.IOperator).Arguments();
      } else throw new Exception(t, "Not an operator: "+t);
    }

    SyntaxTree INode.GrabChildren(Stack<SyntaxTree> t) {
      if (t.Count() >= children) {
        foreach (var tok in t.Take(children).Reverse()) {
          t.Pop(); // does not remove with take
          tree.Add(tok);
        }
      } else throw new Exception(tree, "Not enough arguments (expected "+children+")");
      return tree;
    }
  }

  class ShuntingYard {
    Stack<SyntaxTree> output, operators;
    Parser _parser;

    public ShuntingYard(Parser p) {
      _parser = p;
      output = new Stack<SyntaxTree>();
      operators = new Stack<SyntaxTree>();
    }

    public void Shunt() {
      for (;;) {
        foreach(var t in _parser._toks) {
          if (t.type == Token.Type.StatementTerminal) {
            break;
          } else if (t.type == Token.Type.Integer || t.type == Token.Type.FloatingPoint || t.type == Token.Type.Identifier) {
            output.Push(new SyntaxTree(SyntaxTree.Node.Factory(t)));
          } else if (t.type == Token.Type.Operator) {
            var tree = new SyntaxTree(SyntaxTree.Node.Factory(t));
            // Move lesser operators off the stack,
            // place them on the output stack with the correct
            // children pulled from the top of output.
            while (operators.Count() > 0) {
              var oOp = tree.node as SyntaxTree.IOperator;
              var op = operators.Peek().node as SyntaxTree.IOperator;
              if ((oOp.Associativity() == SyntaxTree.OperatorAssociativity.Left
                  && oOp.Precedence() <= op.Precedence())
                || (oOp.Associativity() == SyntaxTree.OperatorAssociativity.Right
                  && oOp.Precedence() < op.Precedence())) {
                output.Push((new Node(operators.Pop()) as INode).GrabChildren(output));
              } else break;
            }
            operators.Push(tree);
          }
        }
        // Remove all remaining operators from the stack,
        // placing them on the output with the correct
        // children pulled from the top of output.
        // output: 1, 2; operators: +; => output: +{1,2};
        while (operators.Count() > 0) {
          output.Push((new Node(operators.Pop()) as INode).GrabChildren(output));
        }
        if (output.Count() > 0) {
          while (output.Count() > 0) _parser._trees.Add(output.Pop());
        } else break;
      }
    }

    SyntaxTree WithChildren(SyntaxTree st, int children) {
      if (output.Count() >= children) {
        foreach (var tok in output.Take(children).Reverse()) {
          output.Pop(); // does not remove with take
          st.Add(tok);
        }
      } else throw new Exception(st, "Not enough arguments (expected "+children+")");
      return st;
    }
  }

  StateFunc ExpressionState() {
    new ShuntingYard(this).Shunt();
    return null;
  }

  StateFunc StartState() {
    return ExpressionState;
  }

  public void Run() {
    StateFunc func = StartState;
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
