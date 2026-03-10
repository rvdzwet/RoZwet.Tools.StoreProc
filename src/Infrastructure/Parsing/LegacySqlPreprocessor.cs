using System.Text.RegularExpressions;

namespace RoZwet.Tools.StoreProc.Infrastructure.Parsing;

/// <summary>
/// Normalises legacy Sybase / SQL Server 6.x syntax constructs that are rejected
/// by the modern <c>TSql160Parser</c> before the SQL is handed to ScriptDom.
///
/// Transformations applied (in order):
/// <list type="number">
///   <item>
///     <term>Legacy RAISERROR</term>
///     <description>
///       <c>RAISERROR &lt;msg_number&gt; &lt;expression&gt;</c>
///       → <c>RAISERROR(&lt;expression&gt;, 16, 1)</c>
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
/// </list>
///
/// The original SQL is never mutated — callers receive a new string.
/// Only the normalised copy is used for parsing; the original is preserved
/// verbatim for storage in the graph.
/// </summary>
internal static class LegacySqlPreprocessor
{
    private static readonly Regex LegacyRaiserrorPattern = new(
        @"(?i)\bRAISERROR\s+\d+\s+(.+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ArithAbortNumericTruncationPattern = new(
        @"(?i)\bSET\s+ARITHABORT\s+NUMERIC_TRUNCATION\s+(?:ON|OFF)\b[^\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DoubleQuotedStringPattern = new(
        @"""([^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Sybase left outer join: <c>expr1 *= expr2</c>.
    /// Matches <c>*=</c> when preceded by a word character (column name/closing paren),
    /// ensuring we do not corrupt the SQL Server compound assignment operator <c>*=</c>
    /// used in SET statements (which would be <c>SET @var *= expr</c> — also neutralised
    /// to <c>=</c> since we only need the table references, not the math).
    /// </summary>
    private static readonly Regex SybaseLeftOuterJoinPattern = new(
        @"\*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Sybase right outer join: <c>expr1 =* expr2</c>.
    /// Matches <c>=*</c> only when followed by a non-equals character so that
    /// <c>==</c> or normal assignments are not affected.
    /// </summary>
    private static readonly Regex SybaseRightOuterJoinPattern = new(
        @"=\*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Strips Sybase-specific cursor options that follow the SELECT body:
    /// <c>FOR READ ONLY</c> and <c>FOR BROWSE</c>.
    /// T-SQL 160 uses <c>READ_ONLY</c> inside the cursor declaration header, not here.
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
    /// Returns a version of <paramref name="sql"/> with legacy Sybase syntax
    /// normalised to modern T-SQL that <c>TSql160Parser</c> accepts.
    /// </summary>
    /// <param name="sql">Raw SQL source from a stored procedure file.</param>
    /// <returns>Normalised SQL string (new allocation; original is unchanged).</returns>
    public static string Normalize(string sql)
    {
        var result = LegacyRaiserrorPattern.Replace(sql, static m =>
        {
            var expression = m.Groups[1].Value.TrimEnd();
            return $"RAISERROR({expression}, 16, 1)";
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

        return result;
    }
}
