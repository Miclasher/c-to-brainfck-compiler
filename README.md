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
