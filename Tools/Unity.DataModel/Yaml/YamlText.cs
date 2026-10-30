using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Unity.DataModel.Yaml
{
    /// <summary>Reading and writing scalar values the way Unity's YAML writer does.</summary>
    public static class YamlText
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Regex NeedsQuoteStart = new Regex(@"^[-?:,\[\]{}#&*!|>'""%@`]");
        static readonly Regex NeedsQuoteInside = new Regex(@": |\s#");
        static readonly Regex Number = new Regex(@"^-?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$");

        /// <summary>Plain text of a scalar: quotes removed, <c>''</c> and JSON escapes resolved.</summary>
        public static string Unquote(string? raw)
        {
            var v = (raw ?? "").Trim();
            if (v.Length >= 2 && v[0] == '\'' && v[v.Length - 1] == '\'') return v.Substring(1, v.Length - 2).Replace("''", "'");
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') return UnescapeDouble(v);
            return v;
        }

        static string UnescapeDouble(string v)
        {
            var sb = new StringBuilder();
            for (var i = 1; i < v.Length - 1; i++)
            {
                var c = v[i];
                if (c != '\\' || i + 1 >= v.Length - 1) { sb.Append(c); continue; }
                var e = v[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u' when i + 4 < v.Length - 1 && int.TryParse(v.Substring(i + 1, 4), NumberStyles.HexNumber, Inv, out var code):
                        sb.Append((char)code); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// A string written as Unity would: plain when that's unambiguous, else single-quoted. A value that
        /// was quoted before (<paramref name="previousRaw"/>) stays quoted.
        /// </summary>
        public static string FormatString(string text, string? previousRaw = null)
        {
            var wasQuoted = (previousRaw ?? "").TrimStart().StartsWith("'") || (previousRaw ?? "").TrimStart().StartsWith("\"");
            var needsQuotes = text != text.Trim() || NeedsQuoteStart.IsMatch(text) || NeedsQuoteInside.IsMatch(text)
                || (text.Length > 0 && text.Trim().Length == 0);
            if (!wasQuoted && !needsQuotes) return text;
            return "'" + text.Replace("'", "''") + "'";
        }

        /// <summary>Is this a number literal (as Unity writes them)?</summary>
        public static bool IsNumber(string? text) => text != null && Number.IsMatch(text.Trim());

        /// <summary>A scalar read as a number; 0 when it isn't one (Unity's reading of an empty value).</summary>
        public static double ToNumber(string? raw)
        {
            var v = Unquote(raw);
            if (v.Length == 0) return 0;
            return double.TryParse(v, NumberStyles.Float, Inv, out var d) && !double.IsNaN(d) && !double.IsInfinity(d) ? d : 0;
        }

        /// <summary>
        /// A number for the file: 8 significant digits, the shortest text that reads back as that, and 0 for
        /// anything smaller than 1e-7 (float noise).
        /// </summary>
        public static string FormatNumber(double v)
        {
            if (Math.Abs(v) < 1e-7) return "0";
            var rounded = double.Parse(v.ToString("G8", Inv), Inv);
            var s = rounded.ToString("R", Inv);
            if (s.IndexOf('E') < 0) return s;
            var abs = Math.Abs(rounded);
            if (abs >= 1e-6 && abs < 1e21) return rounded.ToString("0.##############################", Inv);
            // 5.4E-07 → 5.4e-7
            var at = s.IndexOf('E');
            var exp = int.Parse(s.Substring(at + 1), Inv);
            return s.Substring(0, at) + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString(Inv);
        }
    }
}
