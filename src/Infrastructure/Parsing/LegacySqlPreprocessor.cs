using System.Text.RegularExpressions;

namespace RoZwet.Tools.StoreProc.Infrastructure.Parsing;

/// <summary>
/// Normalises legacy Sybase / SQL Server 6.x syntax constructs that are rejected
/// by the modern <c>TSql160Parser</c> before the SQL is handed to ScriptDom.
///
/// Transformations applied (in order):
/// <list type="number">
///   <item>
///     <term>Legacy RAISERROR — space-separated, comma-separated, or bare</term>
///     <description>
///       <c>RAISERROR &lt;num&gt; &lt;expr&gt;</c>,
///       <c>RAISERROR &lt;num&gt;, &lt;expr&gt;</c>, or
///       <c>RAISERROR &lt;num&gt;</c>
///       → <c>RAISERROR(&lt;expr_or_num&gt;, 16, 1)</c>.
///     </description>
///   </item>
///   <item>
///     <term>SET ARITHABORT NUMERIC_TRUNCATION</term>
///     <description>
///       Sybase-only qualifier stripped entirely; SQL Server only accepts
///       <c>SET ARITHABORT ON|OFF</c>.
///     </description>
///   </item>
///   <item>
///     <term>Double-quoted string literals</term>
///     <description>
///       <c>"literal value"</c> → <c>'literal value'</c>.
///       Sybase historically allowed <c>SET QUOTED_IDENTIFIER OFF</c>;
///       ScriptDom is initialised with <c>initialQuotedIdentifiers: true</c>,
///       which would mis-classify these as delimited identifiers.
///     </description>
///   </item>
///   <item>
///     <term>Sybase outer-join operators</term>
///     <description>
///       <c>col1 *= col2</c> (left outer) and <c>col1 =* col2</c> (right outer)
///       are neutralised to <c>col1 = col2</c>.  The semantics differ (inner join),
///       but the purpose here is AST-level dependency extraction, not query execution.
///     </description>
///   </item>
///   <item>
///     <term>CURSOR FOR READ ONLY / FOR BROWSE</term>
///     <description>
///       Sybase-specific cursor options appended after the SELECT are stripped;
///       T-SQL 160 rejects them as part of the DECLARE CURSOR statement body.
///     </description>
///   </item>
///   <item>
///     <term>SET SHOWPLAN</term>
///     <description>
///       <c>SET SHOWPLAN ON|OFF</c> is a Sybase-only statement rejected by T-SQL 160.
///       The entire statement is stripped.
///     </description>
///   </item>
///   <item>
///     <term>SET PROCID</term>
///     <description>
///       <c>SET PROCID ON|OFF</c> is Sybase-only and stripped entirely.
///     </description>
///   </item>
///   <item>
///     <term>Sybase multi-assignment SET</term>
///     <description>
///       Sybase allows <c>SET @a = expr1, @b = expr2</c> (multiple variable assignments
///       in one statement, comma-separated).  T-SQL 160 only permits one assignment per
///       SET.  Converted to <c>SELECT @a = expr1, @b = expr2</c> which is semantically
///       equivalent for scalar variable assignment and is valid in T-SQL 160.
///     </description>
///   </item>
/// </list>
///
/// The original SQL is never mutated — callers receive a new string.
/// Only the normalised copy is used for parsing; the original is preserved
/// verbatim for storage in the graph.
/// </summary>
internal static class LegacySqlPreprocessor
{
    /// <summary>
    /// Matches all three Sybase RAISERROR legacy forms:
    /// <list type="bullet">
    ///   <item><c>RAISERROR 20001 'message'</c>   — space-separated num + message</item>
    ///   <item><c>RAISERROR 20001, 'message'</c>   — comma-separated num + message</item>
    ///   <item><c>RAISERROR 20001</c>              — bare number, no message</item>
    /// </list>
    /// Group 1: the message number (used as fallback when no message is present).
    /// Group 2: the message expression (optional).
    /// </summary>
    private static readonly Regex LegacyRaiserrorPattern = new(
        @"(?i)\bRAISERROR\s+(\d+)(?:\s*,\s*|\s+)?(.*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ArithAbortNumericTruncationPattern = new(
        @"(?i)\bSET\s+ARITHABORT\s+NUMERIC_TRUNCATION\s+(?:ON|OFF)\b[^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DoubleQuotedStringPattern = new(
        @"""([^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Sybase left outer join: <c>expr1 *= expr2</c>.
    /// </summary>
    private static readonly Regex SybaseLeftOuterJoinPattern = new(
        @"\*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Sybase right outer join: <c>expr1 =* expr2</c>.
    /// </summary>
    private static readonly Regex SybaseRightOuterJoinPattern = new(
        @"=\*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Strips Sybase-specific cursor options: <c>FOR READ ONLY</c> and <c>FOR BROWSE</c>.
    /// </summary>
    private static readonly Regex CursorForReadOnlyPattern = new(
        @"(?i)\bFOR\s+(?:READ\s+ONLY|BROWSE)\b[^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Strips Sybase-only <c>SET SHOWPLAN ON|OFF</c>.</summary>
    private static readonly Regex SetShowplanPattern = new(
        @"(?i)\bSET\s+SHOWPLAN\s+(?:ON|OFF)\b[^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Strips Sybase-only <c>SET PROCID ON|OFF</c>.</summary>
    private static readonly Regex SetProcidPattern = new(
        @"(?i)\bSET\s+PROCID\s+(?:ON|OFF)\b[^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Converts Sybase multi-assignment <c>SET @a = e1, @b = e2</c> to
    /// T-SQL-compatible <c>SELECT @a = e1, @b = e2</c>.
    ///
    /// Detection: a <c>SET</c> keyword (word boundary, case-insensitive) followed by
    /// a variable assignment (<c>@identifier =</c>) that contains at least one comma
    /// before the next assignment (<c>, @identifier =</c>).  Only the keyword is
    /// replaced — the rest of the statement is preserved verbatim so that complex
    /// expressions (function calls, sub-selects) are not disturbed.
    /// </summary>
    private static readonly Regex SybaseMultiSetPattern = new(
        @"(?i)\bSET\s+(@\w+\s*=\s*[^@\r\n]+,\s*@\w+\s*=)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns a version of <paramref name="sql"/> with legacy Sybase syntax
    /// normalised to modern T-SQL that <c>TSql160Parser</c> accepts.
    /// </summary>
    /// <param name="sql">Raw SQL source from a stored procedure file.</param>
    /// <returns>Normalised SQL string (new allocation; original is unchanged).</returns>
    public static string Normalize(string sql)
    {
        var result = LegacyRaiserrorPattern.Replace(sql, static m =>
        {
            var msgNumber  = m.Groups[1].Value.Trim();
            var expression = m.Groups[2].Value.Trim();

            var payload = string.IsNullOrEmpty(expression) ? msgNumber : expression;
            return $"RAISERROR({payload}, 16, 1)";
        });

        result = ArithAbortNumericTruncationPattern.Replace(result, string.Empty);

        result = DoubleQuotedStringPattern.Replace(result, static m =>
        {
            var content = m.Groups[1].Value;
            return $"'{content}'";
        });

        result = SybaseLeftOuterJoinPattern.Replace(result, "=");
        result = SybaseRightOuterJoinPattern.Replace(result, "=");
        result = CursorForReadOnlyPattern.Replace(result, string.Empty);
        result = SetShowplanPattern.Replace(result, string.Empty);
        result = SetProcidPattern.Replace(result, string.Empty);
        result = SybaseMultiSetPattern.Replace(result, static m => "SELECT " + m.Groups[1].Value);

        return result;
    }
}
