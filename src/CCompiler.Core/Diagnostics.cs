namespace CCompiler.Core;

public enum Severity
{
    Error,
    Warning
}

public sealed record Diagnostic(Severity Severity, string Phase, int Line, int Column, string Message)
{
    public override string ToString() =>
        $"{Line}:{Column} {Severity.ToString().ToLowerInvariant()} [{Phase}] {Message}";
}

public sealed class CompileException(string message, int line = 0) : Exception(message)
{
    public int Line { get; } = line;
}