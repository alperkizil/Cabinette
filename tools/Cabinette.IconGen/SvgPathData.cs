using System.Globalization;
using System.Text;

namespace Cabinette.IconGen;

/// <summary>
/// Rewrites SVG path data into the form WPF's path mini-language parser accepts: SVG allows compact numbers
/// ("1.5.5" is two numbers, "1-2" too) and arc flags without separators ("a1 1 0 011 1"), which WPF does not.
/// The fill rule prefix is added by the caller.
/// </summary>
internal static class SvgPathData
{
    public static string Normalize(string data)
    {
        var output = new StringBuilder(data.Length + 16);
        var i = 0;
        var command = '\0';
        var argIndex = 0;

        while (true)
        {
            SkipSeparators(data, ref i);
            if (i >= data.Length)
            {
                break;
            }

            var c = data[i];
            if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                if ("MmLlHhVvCcSsQqTtAaZz".IndexOf(c) < 0)
                {
                    throw new FormatException($"unknown path command '{c}'");
                }

                command = c;
                argIndex = 0;
                output.Append(c).Append(' ');
                i++;
                continue;
            }

            if (command == '\0')
            {
                throw new FormatException("path data must start with a command");
            }

            // Arc flags (4th and 5th argument of each 7-number arc group) are single digits.
            var isArc = command is 'A' or 'a';
            var position = argIndex % 7;
            if (isArc && position is 3 or 4)
            {
                if (c is not ('0' or '1'))
                {
                    throw new FormatException("bad arc flag");
                }

                output.Append(c).Append(' ');
                i++;
                argIndex++;
                continue;
            }

            var number = ReadNumber(data, ref i);
            output.Append(number.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
            argIndex++;
        }

        return output.ToString().TrimEnd();
    }

    private static void SkipSeparators(string data, ref int i)
    {
        while (i < data.Length && (char.IsWhiteSpace(data[i]) || data[i] == ','))
        {
            i++;
        }
    }

    private static double ReadNumber(string data, ref int i)
    {
        var start = i;
        if (i < data.Length && data[i] is '+' or '-')
        {
            i++;
        }

        var sawDigit = false;
        var sawDot = false;
        while (i < data.Length)
        {
            var c = data[i];
            if (char.IsAsciiDigit(c))
            {
                sawDigit = true;
                i++;
            }
            else if (c == '.' && !sawDot)
            {
                sawDot = true;
                i++;
            }
            else
            {
                break;
            }
        }

        if (sawDigit && i < data.Length && data[i] is 'e' or 'E')
        {
            var save = i;
            i++;
            if (i < data.Length && data[i] is '+' or '-')
            {
                i++;
            }

            if (i < data.Length && char.IsAsciiDigit(data[i]))
            {
                while (i < data.Length && char.IsAsciiDigit(data[i]))
                {
                    i++;
                }
            }
            else
            {
                i = save;
            }
        }

        if (!sawDigit)
        {
            throw new FormatException($"bad number at position {start}");
        }

        return double.Parse(data.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
