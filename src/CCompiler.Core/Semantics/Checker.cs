using CCompiler.Core.Ast;

namespace CCompiler.Core.Semantics;

public sealed class ProgramInfo
{
    public Symbol? Main { get; set; }
    /// <summary>Number of tape cells reserved for variables; scratch cells start right after.</summary>
    public int VarCells { get; set; }
}

/// <summary>
/// Semantic analysis on the AST: scopes, declarations, arity, lvalues, plus compile-time evaluation
/// (constant folding with the same unsigned 8-bit semantics the generated Brainfuck has).
/// Variables get static tape addresses; functions are non-recursive so their locals can be static too.
/// </summary>
public sealed class Checker
{
    static readonly HashSet<string> IntWords = ["int", "char", "short", "long", "signed", "unsigned", "_Bool"];

    readonly List<Diagnostic> _diags;
    readonly List<Dictionary<string, Symbol>> _scopes = [];
    readonly ProgramInfo _info = new();
    Symbol? _currentFunc;
    int _nextCell;
    int _loopDepth;

    Checker(List<Diagnostic> diags) { _diags = diags; }

    public static ProgramInfo Check(AstNode program, List<Diagnostic> diags)
    {
        var c = new Checker(diags);
        c.Run(program);
        return c._info;
    }

    void Error(AstNode n, string msg) => _diags.Add(new(Severity.Error, "semantic", n.Line, 0, msg));
    void Warn(AstNode n, string msg) => _diags.Add(new(Severity.Warning, "semantic", n.Line, 0, msg));

    // ───────────── scopes ─────────────

    void Push() => _scopes.Add([]);
    void Pop() => _scopes.RemoveAt(_scopes.Count - 1);

    Symbol? Lookup(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var s)) return s;
        return null;
    }

    Symbol? Declare(AstNode at, Symbol s)
    {
        if (_scopes[^1].ContainsKey(s.Name))
        {
            Error(at, $"'{s.Name}' is already declared in this scope");
            return null;
        }
        _scopes[^1][s.Name] = s;
        return s;
    }

    int Alloc(int size) { var c = _nextCell; _nextCell += size; _info.VarCells = _nextCell; return c; }

    // ───────────── top level ─────────────

    void Run(AstNode program)
    {
        Push();
        foreach (var item in program.Children)
        {
            switch (item.Kind)
            {
                case "VarDecl": VarDecl(item); break;
                case "FuncDef": FuncDef(item); break;
                case "Prototype": break;
                default: Stmt(item); break;
            }
        }
        Pop();
        if (_info.Main is null) _diags.Add(new(Severity.Error, "semantic", 0, 0, "no 'main' function"));
    }

    bool CheckType(AstNode at, string? type, bool allowVoid)
    {
        if (type is null) return false;
        if (type.Contains('*')) { Error(at, $"pointer type '{type}' is not supported by the Brainfuck backend"); return false; }
        if (type.EndsWith("[]")) { Error(at, "array parameters are not supported"); return false; }
        if (type == "void") { if (!allowVoid) Error(at, "variable cannot be void"); return allowVoid; }
        var words = type.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Any(w => !IntWords.Contains(w)))
        {
            Error(at, $"type '{type}' is not supported (only integer types, modelled as 8-bit cells)");
            return false;
        }
        return true;
    }

    void FuncDef(AstNode f)
    {
        var sym = new Symbol { Name = f.Text!, Kind = SymbolKind.Function, Type = f.Type ?? "int", Line = f.Line, Definition = f };
        sym.ReturnsVoid = sym.Type == "void";
        if (!sym.ReturnsVoid) CheckType(f, sym.Type, false);
        if (Declare(f, sym) is null) return;
        f.Symbol = sym;
        if (f.Text == "main") _info.Main = sym;

        _currentFunc = sym;
        var savedDepth = _loopDepth; _loopDepth = 0;
        Push();
        foreach (var p in f.Children[0].Children)
        {
            if (string.IsNullOrEmpty(p.Text)) { Error(p, "parameter without a name"); continue; }
            CheckType(p, p.Type, false);
            var ps = new Symbol { Name = p.Text, Kind = SymbolKind.Variable, Type = p.Type ?? "int", Line = p.Line, Cell = Alloc(1) };
            if (Declare(p, ps) is not null) { p.Symbol = ps; sym.Params.Add(ps); }
        }
        if (f.Text == "main" && sym.Params.Count > 0) Error(f, "main with parameters is not supported");
        if (!sym.ReturnsVoid) sym.ReturnCell = new Symbol { Name = "$ret", Kind = SymbolKind.Variable, Cell = Alloc(1) };

        var body = f.Children[1];
        Push();
        for (var i = 0; i < body.Children.Count; i++)
        {
            var s = body.Children[i];
            if (s.Kind == "Return" && i == body.Children.Count - 1) ReturnStmt(s, sym);
            else Stmt(s);
        }
        Pop();
        Pop();
        _loopDepth = savedDepth;
        _currentFunc = null;
    }

    void ReturnStmt(AstNode r, Symbol fn)
    {
        if (r.Children.Count == 1)
        {
            if (fn.ReturnsVoid) Error(r, "void function returns a value");
            Expr(r.Children[0]);
        }
        else if (!fn.ReturnsVoid && fn.Name != "main") Error(r, "non-void function must return a value");
    }

    // ───────────── declarations ─────────────

    void VarDecl(AstNode d)
    {
        if (!CheckType(d, d.Type, false)) { Declare(d, new Symbol { Name = d.Text!, Kind = SymbolKind.Variable, Cell = 0, Line = d.Line }); return; }

        var sizeNode = d.Children.FirstOrDefault(c => c.Kind == "ArraySize");
        var init = d.Children.FirstOrDefault(c => c.Kind == "Init")?.Children[0];
        var sym = new Symbol { Name = d.Text!, Kind = SymbolKind.Variable, Type = d.Type!, Line = d.Line };

        if (sizeNode is not null)
        {
            sym.IsArray = true;
            var n = -1;
            if (sizeNode.Children.Count == 1)
            {
                Expr(sizeNode.Children[0]);
                if (IsConst(sizeNode.Children[0])) n = sizeNode.Children[0].Value;
                else Error(d, "array size must be a constant expression");
            }
            else if (init is { Kind: "InitList" }) n = init.Children.Count;
            else if (init is { Kind: "Str" }) n = init.Bytes!.Length + 1;
            else Error(d, "array size is missing");
            if (n <= 0 && sizeNode.Children.Count == 1 && IsConst(sizeNode.Children[0])) Error(d, "array size must be positive");
            sym.Size = Math.Max(n, 1);
        }

        if (init is not null) CheckInit(d, sym, init);

        sym.Cell = Alloc(sym.Size);
        if (Declare(d, sym) is not null) d.Symbol = sym;
    }

    void CheckInit(AstNode d, Symbol sym, AstNode init)
    {
        if (sym.IsArray)
        {
            if (init.Kind == "InitList")
            {
                if (init.Children.Count > sym.Size) Error(d, $"too many initializers for '{sym.Name}[{sym.Size}]'");
                foreach (var e in init.Children)
                {
                    if (e.Kind == "InitList") Error(e, "nested initializer lists are not supported");
                    else Expr(e);
                    if (e.Kind != "InitList" && !IsConst(e)) Warn(e, "non-constant array initializer is evaluated at run time");
                }
            }
            else if (init.Kind == "Str")
            {
                if (init.Bytes!.Length + 1 > sym.Size) Error(d, $"string initializer too long for '{sym.Name}[{sym.Size}]'");
            }
            else Error(d, $"array '{sym.Name}' must be initialised with a list or string");
        }
        else
        {
            if (init.Kind is "InitList" or "Str") Error(d, $"cannot initialise scalar '{sym.Name}' with {(init.Kind == "Str" ? "a string" : "a list")}");
            else Expr(init);
        }
    }

    // ───────────── statements ─────────────

    void Stmt(AstNode s)
    {
        switch (s.Kind)
        {
            case "Block":
                Push();
                foreach (var c in s.Children) Stmt(c);
                Pop();
                break;
            case "VarDecl": VarDecl(s); break;
            case "ExprStmt": Expr(s.Children[0]); break;
            case "Empty": break;
            case "If":
                Expr(s.Children[0]);
                foreach (var c in s.Children.Skip(1)) ScopedStmt(c);
                break;
            case "While":
                Expr(s.Children[0]);
                LoopBody(s.Children[1]);
                break;
            case "DoWhile":
                LoopBody(s.Children[0]);
                Expr(s.Children[1]);
                break;
            case "For":
                Push();
                foreach (var c in s.Children[0].Children) Stmt(c);
                foreach (var c in s.Children[1].Children) Expr(c);
                foreach (var c in s.Children[2].Children) Expr(c);
                LoopBody(s.Children[3]);
                Pop();
                break;
            case "Return":
                Error(s, "'return' is only supported as the last statement of a function");
                break;
            case "Break": case "Continue":
                if (_loopDepth == 0) Error(s, $"'{s.Kind.ToLowerInvariant()}' outside of a loop");
                break;
            case "Unsupported":
                Error(s, $"{s.Text} is not supported by the Brainfuck backend");
                break;
            case "Prototype": break;
            default:
                Error(s, $"unexpected {s.Kind}");
                break;
        }
    }

    void ScopedStmt(AstNode s) { Push(); Stmt(s); Pop(); }

    void LoopBody(AstNode s) { _loopDepth++; ScopedStmt(s); _loopDepth--; }

    // ───────────── expressions ─────────────

    static bool IsConst(AstNode n) => n.Kind is "Num" or "Char";

    static void SetConst(AstNode n, int v)
    {
        v &= 0xFF;
        n.Kind = "Num"; n.Text = v.ToString(); n.Value = v; n.Children.Clear(); n.Folded = true; n.Symbol = null;
    }

    static void Replace(AstNode n, AstNode other)
    {
        n.Kind = other.Kind; n.Text = other.Text; n.Type = other.Type; n.Value = other.Value;
        n.Bytes = other.Bytes; n.Symbol = other.Symbol; n.Children = other.Children; n.Folded = true;
    }

    void Expr(AstNode n)
    {
        switch (n.Kind)
        {
            case "Num":
                if (int.TryParse(n.Text, out var big) && big > 255)
                    Warn(n, $"constant {big} does not fit an 8-bit cell, wraps to {n.Value}");
                break;
            case "Char": break;
            case "Str": Error(n, "string literals are only allowed as printf/puts arguments or array initialisers"); break;
            case "Unsupported": Error(n, $"{n.Text} is not supported by the Brainfuck backend"); break;
            case "Id":
            {
                var s = Lookup(n.Text!);
                if (s is null) { Error(n, $"'{n.Text}' is not declared"); break; }
                if (s.Kind == SymbolKind.Function) { Error(n, $"function '{n.Text}' used as a value"); break; }
                if (s.IsArray) { Error(n, $"array '{n.Text}' used as a value (pointers are not supported)"); break; }
                n.Symbol = s;
                break;
            }
            case "Index": Index(n); break;
            case "Cast":
                Expr(n.Children[0]);
                if (CheckType(n, n.Type, false) && IsConst(n.Children[0])) SetConst(n, n.Children[0].Value);
                break;
            case "Unary": Unary(n); break;
            case "BinOp": Binary(n); break;
            case "Ternary":
                foreach (var c in n.Children) Expr(c);
                if (IsConst(n.Children[0])) Replace(n, n.Children[0].Value != 0 ? n.Children[1] : n.Children[2]);
                break;
            case "Comma":
                foreach (var c in n.Children) Expr(c);
                break;
            case "Assign": Assign(n); break;
            case "PreInc": case "PostInc":
                Expr(n.Children[0]);
                RequireLvalue(n.Children[0]);
                break;
            case "Call": Call(n); break;
            default: Error(n, $"unexpected {n.Kind} in expression"); break;
        }
    }

    void Index(AstNode n)
    {
        var b = n.Children[0];
        Expr(n.Children[1]);
        if (b.Kind != "Id") { Error(n, "only named arrays can be indexed"); return; }
        var s = Lookup(b.Text!);
        if (s is null) { Error(b, $"'{b.Text}' is not declared"); return; }
        if (!s.IsArray) { Error(b, $"'{b.Text}' is not an array"); return; }
        b.Symbol = s;
        n.Symbol = s;
        if (!IsConst(n.Children[1]))
        {
            Error(n, "array indices must be compile-time constants (the tape has no computed addressing)");
            return;
        }
        if (n.Children[1].Value >= s.Size) Error(n, $"index {n.Children[1].Value} is out of bounds for '{s.Name}[{s.Size}]'");
    }

    void RequireLvalue(AstNode lhs)
    {
        if (lhs.Kind == "Id" || lhs.Kind == "Index") return;
        Error(lhs, "left side is not assignable");
    }

    void Assign(AstNode n)
    {
        Expr(n.Children[0]);
        Expr(n.Children[1]);
        RequireLvalue(n.Children[0]);
        var op = n.Text!;
        if (op is "<<=" or ">>=")
        {
            if (!IsConst(n.Children[1])) { Error(n, "shift amount must be a constant"); return; }
            var k = n.Children[1].Value;
            SetConst(n.Children[1], k >= 8 ? 0 : 1 << k);
            n.Text = op == "<<=" ? "*=" : "/=";
            if (op == ">>=" && k >= 8) { n.Text = "="; SetConst(n.Children[1], 0); }
        }
        else if (op is "&=" or "|=" or "^=")
            Error(n, $"'{op}' is not supported at run time (bitwise operators only fold constants)");
        else if ((op is "/=" or "%=") && IsConst(n.Children[1]) && n.Children[1].Value == 0)
            Error(n, "division by zero");
    }

    void Unary(AstNode n)
    {
        Expr(n.Children[0]);
        var c = n.Children[0];
        switch (n.Text)
        {
            case "&": case "*": Error(n, "pointers are not supported by the Brainfuck backend"); return;
        }
        if (!IsConst(c)) return;
        var v = c.Value;
        SetConst(n, n.Text switch { "-" => -v, "+" => v, "~" => ~v, "!" => v == 0 ? 1 : 0, _ => v });
    }

    void Binary(AstNode n)
    {
        var l = n.Children[0]; var r = n.Children[1];
        Expr(l); Expr(r);
        var op = n.Text!;

        if (IsConst(l) && IsConst(r))
        {
            int a = l.Value, b = r.Value;
            if (op is "/" or "%" && b == 0) { Error(n, "division by zero in constant expression"); return; }
            SetConst(n, op switch
            {
                "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, "%" => a % b,
                "<" => a < b ? 1 : 0, ">" => a > b ? 1 : 0, "<=" => a <= b ? 1 : 0, ">=" => a >= b ? 1 : 0,
                "==" => a == b ? 1 : 0, "!=" => a != b ? 1 : 0,
                "&&" => a != 0 && b != 0 ? 1 : 0, "||" => a != 0 || b != 0 ? 1 : 0,
                "&" => a & b, "|" => a | b, "^" => a ^ b,
                "<<" => b >= 8 ? 0 : a << b, ">>" => b >= 8 ? 0 : a >> b,
                _ => 0,
            });
            return;
        }

        switch (op)
        {
            case "<<" or ">>":
                if (!IsConst(r)) { Error(n, "shift amount must be a constant"); return; }
                if (r.Value >= 8) { SetConst(n, 0); return; }
                n.Text = op == "<<" ? "*" : "/";
                SetConst(r, 1 << r.Value);
                break;
            case "&" or "|" or "^":
                Error(n, $"'{op}' is not supported at run time (bitwise operators only fold constants)");
                break;
            case "/" or "%" when IsConst(r) && r.Value == 0:
                Error(n, "division by zero");
                break;
        }
    }

    void Call(AstNode n)
    {
        var name = n.Text!;
        var args = n.Children;
        foreach (var a in args.Where(a => a.Kind != "Str")) Expr(a);

        void NoStrings(IEnumerable<AstNode> list) { foreach (var a in list) if (a.Kind == "Str") Error(a, "string argument not allowed here"); }

        switch (name)
        {
            case "putchar":
                if (args.Count != 1) Error(n, "putchar expects 1 argument");
                NoStrings(args);
                return;
            case "getchar":
                if (args.Count != 0) Error(n, "getchar expects no arguments");
                return;
            case "puts":
                if (args.Count != 1 || args[0].Kind != "Str") Error(n, "puts expects a string literal");
                return;
            case "printf":
                if (args.Count == 0 || args[0].Kind != "Str") { Error(n, "printf expects a string literal format"); return; }
                NoStrings(args.Skip(1));
                var need = Format.Parse(args[0].Bytes!).Count(p => p.Spec != '\0');
                if (need != args.Count - 1) Error(n, $"printf format needs {need} argument(s), {args.Count - 1} given");
                foreach (var p in Format.Parse(args[0].Bytes!))
                    if (p.Spec is not '\0' and not ('d' or 'u' or 'i' or 'c'))
                        Error(n, $"printf conversion '%{(char)p.Spec}' is not supported (use %d %u %c)");
                return;
        }

        var s = Lookup(name);
        if (s is null) { Error(n, $"function '{name}' is not declared (only functions defined earlier in the file can be called)"); return; }
        if (s.Kind != SymbolKind.Function) { Error(n, $"'{name}' is not a function"); return; }
        if (s == _currentFunc) { Error(n, "recursion is not supported (functions are inlined at call sites)"); return; }
        NoStrings(args);
        if (args.Count != s.Params.Count) Error(n, $"'{name}' expects {s.Params.Count} argument(s), {args.Count} given");
        n.Symbol = s;
    }
}

/// <summary>printf format splitter shared by the checker and the code generator.</summary>
public static class Format
{
    /// <summary>Spec == 0 means a literal byte (in Literal); otherwise a conversion character.</summary>
    public readonly record struct Piece(byte Literal, int Spec);

    public static List<Piece> Parse(byte[] fmt)
    {
        var r = new List<Piece>();
        for (var i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%') { r.Add(new(fmt[i], 0)); continue; }
            i++;
            if (i >= fmt.Length) { r.Add(new((byte)'%', 0)); break; }
            if (fmt[i] == '%') r.Add(new((byte)'%', 0));
            else r.Add(new(0, fmt[i]));
        }
        return r;
    }
}
