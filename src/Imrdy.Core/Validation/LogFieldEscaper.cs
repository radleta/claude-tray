using System.Text;

namespace Imrdy.Core.Validation;

/// <summary>
/// Escapes a value for a one-line log record: CR as <c>\r</c>, LF as <c>\n</c>, every other
/// control character as <c>\xNN</c>.
/// <para>
/// imrdy's logs are one line per event and are read with grep, so a field carrying CR or LF
/// would end the record early and let the remainder forge a second line that parses as a
/// genuine record (CWE-117). Every value derived from something imrdy did not write itself
/// goes through this: a hook payload's fields, and — since the network exists — a peer's
/// machine name, its frame type and any reason built from them.
/// </para>
/// <para>
/// Escaped rather than stripped, deliberately. Stripping produces a clean-looking line and
/// destroys the only evidence that something tried to inject one; the escaped form keeps the
/// attempt greppable while making it inert.
/// </para>
/// </summary>
public static class LogFieldEscaper
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var needsEscape = false;
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                needsEscape = true;
                break;
            }
        }

        if (!needsEscape)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2"));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes and bounds a value that arrived off the wire. A machine name, a frame type or a
    /// session id can be up to the 64 KiB line cap, and a log record — or a link's
    /// <c>LastError</c>, or a row in the connections window — must not carry that.
    /// </summary>
    public static string EscapeBounded(string? value, int maxLength)
    {
        var escaped = Escape(value);
        return escaped.Length <= maxLength ? escaped : escaped[..maxLength] + "…";
    }
}
