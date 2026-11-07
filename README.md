# C → Brainfuck compiler (ANTLR4 + .NET)

Lab 3, system programming. A syntax analyzer for **C** built on **ANTLR4** (the Lex/YACC alternative) with
semantic analysis, **compile-time evaluation**, **Brainfuck code generation** and an **AST visualization** in the browser (d3.js).

```
C source ──ANTLR lexer/parser──▶ parse tree ──AstBuilder──▶ AST ──Checker──▶ folded AST ──BrainfuckGen──▶ .bf
                                     │                        │                  │
                               "Parse tree" tab           "AST" tab      "after constant folding"
```

## Run

Requirements: .NET SDK 10, a Java runtime (only needed at build time, ANTLR's code generator is a Java tool).

```sh
dotnet test                              # 29 tests
dotnet run --project src/CCompiler.Web   # http://localhost:5020
```

In the page: pick a sample or type C, press **Compile** (Ctrl+Enter), then browse the tabs
**AST**, **Parse tree** (the raw ANTLR tree) and **Brainfuck** (copy / download, then paste into any online interpreter,
for example <https://copy.sh/brainfuck/>). The project deliberately contains no Brainfuck interpreter.

## Layout

| Path | What |
|---|---|
| `src/CCompiler.Core/Grammar/C.g4` | ANTLR4 grammar for C (grammars-v4, C11 + GNU extensions), unmodified |
| `src/CCompiler.Core/Ast/` | `AstNode`, `AstBuilder` (parse tree → compact AST) |
| `src/CCompiler.Core/Semantics/Checker.cs` | scopes, types, arity, lvalues, array bounds, **constant folding** |
| `src/CCompiler.Core/CodeGen/BrainfuckGen.cs` | AST → Brainfuck |
| `src/CCompiler.Core/Compiler.cs` | pipeline facade, ANTLR error listeners, parse-tree export |
| `src/CCompiler.Web/` | ASP.NET minimal API (`POST /api/compile`) + `wwwroot` (d3 tree, editor) |
| `tests/CCompiler.Tests/` | xunit tests |
| `samples/*.c` | demo programs |
| `docs/THEORY.md` | LALR / yacc / conflict resolution, ANTLR vs yacc, codegen design (for the defense) |

## What C is supported

The **whole C grammar parses**. The Brainfuck backend compiles this subset and reports everything else
as an error with its line number:

* `int`/`char`/`short`/`long`/`unsigned`… all modelled as **unsigned 8-bit cells** (values wrap at 256, like the tape)
* globals, locals, block scopes, 1-D arrays with **constant** indices, initializer lists, string initializers
* `+ - * / %`, comparisons, `&& || !` (short-circuit), `?:`, `= += -= *= /= %= <<= >>=`, `++ --`, unary `- + ~`,
  `<<`/`>>` by constants, casts between integer types, comma operator
* `if/else`, `while`, `do/while`, `for`, `break`, `continue`
* functions: parameters, return value, calls. They are **inlined**, so recursion is rejected and `return` must be the last statement
* I/O: `putchar`, `getchar`, `puts`, `printf` with a literal format and `%d %u %c %%`

Not supported: pointers, structs/unions/enums, floats, `switch`, `goto`, runtime array indices, runtime bitwise `& | ^`.

## Grading checklist (where to look)

| Item | Where |
|---|---|
| Working project | `dotnet test`, web app |
| C grammar | `Grammar/C.g4` |
| LALR / yacc / conflicts theory | `docs/THEORY.md` |
| Semantics / evaluation | `Checker.cs` (folding, diagnostics) |
| Target code generation | `BrainfuckGen.cs`, "Brainfuck" tab |
| AST visualization (d3.js) | `wwwroot/app.js`, "AST" / "Parse tree" tabs |
