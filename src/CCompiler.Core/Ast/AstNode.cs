using System.Text.Json.Serialization;
using CCompiler.Core.Semantics;

namespace CCompiler.Core.Ast;

/// <summary>Compact AST node. Kind is the node category (BinOp, If, Num...), Text its payload (operator, name, literal).</summary>
public sealed class AstNode
{
    [JsonPropertyName("name")] public string Kind { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("folded")] public bool Folded { get; set; }
    [JsonPropertyName("children")] public List<AstNode> Children { get; set; } = [];

    /// <summary>Compile-time value for Num/Char nodes (8-bit unsigned semantics).</summary>
    [JsonIgnore] public int Value { get; set; }
    [JsonIgnore] public byte[]? Bytes { get; set; }
    [JsonIgnore] public Symbol? Symbol { get; set; }

    public AstNode(string kind, string? text = null, int line = 0, params AstNode[] children)
    {
        Kind = kind;
        Text = text;
        Line = line;
        Children.AddRange(children);
    }

    public AstNode Clone()
    {
        var c = new AstNode(Kind, Text, Line)
        {
            Type = Type, Folded = Folded, Value = Value, Bytes = Bytes, Symbol = Symbol,
        };
        foreach (var ch in Children) c.Children.Add(ch.Clone());
        return c;
    }

    public IEnumerable<AstNode> Walk()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.Walk())
                yield return d;
    }

    public override string ToString() => Text is null ? Kind : $"{Kind}({Text})";
}
