using System.Text;
using System.Text.RegularExpressions;
using CCompiler.Core.Ast;
using CCompiler.Core.Semantics;

namespace CCompiler.Core.CodeGen;

/// <summary>
/// AST → Brainfuck. The tape is split in two regions: variable cells (static addresses assigned by the checker)
/// followed by a scratch stack. Every expression is evaluated into a fresh scratch cell (<see cref="Eval"/>);
/// scratch cells are always zero when allocated and are zeroed before being released.
/// The generator tracks the data pointer at compile time, so every loop body ends where it started.
/// All arithmetic is unsigned 8-bit (cells wrap at 256).
/// </summary>
public sealed class BrainfuckGen
{
    readonly StringBuilder _sb = new();
    readonly ProgramInfo _info;
    readonly string[] _source;
    readonly bool _annotate;
    int _ptr;          // where the real data pointer is after the code emitted so far
    int _target;       // where the next instruction wants it to be (lazy moves)
    int _top;          // next free scratch cell
    Symbol? _fn;       // function whose body is being emitted (for tail return)
    LoopCtx? _loop;    // innermost loop that contains break/continue

    /// <summary>break/continue are compiled to flags: statements after a jump are guarded by !skip, the loop test by !brk.</summary>
    sealed record LoopCtx(int Skip, int Brk);

    BrainfuckGen(ProgramInfo info, string[] source, bool annotate)
    {
        _info = info; _source = source; _annotate = annotate;
        _top = info.VarCells;
    }

    public static string Generate(AstNode program, ProgramInfo info, string[] sourceLines, bool annotate)
    {
        if (info.Main is null) throw new CompileException("no main");
        var g = new BrainfuckGen(info, sourceLines, annotate);
        foreach (var d in program.Children.Where(c => c.Kind == "VarDecl")) g.Stmt(d);
        g.Note("main");
        g.InlineBody(info.Main);
        return g.Finish();
    }

    // ───────────── emission primitives ─────────────

    string Finish()
    {
        var code = _sb.ToString();
        // peephole: adjacent inverse operations cancel
        string prev;
        do { prev = code; code = Regex.Replace(code, @"\+-|-\+|<>|><", ""); } while (code != prev);
        return Wrap(code).TrimStart('\n').TrimEnd() + "\n";
    }

    static string Wrap(string s)
    {
        var sb = new StringBuilder();
        foreach (var line in s.Split('\n'))
        {
            if (line.StartsWith("//")) { sb.Append(line).Append('\n'); continue; }
            for (var i = 0; i < line.Length; i += 100) sb.Append(line, i, Math.Min(100, line.Length - i)).Append('\n');
        }
        return sb.ToString();
    }

    void Op(string s)
    {
        var d = _target - _ptr;
        if (d > 0) _sb.Append('>', d); else if (d < 0) _sb.Append('<', -d);
        _ptr = _target;
        _sb.Append(s);
    }

    void Goto(int cell) => _target = cell;

    static readonly Dictionary<char, char> Lookalike = new()
    {
        ['+'] = '＋', ['-'] = '−', ['<'] = '‹', ['>'] = '›', ['['] = '［', [']'] = '］', ['.'] = '．', [','] = '，',
    };

    void Note(string text)
    {
        if (!_annotate) return;
        var clean = new string(text.Select(c => Lookalike.GetValueOrDefault(c, c)).ToArray());
        _sb.Append("\n// ").Append(clean.Replace('\n', ' ')).Append('\n');
    }

    void NoteLine(int line)
    {
        if (!_annotate || line < 1 || line > _source.Length) return;
        Note($"L{line}: {_source[line - 1].Trim()}");
    }

    /// <summary>Raw signed add (no wrap tricks).</summary>
    void AddRaw(int cell, int n)
    {
        if (n == 0) return;
        Goto(cell);
        Op(new string(n > 0 ? '+' : '-', Math.Abs(n)));
    }

    /// <summary>Add a constant, using a multiply loop for big deltas to keep the output readable.</summary>
    void Add(int cell, int n)
    {
        n = ((n + 128) & 0xFF) - 128;                   // -128..127
        if (Math.Abs(n) < 10) { AddRaw(cell, n); return; }
        var abs = Math.Abs(n);
        var a = (int)Math.Round(Math.Sqrt(abs));
        var b = abs / a;
        var r = abs % a;
        var sign = Math.Sign(n);
        var u = Alloc();
        AddRaw(u, a);
        Loop(u, () => { AddRaw(u, -1); AddRaw(cell, sign * b); });
        AddRaw(cell, sign * r);
        Free(u);
    }

    void Zero(int cell) { Goto(cell); Op("[-]"); }

    void SetConst(int cell, int n) { Zero(cell); Add(cell, n); }

    void Loop(int cell, Action body)
    {
        Goto(cell); Op("[");
        body();
        Goto(cell); Op("]");
    }

    /// <summary>dst += src; src = 0.</summary>
    void Move(int src, int dst) => Loop(src, () => { AddRaw(src, -1); AddRaw(dst, 1); });

    /// <summary>dst += sign * src; src unchanged.</summary>
    void AddCopy(int src, int dst, int sign = 1)
    {
        var t = Alloc();
        Loop(src, () => { AddRaw(src, -1); AddRaw(dst, sign); AddRaw(t, 1); });
        Move(t, src);
        Free(t);
    }

    /// <summary>if (cond) then else els. Consumes cond (left at zero).</summary>
    void IfElse(int cond, Action then, Action? els)
    {
        if (els is null)
        {
            Loop(cond, () => { Zero(cond); then(); });
            return;
        }
        var ef = Alloc();
        AddRaw(ef, 1);
        Loop(cond, () => { Zero(cond); AddRaw(ef, -1); then(); });
        Loop(ef, () => { AddRaw(ef, -1); els(); });
        Free(ef);
    }

    int Alloc() => _top++;

    void Free(int cell)
    {
        if (cell != _top - 1) throw new InvalidOperationException($"scratch stack corrupted (freeing {cell}, top {_top})");
        _top--;
    }

    void Discard(int cell) { Zero(cell); Free(cell); }

    /// <summary>x = !x (strict 0/1 result).</summary>
    void Invert(int cell)
    {
        var t = Alloc();
        AddRaw(t, 1);
        Loop(cell, () => { Zero(cell); AddRaw(t, -1); });
        Move(t, cell);
        Free(t);
    }

    /// <summary>x = (x != 0).</summary>
    void Normalize(int cell)
    {
        var t = Alloc();
        Loop(cell, () => { Zero(cell); AddRaw(t, 1); });
        Move(t, cell);
        Free(t);
    }

    // ───────────── arithmetic helpers ─────────────

    /// <summary>Fresh cell = (a &lt; b), unsigned. a and b are left untouched.</summary>
    int Lt(int a, int b)
    {
        var res = Alloc(); var ca = Alloc(); var cb = Alloc(); var f = Alloc();
        AddCopy(a, ca); AddCopy(b, cb); AddRaw(f, 1);
        Loop(f, () =>
        {
            var c1 = Alloc();
            AddCopy(ca, c1);
            IfElse(c1,
                then: () =>
                {
                    var c2 = Alloc();
                    AddCopy(cb, c2);
                    IfElse(c2, () => { AddRaw(ca, -1); AddRaw(cb, -1); }, () => Zero(f));
                    Free(c2);
                },
                els: () =>
                {
                    Zero(f);
                    var c3 = Alloc();
                    AddCopy(cb, c3);
                    IfElse(c3, () => AddRaw(res, 1), null);
                    Free(c3);
                });
            Free(c1);
        });
        Discard(f); Discard(cb); Discard(ca);
        return res;
    }

    /// <summary>f = (y != 0 &amp;&amp; x &gt;= y)</summary>
    void DivCond(int f, int x, int y)
    {
        Zero(f);
        var g = Alloc();
        AddCopy(y, g);
        IfElse(g, () =>
        {
            var lt = Lt(x, y);
            Invert(lt);
            Move(lt, f);
            Free(lt);
        }, null);
        Free(g);
    }

    /// <summary>x = x mod y; q += x div y; y unchanged. Dividing by zero yields q = 0, x unchanged.</summary>
    void DivMod(int x, int y, int q)
    {
        var f = Alloc();
        DivCond(f, x, y);
        Loop(f, () =>
        {
            AddCopy(y, x, -1);
            AddRaw(q, 1);
            DivCond(f, x, y);
        });
        Free(f);
    }

    /// <summary>x = x mod k; returns fresh cell with x div k.</summary>
    int DivModConst(int x, int k)
    {
        var q = Alloc(); var y = Alloc();
        SetConst(y, k);
        DivMod(x, y, q);
        Discard(y);
        return q;
    }

    void PrintDigit(int cell)
    {
        AddRaw(cell, 48);
        Goto(cell); Op(".");
        AddRaw(cell, -48);
    }

    /// <summary>Print an unsigned 8-bit value in decimal. Destroys the value (leaves the last digit).</summary>
    void PrintU8(int v)
    {
        var h = DivModConst(v, 100);
        var t = DivModConst(v, 10);
        var hc = Alloc();
        AddCopy(h, hc);
        IfElse(hc, () => PrintDigit(h), null);
        Free(hc);
        var any = Alloc();
        AddCopy(h, any); AddCopy(t, any);
        IfElse(any, () => PrintDigit(t), null);
        Free(any);
        PrintDigit(v);
        Discard(t); Discard(h);
    }

    void EmitBytes(IEnumerable<byte> bytes)
    {
        var list = bytes.ToList();
        if (list.Count == 0) return;
        var t = Alloc();
        var cur = 0;
        foreach (var b in list)
        {
            Add(t, b - cur);
            cur = b;
            Goto(t); Op(".");
        }
        Discard(t);
    }

    // ───────────── expressions ─────────────

    int CellOf(AstNode lv) => lv.Kind == "Index" ? lv.Symbol!.Cell + lv.Children[1].Value : lv.Symbol!.Cell;

    /// <summary>Evaluates e into a fresh scratch cell (top of the scratch stack) and returns it.</summary>
    int Eval(AstNode e)
    {
        switch (e.Kind)
        {
            case "Num": case "Char":
            {
                var t = Alloc();
                Add(t, e.Value);
                return t;
            }
            case "Id": case "Index":
            {
                var t = Alloc();
                AddCopy(CellOf(e), t);
                return t;
            }
            case "Cast": return Eval(e.Children[0]);
            case "Unary": return EvalUnary(e);
            case "BinOp": return EvalBinary(e);
            case "Ternary":
            {
                var res = Alloc();
                var c = Eval(e.Children[0]);
                IfElse(c,
                    () => { var v = Eval(e.Children[1]); Move(v, res); Free(v); },
                    () => { var v = Eval(e.Children[2]); Move(v, res); Free(v); });
                Free(c);
                return res;
            }
            case "Comma":
            {
                for (var i = 0; i < e.Children.Count - 1; i++) ExecExpr(e.Children[i]);
                return Eval(e.Children[^1]);
            }
            case "Assign":
            {
                AssignCore(e);
                var t = Alloc();
                AddCopy(CellOf(e.Children[0]), t);
                return t;
            }
            case "PreInc":
            {
                var cell = CellOf(e.Children[0]);
                AddRaw(cell, e.Text == "++" ? 1 : -1);
                var t = Alloc();
                AddCopy(cell, t);
                return t;
            }
            case "PostInc":
            {
                var cell = CellOf(e.Children[0]);
                var t = Alloc();
                AddCopy(cell, t);
                AddRaw(cell, e.Text == "++" ? 1 : -1);
                return t;
            }
            case "Call": return EvalCall(e);
            default: throw new CompileException($"cannot generate code for {e.Kind}", e.Line);
        }
    }

    int EvalUnary(AstNode e)
    {
        var l = Eval(e.Children[0]);
        switch (e.Text)
        {
            case "+": break;
            case "-":
            {
                var r = Alloc();
                Loop(l, () => { AddRaw(l, -1); AddRaw(r, -1); });
                Move(r, l);
                Free(r);
                break;
            }
            case "~":
            {
                var r = Alloc();
                AddRaw(r, -1);
                Loop(l, () => { AddRaw(l, -1); AddRaw(r, -1); });
                Move(r, l);
                Free(r);
                break;
            }
            case "!": Invert(l); break;
            default: throw new CompileException($"unary '{e.Text}' not supported", e.Line);
        }
        return l;
    }

    int EvalBinary(AstNode e)
    {
        var op = e.Text!;
        if (op is "&&" or "||")
        {
            var res = Alloc();
            var l = Eval(e.Children[0]);
            void EvalRhs()
            {
                var r = Eval(e.Children[1]);
                Loop(r, () => { Zero(r); AddRaw(res, 1); });
                Free(r);
            }
            if (op == "&&") IfElse(l, EvalRhs, null);
            else IfElse(l, () => AddRaw(res, 1), EvalRhs);
            Free(l);
            return res;
        }
        var a = Eval(e.Children[0]);
        var b = Eval(e.Children[1]);
        ApplyBinary(op, a, b, e.Line);
        return a;
    }

    /// <summary>a = a op b; b is zeroed and released (it must be the top scratch cell).</summary>
    void ApplyBinary(string op, int a, int b, int line)
    {
        switch (op)
        {
            case "+": Move(b, a); break;
            case "-": Loop(b, () => { AddRaw(b, -1); AddRaw(a, -1); }); break;
            case "*":
            {
                var z = Alloc(); var w = Alloc();
                Loop(a, () =>
                {
                    AddRaw(a, -1);
                    Loop(b, () => { AddRaw(b, -1); AddRaw(z, 1); AddRaw(w, 1); });
                    Move(w, b);
                });
                Zero(b);
                Move(z, a);
                Free(w); Free(z);
                break;
            }
            case "/": case "%":
            {
                var q = Alloc();
                DivMod(a, b, q);
                if (op == "/") { Zero(a); Move(q, a); }
                else Zero(q);
                Free(q);
                Zero(b);
                break;
            }
            case "==": case "!=":
            {
                Loop(b, () => { AddRaw(b, -1); AddRaw(a, -1); });   // a = a - b
                if (op == "==") Invert(a); else Normalize(a);
                break;
            }
            case "<": case ">": case "<=": case ">=":
            {
                var res = op is "<" or ">=" ? Lt(a, b) : Lt(b, a);
                if (op is "<=" or ">=") Invert(res);
                Zero(a); Zero(b);
                Move(res, a);
                Free(res);
                break;
            }
            default: throw new CompileException($"operator '{op}' not supported at run time", line);
        }
        Free(b);
    }

    int EvalCall(AstNode e)
    {
        switch (e.Text)
        {
            case "putchar":
            {
                var v = Eval(e.Children[0]);
                Goto(v); Op(".");
                return v;
            }
            case "getchar":
            {
                var t = Alloc();
                Goto(t); Op(",");
                return t;
            }
            case "puts":
                EmitBytes(e.Children[0].Bytes!.Append((byte)'\n'));
                return Alloc();
            case "printf":
            {
                var pending = new List<byte>();
                var arg = 1;
                foreach (var p in Format.Parse(e.Children[0].Bytes!))
                {
                    if (p.Spec == 0) { pending.Add(p.Literal); continue; }
                    var a = e.Children[arg++];
                    if (a.Kind is "Num" or "Char")   // folded at compile time: no runtime printing code needed
                    {
                        pending.AddRange(Encoding.ASCII.GetBytes(p.Spec == 'c' ? ((char)a.Value).ToString() : a.Value.ToString()));
                        continue;
                    }
                    EmitBytes(pending); pending.Clear();
                    var v = Eval(a);
                    if (p.Spec == 'c') { Goto(v); Op("."); }
                    else PrintU8(v);
                    Discard(v);
                }
                EmitBytes(pending);
                return Alloc();
            }
        }

        // user function: evaluate every argument first (an argument may call the same function), then bind and inline
        var fn = e.Symbol!;
        var temps = e.Children.Select(Eval).ToList();
        for (var i = temps.Count - 1; i >= 0; i--)
        {
            var p = fn.Params[i].Cell;
            Zero(p);
            Move(temps[i], p);
            Free(temps[i]);
        }
        InlineBody(fn);
        var res = Alloc();
        if (fn.ReturnCell is not null) Move(fn.ReturnCell.Cell, res);
        return res;
    }

    // ───────────── statements ─────────────

    void InlineBody(Symbol fn)
    {
        var saved = (_fn, _loop);
        (_fn, _loop) = (fn, null);
        if (fn.ReturnCell is not null) Zero(fn.ReturnCell.Cell);
        StmtList(fn.Definition!.Children[1].Children);
        (_fn, _loop) = saved;
    }

    void ExecExpr(AstNode e)
    {
        switch (e.Kind)
        {
            case "Assign": AssignCore(e); break;
            case "PreInc": case "PostInc":
                AddRaw(CellOf(e.Children[0]), e.Text == "++" ? 1 : -1);
                break;
            case "Comma":
                foreach (var c in e.Children) ExecExpr(c);
                break;
            default: Discard(Eval(e)); break;
        }
    }

    void AssignCore(AstNode e)
    {
        var cell = CellOf(e.Children[0]);
        var rhs = e.Children[1];
        switch (e.Text)
        {
            case "=":
            {
                if (rhs.Kind is "Num" or "Char") { SetConst(cell, rhs.Value); break; }
                var v = Eval(rhs);
                Zero(cell);
                Move(v, cell);
                Free(v);
                break;
            }
            case "+=": { var v = Eval(rhs); Move(v, cell); Free(v); break; }
            case "-=":
            {
                var v = Eval(rhs);
                Loop(v, () => { AddRaw(v, -1); AddRaw(cell, -1); });
                Free(v);
                break;
            }
            default:
            {
                var l = Alloc();
                AddCopy(cell, l);
                var r = Eval(rhs);
                ApplyBinary(e.Text![..^1], l, r, e.Line);
                Zero(cell);
                Move(l, cell);
                Free(l);
                break;
            }
        }
    }

    void Cond(AstNode? cond, int into)
    {
        if (cond is null) { SetConst(into, 1); return; }
        var v = Eval(cond);
        Zero(into);
        Move(v, into);
        Free(v);
    }

    void Stmt(AstNode s)
    {
        if (s.Kind is not ("Block" or "Empty")) NoteLine(s.Line);
        switch (s.Kind)
        {
            case "Block": StmtList(s.Children); break;
            case "Break":
                SetConst(_loop!.Brk, 1);
                SetConst(_loop.Skip, 1);
                break;
            case "Continue": SetConst(_loop!.Skip, 1); break;
            case "Empty": break;
            case "VarDecl": VarDecl(s); break;
            case "ExprStmt": ExecExpr(s.Children[0]); break;
            case "If":
            {
                var c = Eval(s.Children[0]);
                IfElse(c, () => Stmt(s.Children[1]), s.Children.Count > 2 ? () => Stmt(s.Children[2]) : null);
                Free(c);
                break;
            }
            case "While":
            {
                var f = Alloc();
                Cond(s.Children[0], f);
                RunLoop(s.Children[1], f, s.Children[0], null);
                Free(f);
                break;
            }
            case "DoWhile":
            {
                var f = Alloc();
                AddRaw(f, 1);
                RunLoop(s.Children[0], f, s.Children[1], null);
                Free(f);
                break;
            }
            case "For":
            {
                foreach (var i in s.Children[0].Children) Stmt(i);
                var f = Alloc();
                var cond = s.Children[1].Children.FirstOrDefault();
                Cond(cond, f);
                RunLoop(s.Children[3], f, cond, () => { foreach (var u in s.Children[2].Children) ExecExpr(u); });
                Free(f);
                break;
            }
            case "Return":
                if (s.Children.Count == 1)
                {
                    var v = Eval(s.Children[0]);
                    if (_fn?.ReturnCell is { } rc) { Zero(rc.Cell); Move(v, rc.Cell); }
                    else Zero(v);
                    Free(v);
                }
                break;
            default: throw new CompileException($"cannot generate code for {s.Kind}", s.Line);
        }
    }

    static bool HasJump(AstNode n) =>
        n.Kind is "Break" or "Continue" || (n.Kind is not ("While" or "DoWhile" or "For") && n.Children.Any(HasJump));

    /// <summary>Emits `f[ body; update; f = cond ]`. f already holds the first condition value.</summary>
    void RunLoop(AstNode body, int f, AstNode? cond, Action? update)
    {
        var ctx = HasJump(body) ? new LoopCtx(Alloc(), Alloc()) : null;
        var saved = _loop;
        _loop = ctx;
        Loop(f, () =>
        {
            Stmt(body);
            if (ctx is not null) Zero(ctx.Skip);
            if (update is not null)
            {
                if (ctx is null) update();
                else
                {
                    var c = Alloc();
                    AddCopy(ctx.Brk, c);
                    Invert(c);
                    IfElse(c, update, null);
                    Free(c);
                }
            }
            Cond(cond, f);
            if (ctx is not null)
            {
                var c = Alloc();
                AddCopy(ctx.Brk, c);
                IfElse(c, () => Zero(f), null);
                Free(c);
            }
        });
        _loop = saved;
        if (ctx is not null) { Discard(ctx.Brk); Discard(ctx.Skip); }
    }

    void StmtList(IReadOnlyList<AstNode> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            Stmt(list[i]);
            if (_loop is not null && i + 1 < list.Count && HasJump(list[i]))
            {
                var c = Alloc();
                AddCopy(_loop.Skip, c);
                Invert(c);
                var rest = list.Skip(i + 1).ToList();
                IfElse(c, () => StmtList(rest), null);
                Free(c);
                return;
            }
        }
    }

    void VarDecl(AstNode d)
    {
        var sym = d.Symbol!;
        var init = d.Children.FirstOrDefault(c => c.Kind == "Init")?.Children[0];
        if (init is null) return;
        if (!sym.IsArray)
        {
            if (init.Kind is "Num" or "Char") { SetConst(sym.Cell, init.Value); return; }
            var v = Eval(init);
            Zero(sym.Cell);
            Move(v, sym.Cell);
            Free(v);
            return;
        }
        for (var i = 0; i < sym.Size; i++)
        {
            var cell = sym.Cell + i;
            if (init.Kind == "Str")
                SetConst(cell, i < init.Bytes!.Length ? init.Bytes[i] : 0);
            else if (i < init.Children.Count)
            {
                var e = init.Children[i];
                if (e.Kind is "Num" or "Char") SetConst(cell, e.Value);
                else { var v = Eval(e); Zero(cell); Move(v, cell); Free(v); }
            }
            else SetConst(cell, 0);
        }
    }
}
