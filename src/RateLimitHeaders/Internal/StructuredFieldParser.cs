using System.Text;

namespace RateLimitHeaders.Internal;

/// <summary>
/// The value type of a structured-field bare item (RFC 9651 section 3.3).
/// </summary>
internal enum StructuredFieldValueKind
{
    /// <summary>An integer of up to 15 digits, carried as <see cref="StructuredFieldValue.IntegerValue"/>.</summary>
    Integer,

    /// <summary>A decimal number, carried as <see cref="StructuredFieldValue.DecimalValue"/>.</summary>
    Decimal,

    /// <summary>A quoted string with its escapes decoded, carried as <see cref="StructuredFieldValue.Text"/>.</summary>
    String,

    /// <summary>An unquoted token, carried as <see cref="StructuredFieldValue.Text"/>.</summary>
    Token,

    /// <summary>
    /// A byte sequence, base64-decoded at parse time. <see cref="StructuredFieldValue.Bytes"/>
    /// carries the raw bytes; <see cref="StructuredFieldValue.Text"/> carries the bytes as a
    /// UTF-8 string when they decode cleanly and contain no control characters, otherwise the
    /// base64 text without the colon delimiters, so no byte value is lost and no control
    /// character can reach logs.
    /// </summary>
    ByteSequence,

    /// <summary>A boolean (<c>?0</c> / <c>?1</c>), carried as <see cref="StructuredFieldValue.BooleanValue"/>.</summary>
    Boolean,
}

/// <summary>
/// One parsed bare item value (RFC 9651 section 3.3).
/// </summary>
internal readonly struct StructuredFieldValue
{
    public StructuredFieldValueKind Kind { get; init; }

    /// <summary>The value for <see cref="StructuredFieldValueKind.Integer"/>.</summary>
    public long IntegerValue { get; init; }

    /// <summary>The value for <see cref="StructuredFieldValueKind.Decimal"/>.</summary>
    public double DecimalValue { get; init; }

    /// <summary>
    /// The text for <see cref="StructuredFieldValueKind.String"/>, <see cref="StructuredFieldValueKind.Token"/>,
    /// and <see cref="StructuredFieldValueKind.ByteSequence"/> (see the kind's own rule); null for the numeric kinds.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>The raw decoded bytes for <see cref="StructuredFieldValueKind.ByteSequence"/>.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>The value for <see cref="StructuredFieldValueKind.Boolean"/>.</summary>
    public bool BooleanValue { get; init; }
}

/// <summary>
/// One list member: a bare item plus its parameters, in listing order with duplicate keys
/// already resolved last-wins (RFC 9651 section 3.1.2).
/// </summary>
internal sealed class StructuredFieldItem
{
    public StructuredFieldItem(StructuredFieldValue value, List<KeyValuePair<string, StructuredFieldValue>> parameters)
    {
        Value = value;
        Parameters = parameters;
    }

    /// <summary>The item's own value (for a rate limit entry, the quoted policy name).</summary>
    public StructuredFieldValue Value { get; }

    /// <summary>The item's parameters in listing order; keys are unique (last occurrence wins).</summary>
    public IReadOnlyList<KeyValuePair<string, StructuredFieldValue>> Parameters { get; }

    /// <summary>
    /// Looks a parameter up by its exact key (case-sensitive, whole-key match per RFC 9651).
    /// </summary>
    public bool TryGetParameter(string key, out StructuredFieldValue value)
    {
        foreach (var parameter in Parameters)
        {
            if (string.Equals(parameter.Key, key, StringComparison.Ordinal))
            {
                value = parameter.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>
/// A strict RFC 9651 structured-field list parser. Any malformed member discards the entire
/// field value with no partial result, which is what keeps malformed hostile input from ever
/// becoming trusted throttling state (Decision 1 in PLAN-audit-fixes.md; finding AUD-09 in
/// TRACKER-adversarial-audit.md, the adversarial-audit findings ledger).
/// </summary>
/// <remarks>
/// Deliberate narrowing, documented here because each case voids the whole field: inner lists
/// (<c>(...)</c>), dates (<c>@</c>), and display strings (<c>%"..."</c>) are legal RFC 9651
/// members that no rate limit header uses; this parser rejects them instead of modeling them,
/// which produces the same outcome as the field-specific validity rules would (the field is
/// discarded).
/// </remarks>
internal static class StructuredFieldParser
{
    private const int MaxIntegerDigits = 15;
    private const int MaxDecimalIntegerDigits = 12;

    /// <summary>
    /// Parses a structured-field list. Returns false, with an empty list, when any part of
    /// the input is malformed.
    /// </summary>
    /// <param name="input">The raw field value.</param>
    /// <param name="items">The parsed members; empty when parsing fails.</param>
    public static bool TryParseList(string? input, out IReadOnlyList<StructuredFieldItem> items)
    {
        var result = new List<StructuredFieldItem>();
        items = result;

        if (input is null)
        {
            return false;
        }

        int position = 0;
        SkipSpaces(input, ref position);

        if (position >= input.Length)
        {
            // An empty (or all-space) field value is a valid, empty list (RFC 9651 section 4.2)
            return true;
        }

        while (true)
        {
            if (!TryParseItem(input, ref position, out var item))
            {
                result.Clear();
                return false;
            }

            result.Add(item);
            SkipOptionalWhitespace(input, ref position);

            if (position >= input.Length)
            {
                return true;
            }

            if (input[position] != ',')
            {
                result.Clear();
                return false;
            }

            position++;
            SkipOptionalWhitespace(input, ref position);

            if (position >= input.Length)
            {
                // A trailing comma is a parse error (RFC 9651 section 4.2.1)
                result.Clear();
                return false;
            }
        }
    }

    private static bool TryParseItem(string input, ref int position, out StructuredFieldItem item)
    {
        item = null!;

        if (!TryParseBareItem(input, ref position, out var value))
        {
            return false;
        }

        if (!TryParseParameters(input, ref position, out var parameters))
        {
            return false;
        }

        item = new StructuredFieldItem(value, parameters);
        return true;
    }

    private static bool TryParseParameters(string input, ref int position, out List<KeyValuePair<string, StructuredFieldValue>> parameters)
    {
        parameters = [];
        Dictionary<string, int>? indexByKey = null;

        while (position < input.Length && input[position] == ';')
        {
            position++;
            SkipSpaces(input, ref position);

            if (!TryParseKey(input, ref position, out var key))
            {
                return false;
            }

            StructuredFieldValue value;
            if (position < input.Length && input[position] == '=')
            {
                position++;
                if (!TryParseBareItem(input, ref position, out value))
                {
                    return false;
                }
            }
            else
            {
                // A parameter without a value is boolean true (RFC 9651 section 3.1.2)
                value = new StructuredFieldValue { Kind = StructuredFieldValueKind.Boolean, BooleanValue = true };
            }

            // Duplicate keys: the last value overwrites the earlier entry in place, keeping
            // the original position (RFC 9651 section 4.2.3.2 step 7). The index keeps the
            // lookup O(1), so a header with thousands of parameters costs linear CPU, not
            // quadratic.
            indexByKey ??= new Dictionary<string, int>(StringComparer.Ordinal);
            if (indexByKey.TryGetValue(key, out int existingIndex))
            {
                parameters[existingIndex] = new KeyValuePair<string, StructuredFieldValue>(key, value);
            }
            else
            {
                indexByKey[key] = parameters.Count;
                parameters.Add(new KeyValuePair<string, StructuredFieldValue>(key, value));
            }
        }

        return true;
    }

    private static bool TryParseKey(string input, ref int position, out string key)
    {
        key = string.Empty;
        int start = position;

        if (position >= input.Length || !IsKeyFirstChar(input[position]))
        {
            return false;
        }

        position++;
        while (position < input.Length && IsKeyChar(input[position]))
        {
            position++;
        }

        key = input[start..position];
        return true;
    }

    private static bool TryParseBareItem(string input, ref int position, out StructuredFieldValue value)
    {
        value = default;

        if (position >= input.Length)
        {
            return false;
        }

        char first = input[position];
        return first switch
        {
            '"' => TryParseString(input, ref position, out value),
            ':' => TryParseByteSequence(input, ref position, out value),
            '?' => TryParseBoolean(input, ref position, out value),
            '-' or (>= '0' and <= '9') => TryParseNumber(input, ref position, out value),
            '*' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') => TryParseToken(input, ref position, out value),
            _ => false,
        };
    }

    private static bool TryParseString(string input, ref int position, out StructuredFieldValue value)
    {
        value = default;
        position++; // consume the opening quote
        var builder = new StringBuilder();

        while (position < input.Length)
        {
            char c = input[position];

            if (c == '\\')
            {
                // Only \" and \\ are legal escapes (RFC 9651 section 4.2.5)
                if (position + 1 >= input.Length)
                {
                    return false;
                }

                char next = input[position + 1];
                if (next != '"' && next != '\\')
                {
                    return false;
                }

                builder.Append(next);
                position += 2;
                continue;
            }

            if (c == '"')
            {
                position++;
                value = new StructuredFieldValue { Kind = StructuredFieldValueKind.String, Text = builder.ToString() };
                return true;
            }

            // Control characters fail the field. Characters above 0x7E are accepted: a
            // deliberate deviation from RFC 9651 (which allows only printable ASCII),
            // because servers emitting non-ASCII policy names arrive as Latin-1-decoded
            // chars through HttpClient (Decision 1 and the Open items section in
            // PLAN-audit-fixes.md).
            if (char.IsControl(c))
            {
                return false;
            }

            builder.Append(c);
            position++;
        }

        // The closing quote never came
        return false;
    }

    private static bool TryParseByteSequence(string input, ref int position, out StructuredFieldValue value)
    {
        value = default;
        position++; // consume the opening colon

        int end = input.IndexOf(':', position);
        if (end < 0)
        {
            return false;
        }

        var base64 = input[position..end];

        // RFC 9651 section 4.2.7 admits only the base64 alphabet between the colons.
        // Convert.TryFromBase64String silently skips whitespace, so the alphabet is checked
        // here first; this is also what guarantees the base64 fallback text below can never
        // carry a control character into logs.
        foreach (char c in base64)
        {
            if (!IsBase64Char(c))
            {
                return false;
            }
        }

        // RFC 9651 section 4.2.7 synthesizes missing padding instead of failing; no valid
        // base64 has a length of 1 mod 4.
        var padded = (base64.Length % 4) switch
        {
            1 => null,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => base64,
        };
        if (padded is null)
        {
            return false;
        }

        var buffer = new byte[padded.Length];
        if (!Convert.TryFromBase64String(padded, buffer, out int bytesWritten))
        {
            return false;
        }

        var bytes = buffer[..bytesWritten];
        position = end + 1;
        value = new StructuredFieldValue
        {
            Kind = StructuredFieldValueKind.ByteSequence,
            Bytes = bytes,
            Text = DecodeByteSequenceText(bytes, base64),
        };
        return true;
    }

    /// <summary>
    /// The bytes become text when they decode as valid UTF-8 and contain nothing that can
    /// alter how a log line renders; otherwise the base64 text (without colons) stands in,
    /// so no byte value is lost and nothing hostile reaches logs (design item 2 in
    /// PLAN-audit-fixes.md). Rejected beyond the control characters (Cc): the Unicode line
    /// and paragraph separators (Zl, Zp), which common log viewers render as line breaks,
    /// and format characters (Cf), which include the bidirectional overrides and zero-width
    /// characters used for display spoofing.
    /// </summary>
    private static string DecodeByteSequenceText(byte[] bytes, string base64)
    {
        try
        {
            var decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            foreach (char c in decoded)
            {
                if (char.IsControl(c))
                {
                    return base64;
                }

                var category = char.GetUnicodeCategory(c);
                if (category is System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator
                    or System.Globalization.UnicodeCategory.Format)
                {
                    return base64;
                }
            }

            return decoded;
        }
        catch (DecoderFallbackException)
        {
            return base64;
        }
    }

    private static bool IsBase64Char(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '=';

    private static bool TryParseBoolean(string input, ref int position, out StructuredFieldValue value)
    {
        value = default;
        position++; // consume the question mark

        if (position >= input.Length || (input[position] != '0' && input[position] != '1'))
        {
            return false;
        }

        value = new StructuredFieldValue { Kind = StructuredFieldValueKind.Boolean, BooleanValue = input[position] == '1' };
        position++;
        return true;
    }

    private static bool TryParseNumber(string input, ref int position, out StructuredFieldValue value)
    {
        value = default;
        bool negative = false;

        if (input[position] == '-')
        {
            negative = true;
            position++;
        }

        int digitsStart = position;
        while (position < input.Length && input[position] is >= '0' and <= '9')
        {
            position++;
        }

        int integerDigits = position - digitsStart;
        if (integerDigits == 0)
        {
            return false;
        }

        if (position < input.Length && input[position] == '.')
        {
            // Decimal: at most 12 integer digits and 1 to 3 fraction digits (RFC 9651 section 3.3.2)
            if (integerDigits > MaxDecimalIntegerDigits)
            {
                return false;
            }

            position++;
            int fractionStart = position;
            while (position < input.Length && input[position] is >= '0' and <= '9')
            {
                position++;
            }

            int fractionDigits = position - fractionStart;
            if (fractionDigits is < 1 or > 3)
            {
                return false;
            }

            var decimalText = input[(digitsStart - (negative ? 1 : 0))..position];
            value = new StructuredFieldValue
            {
                Kind = StructuredFieldValueKind.Decimal,
                DecimalValue = double.Parse(decimalText, System.Globalization.CultureInfo.InvariantCulture),
            };
            return true;
        }

        if (integerDigits > MaxIntegerDigits)
        {
            return false;
        }

        long magnitude = long.Parse(input[digitsStart..position], System.Globalization.CultureInfo.InvariantCulture);
        value = new StructuredFieldValue
        {
            Kind = StructuredFieldValueKind.Integer,
            IntegerValue = negative ? -magnitude : magnitude,
        };
        return true;
    }

    private static bool TryParseToken(string input, ref int position, out StructuredFieldValue value)
    {
        int start = position;
        position++; // the first character was validated by the dispatcher

        while (position < input.Length && IsTokenChar(input[position]))
        {
            position++;
        }

        value = new StructuredFieldValue { Kind = StructuredFieldValueKind.Token, Text = input[start..position] };
        return true;
    }

    private static bool IsKeyFirstChar(char c) => c is (>= 'a' and <= 'z') or '*';

    private static bool IsKeyChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.' or '*';

    private static bool IsTokenChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.'
            or '^' or '_' or '`' or '|' or '~' or ':' or '/';

    private static void SkipSpaces(string input, ref int position)
    {
        while (position < input.Length && input[position] == ' ')
        {
            position++;
        }
    }

    private static void SkipOptionalWhitespace(string input, ref int position)
    {
        while (position < input.Length && (input[position] == ' ' || input[position] == '\t'))
        {
            position++;
        }
    }
}
