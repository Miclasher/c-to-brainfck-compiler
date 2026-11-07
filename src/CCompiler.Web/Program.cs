using System.Text.Json.Serialization;
using CCompiler.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var samplesDir = Path.Combine(AppContext.BaseDirectory, "samples");

app.MapGet("/api/samples", () =>
    Directory.Exists(samplesDir)
        ? Directory.GetFiles(samplesDir, "*.c").OrderBy(f => f)
            .Select(f => new { name = Path.GetFileNameWithoutExtension(f), source = File.ReadAllText(f) })
        : []);

app.MapPost("/api/compile", (CompileRequest req) =>
{
    var r = Compiler.Compile(req.Source ?? "", req.Annotate);
    return new
    {
        ok = !r.HasErrors,
        cst = r.Cst,
        ast = r.Ast,
        foldedAst = r.FoldedAst,
        brainfuck = r.Brainfuck,
        diagnostics = r.Diagnostics,
    };
});

app.Run();

record CompileRequest(string? Source, bool Annotate = true);