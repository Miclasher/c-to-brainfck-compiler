using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using CCompiler.Core.Ast;
using CCompiler.Core.CodeGen;
using CCompiler.Core.Parsing;
using CCompiler.Core.Semantics;

namespace CCompiler.Core;

public sealed class CompileResult
{
    public AstNode? Cst { get; set; }
    public AstNode? Ast { get; set; }
    public AstNode? FoldedAst { get; set; }
    public string? Brainfuck { get; set; }
    public List<Diagnostic> Diagnostics { get; } = [];
    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);
}

public static class Compiler
{
    sealed class Collector(List<Diagnostic> sink, string phase) : IAntlrErrorListener<int>, IAntlrErrorListener<IToken>
    {
        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int col, string msg, RecognitionException e)
            => sink.Add(new(Severity.Error, phase, line, col, msg));

        public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int col, string msg, RecognitionException e)
            => sink.Add(new(Severity.Error, phase, line, col, msg));
    }

    public static CompileResult Compile(string source, bool annotate = true)
    {
        var result = new CompileResult();

        var lexer = new CLexer(new AntlrInputStream(source));
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(new Collector(result.Diagnostics, "lexer"));
        var parser = new CParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        parser.AddErrorListener(new Collector(result.Diagnostics, "parser"));

        var tree = parser.compilationUnit();
        result.Cst = CstExport.Export(tree, parser.RuleNames);
        if (result.HasErrors) return result;

        var ast = AstBuilder.Build(tree);
        result.Ast = ast.Clone();   // as parsed, before semantic folding

        var info = Checker.Check(ast, result.Diagnostics);
        result.FoldedAst = ast;
        if (result.HasErrors) return result;

        try
        {
            result.Brainfuck = BrainfuckGen.Generate(ast, info, source.Split('\n'), annotate);
        }
        catch (CompileException ex)
        {
            result.Diagnostics.Add(new(Severity.Error, "codegen", ex.Line, 0, ex.Message));
        }
        return result;
    }
}

/// <summary>Parse tree → AstNode for display. Chains of single-child rules are collapsed ("a › b › c").</summary>
static class CstExport
{
    public static AstNode Export(IParseTree t, string[] rules)
    {
        if (t is ITerminalNode term)
            return new AstNode("token", term.Symbol.Text, term.Symbol.Line);
        var ctx = (Antlr4.Runtime.ParserRuleContext)t;
        var names = new List<string>();
        IParseTree cur = ctx;
        while (cur is Antlr4.Runtime.ParserRuleContext c && c.ChildCount == 1 && c.GetChild(0) is Antlr4.Runtime.ParserRuleContext)
        {
            names.Add(rules[c.RuleIndex]);
            cur = c.GetChild(0);
        }
        var last = (Antlr4.Runtime.ParserRuleContext)cur;
        names.Add(rules[last.RuleIndex]);
        var node = new AstNode(string.Join(" › ", names), null, last.Start?.Line ?? 0);
        for (var i = 0; i < last.ChildCount; i++)
            if (last.GetChild(i) is not ITerminalNode { Symbol.Type: -1 })   // skip EOF
                node.Children.Add(Export(last.GetChild(i), rules));
        return node;
    }
}
