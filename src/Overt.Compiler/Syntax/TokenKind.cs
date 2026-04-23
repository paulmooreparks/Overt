namespace Overt.Compiler.Syntax;

public enum TokenKind
{
    // Special
    EndOfFile,
    Unknown,

    // Identifiers and keywords
    Identifier,

    KeywordFn,
    KeywordLet,
    KeywordMut,
    KeywordWith,
    KeywordRecord,
    KeywordEnum,
    KeywordMatch,
    KeywordIf,
    KeywordElse,
    KeywordFor,
    KeywordEach,
    KeywordIn,
    KeywordWhile,
    KeywordLoop,
    KeywordBreak,
    KeywordContinue,
    KeywordReturn,
    KeywordUse,
    KeywordAs,
    KeywordModule,
    KeywordPub,
    KeywordParallel,
    KeywordRace,
    KeywordTrace,
    KeywordTrue,
    KeywordFalse,
    KeywordWhere,
    KeywordExtern,
    KeywordUnsafe,
    KeywordType,

    // Trivia — emitted by the lexer so the formatter can preserve them.
    // Parser callers skip these via AdvancePastComments / token-navigation helpers.
    LineComment,

    // Literals
    IntegerLiteral,
    FloatLiteral,

    // String literals — see docs/grammar/lexical.md §6.
    // Un-interpolated strings emit one StringLiteral token.
    // Interpolated strings fragment into StringHead [StringMiddle*] StringTail, with
    // interpolation tokens between them. Bare `$name` form emits Dollar + Identifier;
    // `${expr}` form emits InterpolationStart + inner tokens + InterpolationEnd.
    StringLiteral,
    StringHead,
    StringMiddle,
    StringTail,
    Dollar,
    InterpolationStart,
    InterpolationEnd,

    // Punctuation — single character
    LeftParen,          // (
    RightParen,         // )
    LeftBrace,          // {
    RightBrace,         // }
    LeftBracket,        // [
    RightBracket,       // ]
    Comma,              // ,
    Semicolon,          // ;
    Colon,              // :
    Dot,                // .
    At,                 // @
    Bang,               // !
    Question,           // ?
    Equals,             // =
    Plus,               // +
    Minus,              // -
    Star,               // *
    Slash,              // /
    Percent,            // %
    Ampersand,          // &
    Pipe,               // |
    Caret,              // ^
    Tilde,              // ~
    Less,               // <
    Greater,            // >

    // Punctuation — multi-character
    Arrow,              // ->
    FatArrow,           // =>
    PipeCompose,        // |>
    PipePropagate,      // |>?
    ColonColon,         // :: (reserved; not yet used)
    EqualsEquals,       // ==
    BangEquals,         // !=
    LessEquals,         // <=
    GreaterEquals,      // >=
    AmpersandAmpersand, // &&
    PipePipe,           // ||
}
