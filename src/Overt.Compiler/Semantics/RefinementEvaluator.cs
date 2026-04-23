using System.Globalization;
using Overt.Compiler.Syntax;

namespace Overt.Compiler.Semantics;

/// <summary>
/// Decides refinement predicates from <c>TypeAliasDecl.Predicate</c> against concrete
/// literal values. Handles the decidable fragment that DESIGN.md §8 promises can be
/// checked statically: numeric and boolean literal comparisons, logical and/or,
/// <c>self</c> references, unary <c>!</c>. Anything outside that fragment (function
/// calls, variable references, etc.) returns null and the caller defers to a runtime
/// assertion.
///
/// Returns:
/// <list type="bullet">
///   <item><c>true</c> — predicate provably holds for the given <c>self</c>.</item>
///   <item><c>false</c> — predicate provably fails; emit a compile-time violation.</item>
///   <item><c>null</c> — predicate couldn't be decided (undecidable fragment or
///     value shape mismatch). Caller falls back to "no diagnosis."</item>
/// </list>
/// </summary>
public static class RefinementEvaluator
{
    /// <summary>
    /// Extract a literal value from an expression, or null if the expression isn't a
    /// statically-known primitive literal. Supported: integer, float, boolean, string.
    /// Negative numeric literals (<c>-5</c>) are detected through <see cref="UnaryExpr"/>
    /// with <see cref="UnaryOp.Negate"/>.
    /// </summary>
    public static object? TryExtractLiteral(Expression expr) => expr switch
    {
        IntegerLiteralExpr i => ParseInteger(i.Lexeme),
        FloatLiteralExpr f => ParseFloat(f.Lexeme),
        BooleanLiteralExpr b => b.Value,
        StringLiteralExpr s => TrimQuotes(s.Value),
        UnaryExpr { Op: UnaryOp.Negate } neg => Negate(TryExtractLiteral(neg.Operand)),
        _ => null,
    };

    /// <summary>
    /// Evaluate a predicate with <c>self</c> bound to <paramref name="selfValue"/>.
    /// Returns null when the predicate falls outside the decidable fragment.
    /// </summary>
    public static bool? Evaluate(Expression predicate, object selfValue) => predicate switch
    {
        BooleanLiteralExpr b => b.Value,
        IdentifierExpr { Name: "self" } => selfValue as bool?,
        BinaryExpr be => EvaluateBinary(be, selfValue),
        UnaryExpr { Op: UnaryOp.LogicalNot } ue =>
            Evaluate(ue.Operand, selfValue) is { } inner ? !inner : null,
        _ => null,
    };

    // ------------------------------------------------------------- helpers

    private static bool? EvaluateBinary(BinaryExpr be, object selfValue)
    {
        // Short-circuiting evaluation for && / ||: if the LHS settles the result,
        // an undecidable RHS doesn't taint the answer.
        if (be.Op == BinaryOp.LogicalAnd)
        {
            var lhs = Evaluate(be.Left, selfValue);
            if (lhs == false) return false;
            var rhs = Evaluate(be.Right, selfValue);
            if (lhs is null || rhs is null) return null;
            return lhs.Value && rhs.Value;
        }
        if (be.Op == BinaryOp.LogicalOr)
        {
            var lhs = Evaluate(be.Left, selfValue);
            if (lhs == true) return true;
            var rhs = Evaluate(be.Right, selfValue);
            if (lhs is null || rhs is null) return null;
            return lhs.Value || rhs.Value;
        }

        // Comparison ops need concrete values from both sides.
        var left = EvaluateValue(be.Left, selfValue);
        var right = EvaluateValue(be.Right, selfValue);
        var cmp = Compare(left, right);
        if (cmp is null) return null;

        return be.Op switch
        {
            BinaryOp.Equal => cmp == 0,
            BinaryOp.NotEqual => cmp != 0,
            BinaryOp.Less => cmp < 0,
            BinaryOp.LessEqual => cmp <= 0,
            BinaryOp.Greater => cmp > 0,
            BinaryOp.GreaterEqual => cmp >= 0,
            _ => null,
        };
    }

    private static object? EvaluateValue(Expression expr, object selfValue) => expr switch
    {
        IdentifierExpr { Name: "self" } => selfValue,
        IntegerLiteralExpr i => ParseInteger(i.Lexeme),
        FloatLiteralExpr f => ParseFloat(f.Lexeme),
        BooleanLiteralExpr b => (object)b.Value,
        StringLiteralExpr s => TrimQuotes(s.Value),
        UnaryExpr { Op: UnaryOp.Negate } neg => Negate(EvaluateValue(neg.Operand, selfValue)),
        _ => null,
    };

    private static int? Compare(object? a, object? b)
    {
        if (a is null || b is null) return null;

        // Promote integer to double when the other operand is a double, so
        // `0 <= self` with self: Float still compares correctly.
        return (a, b) switch
        {
            (long la, long lb) => la.CompareTo(lb),
            (double da, double db) => da.CompareTo(db),
            (long la, double db) => ((double)la).CompareTo(db),
            (double da, long lb) => da.CompareTo((double)lb),
            (string sa, string sb) => string.CompareOrdinal(sa, sb),
            (bool ba, bool bb) => ba.CompareTo(bb),
            _ => null,
        };
    }

    private static long? ParseInteger(string lexeme)
    {
        var cleaned = lexeme.Replace("_", "");
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(cleaned[2..], NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var hex) ? hex : null;
        }
        if (cleaned.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            try { return Convert.ToInt64(cleaned[2..], 2); }
            catch { return null; }
        }
        return long.TryParse(cleaned, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var dec) ? dec : null;
    }

    private static double? ParseFloat(string lexeme)
        => double.TryParse(lexeme.Replace("_", ""), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string TrimQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1];
        return s;
    }

    private static object? Negate(object? v) => v switch
    {
        long l => -l,
        double d => -d,
        _ => null,
    };
}
