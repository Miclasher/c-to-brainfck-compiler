using CCompiler.Core.Ast;

namespace CCompiler.Core.Semantics;

public enum SymbolKind
{
    Variable,
    Function
}

/// <summary>A declared name. Variables own <see cref="Size"/> consecutive tape cells starting at <see cref="Cell"/>.</summary>
public sealed class Symbol
{
    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }
    public string Type { get; set; } = "int";
    public int Line { get; set; }

    // variables
    public int Cell { get; set; } = -1;
    public int Size { get; set; } = 1;
    public bool IsArray { get; set; }

    // functions
    public AstNode? Definition { get; set; }
    public List<Symbol> Params { get; } = [];
    public Symbol? ReturnCell { get; set; }
    public bool ReturnsVoid { get; set; }
}