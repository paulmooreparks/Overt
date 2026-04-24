# Overt: Operator Precedence and Associativity

**Authoritative for the parser.** Specifies how token sequences group into expressions. The lexical structure is in [`lexical.md`](lexical.md). The full expression and statement grammar will live in [`syntax.md`](syntax.md); this document is the subset that governs operator parsing.

**Applies to:** the expression sublanguage. Statement and declaration structure is not governed by precedence.

---

## 1. Precedence table

Tightest-binding at the top. Each row is strictly higher-precedence than every row below it. Rows are numbered for reference; numbering is stable, new rows insert with decimal suffixes.

| # | Level | Operators | Associativity | Notes |
|---|---|---|---|---|
| 1 | Primary | literal, identifier, `(expr)`, `if`/`else`, `match`, `with`, `trace`, block `{ ... }`, record literal, list literal | — | Expression forms, not operators. |
| 2 | Postfix | `f(args)`, `.field`, `?` | left | Call, field access, error propagation. |
| 3 | Unary prefix | `-x`, `!x` | — (non-chainable; see §3) | Numeric negation, logical not. |
| 4 | Multiplicative | `*` `/` `%` | left | |
| 5 | Additive | `+` `-` | left | |
| 6 | Comparison | `<` `<=` `>` `>=` | **non-associative** (see §4) | `a < b < c` is a parse error, not a chain (for now). |
| 7 | Equality | `==` `!=` | **non-associative** | `a == b == c` is a parse error. |
| 8 | Logical AND | `&&` | left | Short-circuits. |
| 9 | Logical OR | `\|\|` | left | Short-circuits. |
| 10 | Pipe compose | `\|>` `\|>?` | **left** | See §5 for why pipes are the loosest binary operators. |

**No assignment operators.** `=` in `let x = expr` and `x = expr` (rebinding of `let mut`) is a statement-level token, not an operator in the expression grammar. There is no `+=` / `-=` / etc. family.

**No bitwise operators in v1.** `&` `|` `^` `~` are reserved in the token set (§7.2 of [`lexical.md`](lexical.md)) but produce a parser error if used in expression position. This is deliberate: bitwise manipulation is FFI-boundary territory and the shape of those APIs has not been designed.

**No ternary.** `if`/`else` is an expression; there is no `? :` form.

---

## 2. Postfix chaining

All postfix operators (level 2) chain left-to-right and bind tighter than any prefix or infix operator:

```
fetch(id).user.email?
=> (((fetch(id)).user).email)?
```

The `?` operator is strictly postfix and appears **only** after a non-pipe expression. `(pipe_expr)?` is disallowed at the parser level (see DESIGN.md §7). The canonical form for propagating out of a pipe is `|>?`.

**Interaction with unary prefix:** postfix binds tighter than prefix, so `-x?` parses as `-(x?)` (propagate, then negate). This matches Rust / Swift convention and reads naturally: "if `x` is an error, propagate; otherwise negate the value."

---

## 3. Unary prefix non-chainability

`!!x` and `--x` are **not** valid Overt. Each unary prefix operator applies once and is not chainable. Reasons:

- Double-negation is a human idiom to coerce to `Bool`; Overt has no implicit conversions, so if you want `Bool(x)` you ask for it explicitly.
- `--x` in other languages is decrement, which Overt does not have (no mutation operators).

To write the double application, parenthesize: `!(!x)`. The formatter rejects this; the compiler accepts it with a hint.

---

## 4. Non-associative comparison and equality

`a < b < c` and `a == b == c` are **parse errors** in v1. Reasons:

- Agents reliably get these wrong in C-family languages, where `a < b < c` silently means `(a < b) < c` (bool compared with the third operand) and produces nonsense results with no warning.
- Chained comparison (`0 <= self <= 150` meaning `0 <= self AND self <= 150`) is appealing and reads naturally in refinement predicates, but introduces a parser-level special case that buys very little over the explicit `&&` form.

**Resolved (2026-04-22): reject chained comparison everywhere.** Range predicates must be written with explicit `&&`: `T where 0 <= self && self <= 150`. Rationale: one canonical form (§4 of DESIGN.md); no parser-level magic that only applies inside one context; diagnostic code `OV0102` points agents at the exact rewrite. The notational savings of chained form do not clear the bar for a grammar special case.

---

## 5. Why pipes are the loosest operators

`|>` and `|>?` bind looser than every arithmetic, comparison, and logical operator. This means:

```
a + b |> f
=> (a + b) |> f         // yes, this is what it does
=> f(a + b)

x == y |> g
=> (x == y) |> g
=> g(x == y)
```

Rationale: the pipe operator is **dataflow**, not an arithmetic combinator. The piped value is always conceptually "the result so far, computed however tightly it needs to be." Reversing this (making pipes tight) would force parentheses around every arithmetic subexpression feeding a pipeline, producing noisy code for the common case.

Pipes are **left-associative**:

```
x |> f |> g |> h  =  ((x |> f) |> g) |> h  =  h(g(f(x)))
```

This matches both semantic intuition (left-to-right dataflow) and the canonical Rust/F#/OCaml pipe conventions.

**Mixing `|>` and `|>?`:** same precedence, both left-associative. A mixed chain evaluates left-to-right; the `?` form propagates at each `|>?` step:

```
x |> f |>? g |> h
=  h((f(x) |>? g))
   which propagates if g(f(x)) is Err, else returns h(Ok-inner(g(f(x))))
```

---

## 6. Primary forms as expressions

Several keyword-introduced forms are expressions at Overt's grammar level, not statements. They appear at primary level (tighter than any operator) and their bodies are themselves expressions:

- **`if cond { then_expr } else { else_expr }`.** The `else` arm is **optional**. When absent, the form is equivalent to `if cond { then_expr } else { () }` and the then block must have type `()` (type checker enforces). When present, both arms' types must match.
- **`match scrutinee { pattern => expr, ... }`.** Exhaustive; each arm is an expression.
- **`with record_expr { field1 = expr, field2 = expr, ... }`.** Record copy-with-modification (DESIGN.md §10).
- **`trace { body }`.** Trace block (DESIGN.md §14); value is the body's value.
- **Block `{ stmt; stmt; expr }`.** Value is the trailing expression, or `()` if there is none.

These forms are **atomic** to the operator grammar. `f(if c { 1 } else { 2 })` is a call whose argument is an `if` expression. No operator-precedence interaction is needed: the matched braces delimit the form.

---

## 7. Disallowed combinations (parser diagnostics)

| Form | Example | Diagnostic | Canonical form |
|---|---|---|---|
| Pipe expression followed by postfix `?` | `(x \|> f)?` | `OV0101` | `x \|>? f` |
| Chained comparison | `0 <= x < 100` | `OV0102` | `0 <= x && x < 100` |
| Chained equality | `a == b == c` | `OV0102` | `a == b && b == c` |
| Double unary prefix | `!!x` | `OV0103` | `!(!x)` (rare); usually redundant |
| Bitwise operator in expression | `x & y` | `OV0104` | None in v1 (reserved for future) |

Diagnostic codes prefixed `OV01xx` are parser-level; lexer codes are `OV00xx` (see [`lexical.md`](lexical.md) §9).

---

## 8. Summary pseudo-grammar

For a recursive-descent parser, the expression grammar translates directly into one function per precedence level:

```
expression       = pipe
pipe             = logical_or (( "|>" | "|>?" ) logical_or)*
logical_or       = logical_and ("||" logical_and)*
logical_and      = equality ("&&" equality)*
equality         = comparison (("==" | "!=") comparison)?  // ? not *, non-assoc
comparison       = additive (("<" | "<=" | ">" | ">=") additive)?
additive         = multiplicative (("+" | "-") multiplicative)*
multiplicative   = unary_prefix (("*" | "/" | "%") unary_prefix)*
unary_prefix     = ("-" | "!")? postfix                    // ? not +, no chain
postfix          = primary postfix_op*
postfix_op       = "(" args ")" | "." ident | "?"
primary          = literal | identifier | "(" expression ")"
                 | if_expr | match_expr | with_expr | trace_expr | block
                 | record_literal | list_literal
```

This is a sketch. The full grammar with argument shapes, patterns, and record/list literals is for [`syntax.md`](syntax.md).
