namespace DreamBooks.Pdf;

/// <summary>
/// Minimal PDF tokenizer used by <see cref="PdfObjectStreamPacker"/> to find where an indirect
/// object's body ends. It respects literal strings (balanced parentheses, backslash escapes),
/// hex strings, names, comments and nested dictionaries/arrays, and accepts only the tokens a
/// plain object body may contain (numbers, names, strings, <c>true</c>, <c>false</c>,
/// <c>null</c>, <c>R</c>). Anything else throws <see cref="PdfPackException"/>.
/// </summary>
internal static class PdfObjectScanner
{
    /// <summary>
    /// Scans from <paramref name="start"/> (just after "N G obj") to the first top-level
    /// <c>endobj</c> or <c>stream</c> keyword and returns its index. <paramref name="isStream"/>
    /// is true for <c>stream</c>, which is accepted only right after a top-level dictionary.
    /// </summary>
    internal static int FindBodyEnd(ReadOnlySpan<byte> data, int start, out bool isStream)
    {
        // Open containers: '<' for a dictionary, '[' for an array.
        Span<byte> stack = stackalloc byte[64];
        int depth = 0;
        bool lastWasTopLevelDictionary = false;
        int i = start;
        while (true)
        {
            i = SkipWhitespaceAndComments(data, i);
            if (i >= data.Length)
            {
                throw new PdfPackException("object body has no end");
            }

            byte c = data[i];
            bool closedTopLevelDictionary = false;
            switch (c)
            {
                case (byte)'(':
                    i = SkipLiteralString(data, i);
                    break;
                case (byte)'<' when i + 1 < data.Length && data[i + 1] == '<':
                    Push(stack, ref depth, (byte)'<');
                    i += 2;
                    break;
                case (byte)'<':
                    i = SkipHexString(data, i);
                    break;
                case (byte)'>' when i + 1 < data.Length && data[i + 1] == '>':
                    Pop(stack, ref depth, (byte)'<');
                    closedTopLevelDictionary = depth == 0;
                    i += 2;
                    break;
                case (byte)'[':
                    Push(stack, ref depth, (byte)'[');
                    i++;
                    break;
                case (byte)']':
                    Pop(stack, ref depth, (byte)'[');
                    i++;
                    break;
                case (byte)'/':
                    i = SkipRegular(data, i + 1);
                    break;
                default:
                    if (IsDelimiter(c))
                    {
                        throw new PdfPackException("object body has an unexpected delimiter");
                    }

                    int end = SkipRegular(data, i);
                    ReadOnlySpan<byte> token = data[i..end];
                    if (token.SequenceEqual("endobj"u8))
                    {
                        if (depth != 0)
                        {
                            throw new PdfPackException("object body is not balanced");
                        }

                        isStream = false;
                        return i;
                    }

                    if (token.SequenceEqual("stream"u8))
                    {
                        if (depth != 0 || !lastWasTopLevelDictionary)
                        {
                            throw new PdfPackException("stream keyword without a dictionary");
                        }

                        isStream = true;
                        return i;
                    }

                    if (!IsNumber(token) && !token.SequenceEqual("R"u8) && !token.SequenceEqual("true"u8)
                        && !token.SequenceEqual("false"u8) && !token.SequenceEqual("null"u8))
                    {
                        throw new PdfPackException("object body has an unexpected keyword");
                    }

                    i = end;
                    break;
            }

            lastWasTopLevelDictionary = closedTopLevelDictionary;
        }
    }

    /// <summary>
    /// Adds to <paramref name="found"/> every number in <paramref name="targets"/> that
    /// [<paramref name="start"/>, <paramref name="end"/>) refers to with an "N G R" reference.
    /// Literal and hex strings, names and comments are skipped, so a number written there is not
    /// a reference. The range must hold PDF objects (never stream data).
    /// </summary>
    internal static void FindReferences(
        ReadOnlySpan<byte> data, int start, int end, IReadOnlySet<int> targets, ISet<int> found)
    {
        ReadOnlySpan<byte> range = data[..end];
        long previous = -1; // the integer before the last token
        long last = -1; // the last token, if it was an unsigned integer
        int i = start;
        while (true)
        {
            i = SkipWhitespaceAndComments(range, i);
            if (i >= range.Length)
            {
                return;
            }

            byte c = range[i];
            if (IsDelimiter(c))
            {
                i = c switch
                {
                    (byte)'(' => SkipLiteralString(range, i),
                    (byte)'<' when i + 1 < range.Length && range[i + 1] == '<' => i + 2,
                    (byte)'<' => SkipHexString(range, i),
                    (byte)'/' => SkipRegular(range, i + 1),
                    _ => i + 1,
                };
                previous = last = -1;
                continue;
            }

            int tokenEnd = SkipRegular(range, i);
            ReadOnlySpan<byte> token = range[i..tokenEnd];
            if (token.SequenceEqual("R"u8) && previous >= 0 && last >= 0 && targets.Contains((int)previous))
            {
                found.Add((int)previous);
            }

            previous = last;
            last = ParseSmallUnsigned(token);
            i = tokenEnd;
        }
    }

    /// <summary>The value of an unsigned integer token of at most 9 digits, else -1.</summary>
    private static long ParseSmallUnsigned(ReadOnlySpan<byte> token)
    {
        if (token.Length is 0 or > 9)
        {
            return -1;
        }

        long value = 0;
        foreach (byte b in token)
        {
            if (!char.IsAsciiDigit((char)b))
            {
                return -1;
            }

            value = (value * 10) + (b - '0');
        }

        return value;
    }

    /// <summary>PDF white-space characters (ISO 32000-1, table 1).</summary>
    internal static bool IsWhitespace(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    internal static bool IsDelimiter(byte c) =>
        c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    internal static int SkipWhitespaceAndComments(ReadOnlySpan<byte> data, int i)
    {
        while (i < data.Length)
        {
            if (IsWhitespace(data[i]))
            {
                i++;
            }
            else if (data[i] == '%')
            {
                while (i < data.Length && data[i] != '\r' && data[i] != '\n')
                {
                    i++;
                }
            }
            else
            {
                break;
            }
        }

        return i;
    }

    /// <summary>Index just past the run of regular characters starting at <paramref name="i"/>.</summary>
    internal static int SkipRegular(ReadOnlySpan<byte> data, int i)
    {
        while (i < data.Length && !IsWhitespace(data[i]) && !IsDelimiter(data[i]))
        {
            i++;
        }

        return i;
    }

    /// <summary>Index just past the literal string that opens at <paramref name="i"/>.</summary>
    internal static int SkipLiteralString(ReadOnlySpan<byte> data, int i)
    {
        int nesting = 0;
        for (; i < data.Length; i++)
        {
            byte c = data[i];
            if (c == '\\')
            {
                i++; // the escaped byte (or the first byte of an escaped EOL / octal code)
            }
            else if (c == '(')
            {
                nesting++;
            }
            else if (c == ')' && --nesting == 0)
            {
                return i + 1;
            }
        }

        throw new PdfPackException("literal string is not terminated");
    }

    /// <summary>Index just past the hex string that opens at <paramref name="i"/>.</summary>
    internal static int SkipHexString(ReadOnlySpan<byte> data, int i)
    {
        for (i++; i < data.Length; i++)
        {
            byte c = data[i];
            if (c == '>')
            {
                return i + 1;
            }

            if (!IsWhitespace(c) && !char.IsAsciiHexDigit((char)c))
            {
                throw new PdfPackException("hex string has a non-hex character");
            }
        }

        throw new PdfPackException("hex string is not terminated");
    }

    internal static bool IsNumber(ReadOnlySpan<byte> token)
    {
        int i = token.Length > 0 && (token[0] == '+' || token[0] == '-') ? 1 : 0;
        bool digits = false;
        bool dot = false;
        for (; i < token.Length; i++)
        {
            if (char.IsAsciiDigit((char)token[i]))
            {
                digits = true;
            }
            else if (token[i] == '.' && !dot)
            {
                dot = true;
            }
            else
            {
                return false;
            }
        }

        return digits;
    }

    private static void Push(Span<byte> stack, ref int depth, byte kind)
    {
        if (depth == stack.Length)
        {
            throw new PdfPackException("object body is nested too deeply");
        }

        stack[depth++] = kind;
    }

    private static void Pop(Span<byte> stack, ref int depth, byte kind)
    {
        if (depth == 0 || stack[depth - 1] != kind)
        {
            throw new PdfPackException("object body is not balanced");
        }

        depth--;
    }
}
