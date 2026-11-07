using CCompiler.Core;
using CCompiler.Core.Ast;

namespace CCompiler.Tests;

public class CompilerTests
{
    static CompileResult Compile(string body, bool annotate = false) =>
        Compiler.Compile($"int main(void) {{ {body} return 0; }}", annotate);

    static string[] Errors(CompileResult r) =>
        r.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message).ToArray();

    static bool Balanced(string bf)
    {
        var depth = 0;
        foreach (var c in bf)
        {
            if (c == '[') depth++;
            else if (c == ']' && --depth < 0) return false;
        }

        return depth == 0;
    }

    public static IEnumerable<object[]> Samples() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.c")
            .Select(f => new object[] { Path.GetFileName(f) });

    [Theory, MemberData(nameof(Samples))]
    public void Samples_compile_to_balanced_brainfuck(string file)
    {
        var r = Compiler.Compile(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", file)));
        Assert.Empty(Errors(r));
        Assert.NotNull(r.Brainfuck);
        Assert.True(Balanced(r.Brainfuck!));
    }

    [Fact]
    public void Hello_world_is_plain_output_code()
    {
        var r = Compiler.Compile("int main(void){ putchar('A'); return 0; }", annotate: false);
        Assert.Empty(r.Diagnostics);
        Assert.Contains('.', r.Brainfuck!);
        Assert.DoesNotContain(',', r.Brainfuck!);
    }

    [Fact]
    public void Annotations_never_add_instruction_characters()
    {
        var plain = Compiler.Compile("int main(void){ int a = 1; a += 2; printf(\"x-y\"); return 0; }", annotate: false)
            .Brainfuck!;
        var noted = Compiler.Compile("int main(void){ int a = 1; a += 2; printf(\"x-y\"); return 0; }", annotate: true)
            .Brainfuck!;
        string Ops(string s) => new(s.Where(c => "+-<>[].,".Contains(c)).ToArray());
        Assert.Equal(Ops(plain), Ops(noted));
    }

    [Fact]
    public void Constant_expressions_are_folded_with_8bit_semantics()
    {
        var r = Compile("int x = 2 * 3 + 1; int y = 200 + 100; int z = 7 / 2 + (1 < 2);");
        Assert.Empty(Errors(r));
        var nums = r.FoldedAst!.Walk().Where(n => n.Kind == "VarDecl")
            .Select(d => d.Children.Single(c => c.Kind == "Init").Children[0]).ToList();
        Assert.All(nums, n => Assert.Equal("Num", n.Kind));
        Assert.Equal([7, 44, 4], nums.Select(n => n.Value));
        Assert.True(nums[0].Folded);
        // the unfolded tree keeps the original expression
        Assert.Contains(r.Ast!.Walk(), n => n.Kind == "BinOp" && n.Text == "*");
    }

    [Fact]
    public void Precedence_is_respected_in_the_ast()
    {
        var r = Compile("int a = 1; int b = a + 2 * a;");
        var add = r.Ast!.Walk().First(n => n is { Kind: "BinOp", Text: "+" } && n.Children[0].Kind == "Id");
        Assert.Equal("Id", add.Children[0].Kind);
        Assert.Equal("*", add.Children[1].Text);
    }

    [Fact]
    public void Declaration_without_initializer_is_not_a_typedef_name()
    {
        var r = Compile("int i; i = 3;");
        Assert.Empty(Errors(r));
        Assert.Contains(r.Ast!.Walk(), n => n.Kind == "VarDecl" && n.Text == "i");
    }

    [Fact]
    public void Syntax_errors_carry_a_line_number()
    {
        var r = Compiler.Compile("int main(void) {\n  int x = ;\n}\n");
        Assert.True(r.HasErrors);
        Assert.Null(r.Brainfuck);
        Assert.Contains(r.Diagnostics, d => d.Phase == "parser" && d.Line == 2);
    }

    [Theory]
    [InlineData("y = 1;", "'y' is not declared")]
    [InlineData("int a; int a;", "already declared")]
    [InlineData("int a = 1 / 0;", "division by zero")]
    [InlineData("break;", "outside of a loop")]
    [InlineData("float f;", "not supported")]
    [InlineData("int *p;", "pointer")]
    [InlineData("int a[2]; a[5] = 1;", "out of bounds")]
    [InlineData("int a[2]; int i = 1; a[i] = 1;", "constants")]
    [InlineData("printf(\"%d %d\", 1);", "needs 2 argument")]
    [InlineData("struct S { int a; } s;", "struct")]
    [InlineData("goto end;", "goto")]
    [InlineData("int a = 1 & getchar();", "not supported at run time")]
    public void Semantic_errors(string body, string expected)
    {
        var r = Compile(body);
        Assert.Contains(Errors(r), m => m.Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.Null(r.Brainfuck);
    }

    [Fact]
    public void Recursion_and_missing_main_are_rejected()
    {
        Assert.Contains(Errors(Compiler.Compile("int f(int n) { return f(n); } int main(void) { f(1); return 0; }")),
            m => m.Contains("recursion"));
        Assert.Contains(Errors(Compiler.Compile("int x;")), m => m.Contains("no 'main'"));
    }

    [Fact]
    public void Early_return_is_rejected()
    {
        var r = Compiler.Compile("int f(int n) { if (n) return 1; return 0; } int main(void) { f(1); return 0; }");
        Assert.Contains(Errors(r), m => m.Contains("last statement"));
    }

    [Fact]
    public void Wide_constants_wrap_with_a_warning()
    {
        var r = Compile("int a = 300;");
        Assert.Contains(r.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("wraps to 44"));
    }

    [Fact]
    public void Parse_tree_export_collapses_single_child_chains()
    {
        var r = Compile("int a = 1 + 2;");
        Assert.Contains(r.Cst!.Walk(), n => n.Kind.Contains(" › "));
    }

    [Fact]
    public void Full_c_still_parses_even_when_codegen_is_impossible()
    {
        var r = Compiler.Compile(
            "typedef struct { int a; } T; int main(void) { T t; int *p = &t.a; return sizeof(t); }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Phase is "lexer" or "parser");
        Assert.NotNull(r.Ast);
        Assert.True(r.HasErrors);
    }
}