namespace Overt.Compiler.Syntax;

public enum TokenKind
{
    // Special
    EndOfFile,
    Unknown,

    // Trivia we preserve as tokens (may be skipped by parser)
    LineComment,

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
    KeywordWhile,
    KeywordLoop,
    KeywordReturn,
    KeywordUse,
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
    KeywordFrom,
    KeywordAs,
    KeywordIn,

    // Literals
    IntegerLiteral,
    FloatLiteral,
    StringLiteral,

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
