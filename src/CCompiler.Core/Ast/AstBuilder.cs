using System.Globalization;
using System.Text;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using CCompiler.Core.Parsing;

namespace CCompiler.Core.Ast;

/// <summary>
/// Converts the verbose ANTLR parse tree (one rule per precedence level) into a compact AST.
/// Anything the BF backend cannot handle becomes an "Unsupported" node, reported later by the checker,
/// so the full C grammar always parses even when code generation is impossible.
/// </summary>
public sealed class AstBuilder
{
    public static AstNode Build(CParser.CompilationUnitContext unit) => new AstBuilder().Program(unit);

    static string? Tok(IParseTree t) => t is ITerminalNode n ? n.Symbol.Text : null;
    static int LineOf(ParserRuleContext c) => c.Start?.Line ?? 0;

    static AstNode Unsupported(ParserRuleContext c, string what) =>
        new("Unsupported", what, LineOf(c));

    // ───────────── declarations ─────────────

    AstNode Program(CParser.CompilationUnitContext unit)
    {
        var program = new AstNode("Program", null, 1);
        var tu = unit.translationUnit();
        if (tu is null) return program;
        foreach (var ed in tu.externalDeclaration())
        {
            if (ed.functionDefinition() is { } f) program.Children.Add(FuncDef(f));
            else if (ed.declaration() is { } d) program.Children.AddRange(Declaration(d));
        }
        return program;
    }

    sealed class DeclInfo
    {
        public string Name = "";
        public int Pointers;
        public List<CParser.AssignmentExpressionContext?> Dims = [];
        public bool IsFunction;
        public CParser.ParameterTypeListContext? Params;
        public string? Bad;
    }

    DeclInfo Declarator(CParser.DeclaratorContext d)
    {
        var info = DirectDeclarator(d.directDeclarator());
        if (d.pointer() is { } p) info.Pointers = p.GetText().Count(ch => ch == '*' || ch == '^');
        return info;
    }

    DeclInfo DirectDeclarator(CParser.DirectDeclaratorContext dd)
    {
        var first = dd.GetChild(0);
        if (dd.ChildCount == 1 && Tok(first) is { } id) return new DeclInfo { Name = id };
        if (first is CParser.DirectDeclaratorContext inner)
        {
            var info = DirectDeclarator(inner);
            var t = Tok(dd.GetChild(1));
            if (t == "[") info.Dims.Add(dd.assignmentExpression());
            else if (t == "(") { info.IsFunction = true; info.Params = dd.parameterTypeList(); }
            return info;
        }
        if (Tok(first) == "(" && dd.declarator() is { } nested) return Declarator(nested);
        return new DeclInfo { Name = dd.GetText(), Bad = "exotic declarator" };
    }

    static string TypeName(IEnumerable<CParser.DeclarationSpecifierContext> specs, out string? bad)
    {
        bad = null;
        var words = new List<string>();
        foreach (var s in specs)
        {
            if (s.storageClassSpecifier() is { } sc && sc.GetText() == "typedef") bad = "typedef";
            if (s.typeSpecifier() is { } ts)
            {
                if (ts.structOrUnionSpecifier() is not null) { bad = "struct/union"; words.Add("struct"); }
                else if (ts.enumSpecifier() is not null) { bad = "enum"; words.Add("enum"); }
                else words.Add(ts.GetText());
            }
        }
        return string.Join(' ', words);
    }

    IEnumerable<AstNode> Declaration(CParser.DeclarationContext d)
    {
        if (d.staticAssertDeclaration() is not null) { yield return Unsupported(d, "_Static_assert"); yield break; }
        foreach (var n in DeclFrom(d.declarationSpecifiers(), d.initDeclaratorList(), d)) yield return n;
    }

    IEnumerable<AstNode> DeclFrom(CParser.DeclarationSpecifiersContext specs,
        CParser.InitDeclaratorListContext? list, ParserRuleContext where)
    {
        var specList = specs.declarationSpecifier();
        var type = TypeName(specList, out var bad);
        if (list is null)
        {
            // Predicate-free grammar: in `int i;` the identifier is greedily taken as a typedef-name
            // (the classic C ambiguity). A typedef-name that follows a real type keyword is the declarator.
            if (specList.Length >= 2 && specList[^1].typeSpecifier()?.typedefName() is { } tn
                && specList[..^1].Any(x => x.typeSpecifier() is { } t && t.typedefName() is null))
            {
                var t2 = TypeName(specList[..^1], out var bad2);
                var node = new AstNode("VarDecl", tn.GetText(), LineOf(where)) { Type = t2 };
                if (bad2 is not null) yield return Unsupported(where, bad2); else yield return node;
                yield break;
            }
            if (bad is not null) yield return Unsupported(where, bad);
            yield break;
        }
        foreach (var idl in list.initDeclarator())
        {
            var info = Declarator(idl.declarator());
            var line = LineOf(idl);
            if (info.IsFunction) { yield return new AstNode("Prototype", info.Name, line) { Type = type }; continue; }
            if (bad is not null) { yield return Unsupported(idl, bad); continue; }
            if (info.Bad is not null) { yield return Unsupported(idl, info.Bad); continue; }
            if (info.Dims.Count > 1) { yield return Unsupported(idl, "multi-dimensional array"); continue; }

            var node = new AstNode("VarDecl", info.Name, line) { Type = type + new string('*', info.Pointers) };
            if (info.Dims.Count == 1)
            {
                var size = new AstNode("ArraySize", null, line);
                if (info.Dims[0] is { } dim) size.Children.Add(Expr(dim));
                node.Children.Add(size);
            }
            if (idl.initializer() is { } init)
                node.Children.Add(new AstNode("Init", null, line, Initializer(init)));
            yield return node;
        }
    }

    AstNode Initializer(CParser.InitializerContext init)
    {
        if (init.assignmentExpression() is { } e) return Expr(e);
        var list = new AstNode("InitList", null, LineOf(init));
        if (init.initializerList() is { } il)
            foreach (var i in il.initializer())
                list.Children.Add(Initializer(i));
        return list;
    }

    AstNode FuncDef(CParser.FunctionDefinitionContext f)
    {
        var info = Declarator(f.declarator());
        var ret = f.declarationSpecifiers() is { } ds
            ? TypeName(ds.declarationSpecifier(), out _) + new string('*', info.Pointers)
            : "int";
        var node = new AstNode("FuncDef", info.Name, LineOf(f)) { Type = ret };
        var ps = new AstNode("Params", null, LineOf(f));
        if (info.Params is { } ptl)
            foreach (var pd in ptl.parameterList().parameterDeclaration())
            {
                if (pd.declarator() is { } pdecl)
                {
                    var pi = Declarator(pdecl);
                    var pt = TypeName(pd.declarationSpecifiers().declarationSpecifier(), out _) + new string('*', pi.Pointers);
                    if (pi.Dims.Count > 0) pt += "[]";
                    ps.Children.Add(new AstNode("Param", pi.Name, LineOf(pd)) { Type = pt });
                }
                else
                {
                    var pt = TypeName(pd.declarationSpecifiers2().declarationSpecifier(), out _);
                    if (pt != "void" || pd.abstractDeclarator() is not null)
                        ps.Children.Add(new AstNode("Param", "", LineOf(pd)) { Type = pt });
                }
            }
        node.Children.Add(ps);
        node.Children.Add(Compound(f.compoundStatement()));
        return node;
    }

    // ───────────── statements ─────────────

    AstNode Compound(CParser.CompoundStatementContext c)
    {
        var block = new AstNode("Block", null, LineOf(c));
        if (c.blockItemList() is { } list)
            foreach (var item in list.blockItem())
            {
                if (item.statement() is { } s) block.Children.Add(Stmt(s));
                else if (item.declaration() is { } d) block.Children.AddRange(Declaration(d));
            }
        return block;
    }

    AstNode Stmt(CParser.StatementContext s)
    {
        var line = LineOf(s);
        switch (s.GetChild(0))
        {
            case CParser.CompoundStatementContext c: return Compound(c);
            case CParser.ExpressionStatementContext es:
                return es.expression() is { } e ? new AstNode("ExprStmt", null, line, Expr(e)) : new AstNode("Empty", null, line);
            case CParser.SelectionStatementContext sel:
                if (Tok(sel.GetChild(0)) == "switch") return Unsupported(sel, "switch");
                var node = new AstNode("If", null, line, Expr(sel.expression()), Stmt(sel.statement(0)));
                if (sel.statement().Length > 1) node.Children.Add(Stmt(sel.statement(1)));
                return node;
            case CParser.IterationStatementContext it: return Iteration(it);
            case CParser.JumpStatementContext j:
                switch (Tok(j.GetChild(0)))
                {
                    case "return":
                        var r = new AstNode("Return", null, line);
                        if (j.expression() is { } re) r.Children.Add(Expr(re));
                        return r;
                    case "break": return new AstNode("Break", null, line);
                    case "continue": return new AstNode("Continue", null, line);
                    default: return Unsupported(j, "goto");
                }
            case CParser.LabeledStatementContext l: return Unsupported(l, "labels / case");
            default: return Unsupported(s, "inline asm");
        }
    }

    AstNode Iteration(CParser.IterationStatementContext it)
    {
        var line = LineOf(it);
        switch (Tok(it.GetChild(0)))
        {
            case "while":
                return new AstNode("While", null, line, Expr(it.expression()), Stmt(it.statement()));
            case "do":
                return new AstNode("DoWhile", null, line, Stmt(it.statement()), Expr(it.expression()));
            default:
                var fc = it.forCondition();
                var segs = new List<List<IParseTree>> { new() };
                for (var i = 0; i < fc.ChildCount; i++)
                {
                    if (Tok(fc.GetChild(i)) == ";") segs.Add([]);
                    else segs[^1].Add(fc.GetChild(i));
                }
                var init = new AstNode("Init", null, line);
                foreach (var t in segs[0])
                {
                    if (t is CParser.ForDeclarationContext fd)
                        init.Children.AddRange(DeclFrom(fd.declarationSpecifiers(), fd.initDeclaratorList(), fd));
                    else if (t is CParser.ExpressionContext e)
                        init.Children.Add(new AstNode("ExprStmt", null, line, Expr(e)));
                }
                var cond = new AstNode("Cond", null, line);
                var update = new AstNode("Update", null, line);
                foreach (var t in segs.ElementAtOrDefault(1) ?? [])
                    if (t is CParser.ForExpressionContext fe) cond.Children.Add(ForExpr(fe));
                foreach (var t in segs.ElementAtOrDefault(2) ?? [])
                    if (t is CParser.ForExpressionContext fe) update.Children.Add(ForExpr(fe));
                return new AstNode("For", null, line, init, cond, update, Stmt(it.statement()));
        }
    }

    AstNode ForExpr(CParser.ForExpressionContext fe)
    {
        var parts = fe.assignmentExpression().Select(Expr).ToList();
        return parts.Count == 1 ? parts[0] : new AstNode("Comma", null, LineOf(fe), parts.ToArray());
    }

    // ───────────── expressions ─────────────

    AstNode Expr(IParseTree t)
    {
        switch (t)
        {
            case CParser.ExpressionContext e:
            {
                var parts = e.assignmentExpression().Select(Expr).ToList();
                return parts.Count == 1 ? parts[0] : new AstNode("Comma", null, LineOf(e), parts.ToArray());
            }
            case CParser.AssignmentExpressionContext a:
                if (a.conditionalExpression() is { } ce) return Expr(ce);
                if (a.DigitSequence() is { } ds) return Number(ds.GetText(), LineOf(a));
                return new AstNode("Assign", a.assignmentOperator().GetText(), LineOf(a),
                    Expr(a.unaryExpression()), Expr(a.assignmentExpression()));
            case CParser.ConditionalExpressionContext c:
            {
                var cond = Expr(c.logicalOrExpression());
                return c.expression() is { } te
                    ? new AstNode("Ternary", null, LineOf(c), cond, Expr(te), Expr(c.conditionalExpression()))
                    : cond;
            }
            case CParser.ConstantExpressionContext ke: return Expr(ke.conditionalExpression());
            case CParser.MultiplicativeExpressionContext or CParser.AdditiveExpressionContext
                or CParser.ShiftExpressionContext or CParser.RelationalExpressionContext
                or CParser.EqualityExpressionContext or CParser.AndExpressionContext
                or CParser.ExclusiveOrExpressionContext or CParser.InclusiveOrExpressionContext
                or CParser.LogicalAndExpressionContext or CParser.LogicalOrExpressionContext:
                return BinaryChain((ParserRuleContext)t);
            case CParser.CastExpressionContext cast:
                if (cast.DigitSequence() is { } d2) return Number(d2.GetText(), LineOf(cast));
                if (cast.unaryExpression() is { } cu) return Expr(cu);
                return new AstNode("Cast", cast.typeName().GetText(), LineOf(cast), Expr(cast.castExpression()))
                    { Type = cast.typeName().GetText() };
            case CParser.UnaryExpressionContext u: return Unary(u);
            case CParser.PostfixExpressionContext p: return Postfix(p);
            case CParser.PrimaryExpressionContext pr: return Primary(pr);
            default: return Unsupported((ParserRuleContext)t, "expression");
        }
    }

    AstNode BinaryChain(ParserRuleContext c)
    {
        var left = Expr(c.GetChild(0));
        for (var i = 1; i + 1 < c.ChildCount; i += 2)
            left = new AstNode("BinOp", Tok(c.GetChild(i)), LineOf(c), left, Expr(c.GetChild(i + 1)));
        return left;
    }

    AstNode Unary(CParser.UnaryExpressionContext u)
    {
        var line = LineOf(u);
        var prefixes = new List<string>();
        for (var i = 0; i < u.ChildCount && u.GetChild(i) is ITerminalNode tn; i++)
            prefixes.Add(tn.Symbol.Text);
        if (prefixes.Contains("sizeof") || prefixes.Contains("_Alignof") || prefixes.Contains("&&"))
            return Unsupported(u, "sizeof / alignof / label address");

        AstNode core;
        if (u.postfixExpression() is { } pf) core = Postfix(pf);
        else if (u.unaryOperator() is { } op) core = new AstNode("Unary", op.GetText(), line, Expr(u.castExpression()));
        else return Unsupported(u, "unary expression");

        for (var i = prefixes.Count - 1; i >= 0; i--)
            core = new AstNode("PreInc", prefixes[i], line, core);   // Text is "++" or "--"
        return core;
    }

    AstNode Postfix(CParser.PostfixExpressionContext p)
    {
        var line = LineOf(p);
        if (p.primaryExpression() is not { } prim) return Unsupported(p, "compound literal");
        var node = Primary(prim);
        for (var i = 1; i < p.ChildCount; i++)
        {
            switch (Tok(p.GetChild(i)))
            {
                case "[":
                    node = new AstNode("Index", null, line, node, Expr(p.GetChild(i + 1)));
                    i += 2;
                    break;
                case "(":
                {
                    var call = new AstNode("Call", node.Kind == "Id" ? node.Text : "?", line);
                    if (node.Kind != "Id") return Unsupported(p, "call through expression");
                    if (p.GetChild(i + 1) is CParser.ArgumentExpressionListContext args)
                    {
                        call.Children.AddRange(args.assignmentExpression().Select(Expr));
                        i += 2;
                    }
                    else i += 1;
                    node = call;
                    break;
                }
                case "++": node = new AstNode("PostInc", "++", line, node); break;
                case "--": node = new AstNode("PostInc", "--", line, node); break;
                default: return Unsupported(p, "struct member access");
            }
        }
        return node;
    }

    AstNode Primary(CParser.PrimaryExpressionContext p)
    {
        var line = LineOf(p);
        if (p.Identifier() is { } id) return new AstNode("Id", id.GetText(), line);
        if (p.Constant() is { } k) return Constant(k.GetText(), line);
        if (p.StringLiteral().Length > 0)
        {
            var bytes = new List<byte>();
            foreach (var s in p.StringLiteral())
            {
                var txt = s.GetText();
                if (!txt.StartsWith('"')) return Unsupported(p, "wide / prefixed string");
                bytes.AddRange(Unescape(txt[1..^1]));
            }
            return new AstNode("Str", Display(bytes), line) { Bytes = bytes.ToArray() };
        }
        if (p.expression() is { } e && p.ChildCount == 3) return Expr(e);
        return Unsupported(p, "primary expression");
    }

    static string Display(IEnumerable<byte> bytes)
    {
        var sb = new StringBuilder("\"");
        foreach (var b in bytes)
            sb.Append(b switch { 10 => "\\n", 9 => "\\t", 13 => "\\r", 0 => "\\0", 34 => "\\\"", 92 => "\\\\",
                >= 32 and < 127 => ((char)b).ToString(), _ => $"\\x{b:x2}" });
        return sb.Append('"').ToString();
    }

    AstNode Constant(string text, int line)
    {
        if (text.StartsWith('\''))
        {
            var b = Unescape(text[1..^1]);
            return b.Count == 1 ? new AstNode("Char", Display(b).Replace('"', '\''), line) { Value = b[0] }
                                : new AstNode("Unsupported", "multi-character constant", line);
        }
        if (text.Length > 1 && text[1] is not ('x' or 'X') && text.IndexOfAny(['.', 'e', 'E']) >= 0 && !text.StartsWith("0x"))
            return new AstNode("Unsupported", "floating point constant", line);
        if (text.StartsWith("0x") && text.Contains('.')) return new AstNode("Unsupported", "floating point constant", line);
        return Number(text, line);
    }

    static AstNode Number(string text, int line)
    {
        var t = text.TrimEnd('u', 'U', 'l', 'L');
        System.Numerics.BigInteger v;
        try
        {
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = System.Numerics.BigInteger.Parse("0" + t[2..], NumberStyles.HexNumber);
            else if (t.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) v = t[2..].Aggregate(System.Numerics.BigInteger.Zero, (a, c) => a * 2 + (c - '0'));
            else if (t.Length > 1 && t[0] == '0') v = t[1..].Aggregate(System.Numerics.BigInteger.Zero, (a, c) => a * 8 + (c - '0'));
            else v = System.Numerics.BigInteger.Parse(t, CultureInfo.InvariantCulture);
        }
        catch (FormatException) { return new AstNode("Unsupported", $"bad number {text}", line); }
        // the target has 8-bit cells: remember the wrap in Text so the AST shows what the program really computes
        return new AstNode("Num", v.ToString(), line) { Value = (int)(v & 0xFF) };
    }

    public static List<byte> Unescape(string s)
    {
        var r = new List<byte>();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { r.AddRange(Encoding.UTF8.GetBytes(c.ToString())); continue; }
            c = s[++i];
            switch (c)
            {
                case 'n': r.Add(10); break;
                case 't': r.Add(9); break;
                case 'r': r.Add(13); break;
                case 'a': r.Add(7); break;
                case 'b': r.Add(8); break;
                case 'f': r.Add(12); break;
                case 'v': r.Add(11); break;
                case 'x':
                {
                    var j = i + 1; var v = 0;
                    while (j < s.Length && Uri.IsHexDigit(s[j])) v = v * 16 + Convert.ToInt32(s[j++].ToString(), 16);
                    r.Add((byte)v); i = j - 1; break;
                }
                case >= '0' and <= '7':
                {
                    var v = 0; var j = i;
                    while (j < s.Length && j < i + 3 && s[j] is >= '0' and <= '7') v = v * 8 + (s[j++] - '0');
                    r.Add((byte)v); i = j - 1; break;
                }
                default: r.Add((byte)c); break;   // \\ \' \" \?
            }
        }
        return r;
    }
}
