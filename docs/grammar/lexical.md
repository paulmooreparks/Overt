# Overt — Lexical Grammar

**Authoritative.** The lexer implementation under [`src/Overt.Compiler/Syntax/Lexer.cs`](../../src/Overt.Compiler/Syntax/Lexer.cs) is verified against this document via the test suite; divergence is a bug in one or the other.

**Scope.** This document defines the token-level grammar only: how a source file is chopped into tokens. Expression, statement, and declaration grammar live in [`syntax.md`](syntax.md) (not yet written). Operator precedence and associativity live in [`precedence.md`](precedence.md).

**Notation.** EBNF-adjacent. `X*` means zero or more, `X+` one or more, `X?` optional, `X | Y` alternation, `[a-z]` character class, `"lit"` literal text. Lowercase names are productions defined here; uppercase names (e.g. `IDENT`) are token kinds that appear in the output stream.

---

## 1. Character set and source encoding

**Source files are UTF-8.** The lexer operates on the Unicode code-point sequence after decoding.

**Identifier character set is ASCII-only for v1.** Unicode identifiers are deferred — the canonical-form ethos argues for reducing spelling variance (`α` vs `a`, confusable homoglyphs), and the agent training corpus is overwhelmingly ASCII. String literal contents are full Unicode.

---

## 2. Whitespace and line terminators

```
whitespace       = " " | "\t"
line_terminator  = "\n" | "\r\n" | "\r"
```

Whitespace and line terminators separate tokens and are otherwise **discarded** — they do not appear in the token stream. There is no significant indentation.

Line terminators are normalized for position tracking: `\r\n` and bare `\r` advance the line counter by one. Column counters reset to 1 after any line terminator.

Trailing whitespace on a line is legal but discouraged by the formatter.

---

## 3. Comments

```
line_comment = "//" (any character except line_terminator)*
```

Line comments run to the end of the line and are **discarded** — they do not appear in the token stream emitted to the parser.

> **Open:** block comments. Not in v1. The `@review:` / `@agent:` conventions (DESIGN.md §21) are line-comment based; no use case for block comments has surfaced.

**Special comment tags** (`@review:` and `@agent:`) are line comments with additional semantic meaning enforced by tooling outside the compiler. They are not distinguished at the lexical level.

---

## 4. Identifiers and keywords

```
ident_start    = [a-zA-Z_]
ident_continue = [a-zA-Z_0-9]
identifier     = ident_start ident_continue*
```

An identifier is any maximal run matching `identifier` that is not a reserved word. If the run matches a reserved word, the token kind is the keyword kind instead.

**Reserved words (keywords):**

```
as       async    each     else     enum     extern   false    fn
for      from     if       in       inference io      let      loop
match    module   mut      parallel pub      race     record   return
trace    true     unsafe   use      where    while    with
```

> **Note on `async` / `io` / `inference`:** these are keywords at the lexer level so that effect-row syntax `!{io, async}` parses unambiguously. Whether they are truly reserved for user identifiers in all contexts is a parser-level concern handled in [`syntax.md`](syntax.md).

> **Not keywords:** `Ok`, `Err`, `Some`, `None`, `Result`, `Option`, `List`, `Int`, `String`, `Bool`. These are ordinary identifiers that happen to name stdlib types and constructors. The lexer does not privilege them.

Identifiers are ASCII-only in v1 (see §1).

---

## 5. Numeric literals

Numeric literals are unsigned at the lexical level. Negation is a unary operator applied during parsing.

### 5.1 Integer literals

```
decimal_digit  = [0-9]
decimal_int    = decimal_digit (decimal_digit | "_")*

hex_digit      = [0-9a-fA-F]
hex_prefix     = "0x" | "0X"
hex_int        = hex_prefix hex_digit (hex_digit | "_")*

binary_digit   = [0-1]
binary_prefix  = "0b" | "0B"
binary_int     = binary_prefix binary_digit (binary_digit | "_")*

integer_literal = hex_int | binary_int | decimal_int
```

Underscores are legal anywhere after the first digit of the value and are **purely visual separators** — they do not appear in the semantic value. `1_000_000`, `1_000000`, and `1000000` denote the same integer. Leading underscores (`_1000`) are not permitted — that lexes as an identifier. Trailing underscores (`1000_`) are rejected with a diagnostic.

> **Deferred:** numeric type suffixes (`42u64`, `3.14f32`). Not in v1 — type inference at expression level handles this.

### 5.2 Float literals

```
float_literal =
    decimal_int "." decimal_int (("e" | "E") ("+" | "-")? decimal_int)?
  | decimal_int ("e" | "E") ("+" | "-")? decimal_int
```

The fractional part is **required** when a decimal point is present — `3.` is not a float. Scientific notation without a fractional part (`6e23`) is permitted. Hex and binary float literals are not supported in v1.

Underscore rules from §5.1 apply to every digit run within a float literal.

**Disambiguation with field access:** the lexer uses one-character lookahead after a decimal point. `42.foo` lexes as `IntegerLiteral(42)`, `Dot`, `Identifier("foo")` — the `.` is not consumed as part of the number because the character that follows is not a digit. `42.0` lexes as `FloatLiteral(42.0)`.

---

## 6. String literals and interpolation

Strings are the most involved piece of the lexical grammar because interpolation requires the lexer to switch between two scanning modes.

### 6.1 The two modes

**Default mode** is the top-level mode for source code. It tokenizes identifiers, keywords, numbers, punctuation, and opens strings.

**String-body mode** is entered on `"` and exited on the matching closing `"`. Inside string-body mode, the lexer scans for literal characters, escape sequences, and interpolation triggers (`$name` or `${...}`) — nothing else.

Interpolation **body** (the expression inside `${...}`) is lexed in **default mode** with a brace-depth counter. This means every token kind available in the rest of the language — including nested strings with their own interpolations — works inside `${...}` transparently.

### 6.2 Mode automaton

Stated as a state machine. The lexer maintains a stack of modes; the top element determines which scanner runs for the next token.

| State transition | Trigger | Action |
|---|---|---|
| Default → StringBody | `"` consumed in default mode | Push `StringBody`. Emit `StringHead` or `StringLiteral` depending on whether the string ends with an interpolation (see §6.3). |
| StringBody → Default (interp) | `$IDENT` or `${` matched in string body | Push `Interpolation(braceDepth=0)` (only for `${`; for `$IDENT`, emit `Dollar` + `Identifier` and stay in string body is the naïve read — but see §6.3 for the actual token shape). |
| Default (interp) → StringBody | Unmatched `}` while in `Interpolation` with `braceDepth == 0` | Pop `Interpolation`. Continue string body, emitting `StringMiddle` or `StringTail`. |
| StringBody → Default (pop) | `"` consumed in string body | Pop `StringBody`. Emit `StringEnd` or final `StringLiteral` part. |

Inside `Interpolation` mode, every `{` increments `braceDepth`, every `}` that does not match it decrements. Only a `}` with `braceDepth == 0` returns control to string-body mode. This is how `"${ record { x = 1 } |> f }"` works — the inner `{` / `}` pairs do not close the interpolation.

### 6.3 Token emission

For a string with **no** interpolations: one `StringLiteral` token containing the full string including both quote characters.

For a string with **one or more** interpolations, the string is fragmented into a sequence of tokens:

- `StringHead` — opening `"` through the first `$` or `${`, exclusive. May be empty (`"${...`).
- Interpolation tokens — `Dollar` + `Identifier` for `$name` form, or `InterpolationStart` + inner tokens + `InterpolationEnd` for `${...}` form.
- `StringMiddle` — between two interpolations. May be empty. May appear zero or more times.
- `StringTail` — last interpolation through closing `"`. May contain no literal text if the `"` immediately follows `}` (`...${x}"`).

**Worked examples:**

```
"hello"
→ StringLiteral("\"hello\"")

"Hello, $name!"
→ StringHead("\"Hello, ")
  Dollar("$")
  Identifier("name")
  StringTail("!\"")

"${price * 1.08}"
→ StringHead("\"")
  InterpolationStart("${")
  Identifier("price") Star("*") FloatLiteral("1.08")
  InterpolationEnd("}")
  StringTail("\"")

"a${x}b${y}c"
→ StringHead("\"a")
  InterpolationStart("${") Identifier("x") InterpolationEnd("}")
  StringMiddle("b")
  InterpolationStart("${") Identifier("y") InterpolationEnd("}")
  StringTail("c\"")

"nested: ${inner("hello, $who")}"
→ StringHead("\"nested: ")
  InterpolationStart("${")
  Identifier("inner") LeftParen
    StringHead("\"hello, ")
    Dollar Identifier("who")
    StringTail("\"")
  RightParen
  InterpolationEnd("}")
  StringTail("\"")
```

### 6.4 Interpolation forms

```
dollar_ident = "$" identifier
interp_expr  = "${" (any tokens with brace-depth tracking) "}"
```

**`$identifier` is strictly an identifier** — no dotted paths (`$user.name` interpolates `user` and leaves `.name` as literal text), no call syntax, no arithmetic. For anything richer, use `${...}`.

Rationale: keeping `$IDENT` narrow eliminates any "where does the interpolation end" question. If an agent needs a pattern beyond a bare name, `${...}` makes the scope explicit.

### 6.5 Escape sequences

Inside string-body mode:

```
escape =
    "\\\\"  // backslash
  | "\\\""  // double quote
  | "\\'"   // single quote (permitted for uniformity; single-quoted strings not supported)
  | "\\n"   // line feed
  | "\\r"   // carriage return
  | "\\t"   // tab
  | "\\0"   // null
  | "\\$"   // literal dollar sign — the ONLY way to put a `$` in a string
  | "\\u{" hex_digit+ "}"  // unicode code-point escape
```

**`$` without an identifier or `{` following is an error.** There is no "fallback to literal" — the canonical form for a literal `$` is `\$`. A bare `$5` or `$ ` in a string body produces an `OV0003` diagnostic.

Raw strings (`r"..."`) are deferred.

### 6.6 Newlines inside strings

Unescaped newlines inside a string literal produce an `OV0001` diagnostic. Multi-line strings are deferred to a later raw-string design.

### 6.7 Empty-text parts

`StringMiddle` and `StringHead` / `StringTail` may carry zero text characters between their delimiters and still be emitted, so that downstream code can count parts structurally:

```
"${a}${b}"
→ StringHead("\"")            // empty literal text
  InterpolationStart Identifier("a") InterpolationEnd
  StringMiddle("")             // empty between a and b
  InterpolationStart Identifier("b") InterpolationEnd
  StringTail("\"")             // empty closing text
```

---

## 7. Punctuation and operator tokens

All non-alphanumeric, non-string, non-comment tokens. Lexed with **maximal-munch**: always prefer the longest matching token.

### 7.1 Single-character punctuation

```
(   LeftParen         )   RightParen
{   LeftBrace         }   RightBrace
[   LeftBracket       ]   RightBracket
,   Comma             ;   Semicolon
:   Colon             .   Dot
@   At                ~   Tilde
+   Plus              *   Star
/   Slash             %   Percent
^   Caret             ?   Question
```

### 7.2 Characters that begin multi-character tokens

Each of these characters, in isolation, produces a single-character token; followed by specific characters, a compound token. Maximal-munch applies.

| Start | Next | Token | Lexeme |
|---|---|---|---|
| `-` | `>` | `Arrow` | `->` |
| `-` | — | `Minus` | `-` |
| `=` | `=` | `EqualsEquals` | `==` |
| `=` | `>` | `FatArrow` | `=>` |
| `=` | — | `Equals` | `=` |
| `!` | `=` | `BangEquals` | `!=` |
| `!` | — | `Bang` | `!` |
| `<` | `=` | `LessEquals` | `<=` |
| `<` | — | `Less` | `<` |
| `>` | `=` | `GreaterEquals` | `>=` |
| `>` | — | `Greater` | `>` |
| `&` | `&` | `AmpersandAmpersand` | `&&` |
| `&` | — | `Ampersand` | `&` |
| `:` | `:` | `ColonColon` | `::` |
| `:` | — | `Colon` | `:` |
| `\|` | `\|` | `PipePipe` | `\|\|` |
| `\|` | `>`, `?` | `PipePropagate` | `\|>?` |
| `\|` | `>` | `PipeCompose` | `\|>` |
| `\|` | — | `Pipe` | `\|` |

The two-step lookahead on `|>?` is load-bearing. Parsing must see `|` followed by `>` followed by `?` as a single three-character token, not `PipeCompose` + `Question`. DESIGN.md §7 disallows `(pipe_expr)?`, so there is no valid source in which `|>` and a following `?` should be separate tokens.

### 7.3 The `$` character outside strings

`$` is **not a valid character in source outside string-body mode.** Encountering one in default mode produces an `OV0002` diagnostic. The lexer emits an `Unknown` token and continues.

---

## 8. Ordering and overlap rules

When a character begins more than one possible token, the lexer uses the following precedence (resolving maximal-munch across categories):

1. Line comment (`//`) — beats `Slash` when followed by another `/`.
2. Float literal — beats integer literal + `Dot` when the character after `.` is a digit.
3. Multi-character punctuation — beats single-character punctuation at the same start.
4. Keyword — beats identifier when the full identifier run matches a reserved word.
5. Integer/float literal — no overlap with identifiers (numbers never start with an ident-start character).

---

## 9. Diagnostic codes

The lexer emits these codes. They are committed contract — add new codes at the end, never renumber.

| Code | Meaning |
|---|---|
| `OV0001` | Unterminated string literal. |
| `OV0002` | Unexpected character in default mode. |
| `OV0003` | Bare `$` in string body not followed by an identifier or `{`. |
| `OV0004` | Invalid escape sequence in string. |
| `OV0005` | Trailing underscore in numeric literal. |

Each diagnostic carries a `SourceSpan` covering the offending characters.

---

## 10. Token kind reference

The authoritative list of token kinds lives in [`TokenKind.cs`](../../src/Overt.Compiler/Syntax/TokenKind.cs). This document references kinds by name; any mismatch is a bug.

---

## 11. Not covered (deferred)

- Block comments
- Character literals (single-quoted single characters) — §9 / §13 use constructor-call forms, so no need has surfaced
- Raw string literals (`r"..."`) — deferred until needed for regex-ish content
- Byte string literals — no use case in v1
- Multi-line string literals (`"""..."""`)
- Numeric type suffixes
- Unicode identifiers
- `$` as a meaningful character outside string context
