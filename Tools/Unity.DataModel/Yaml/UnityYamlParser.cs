using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Unity.DataModel.Yaml
{
    /// <summary>A parsed file: its lines (kept as read), its line ending, and its documents.</summary>
    public sealed class YamlFile
    {
        internal YamlFile(string[] lines, string eol, List<YamlDocument> documents)
        {
            Lines = lines;
            Eol = eol;
            Documents = documents;
        }

        public IReadOnlyList<string> Lines { get; }
        public string Eol { get; }
        public IReadOnlyList<YamlDocument> Documents { get; }
    }

    /// <summary>
    /// Reads the subset of YAML Unity writes (scenes, prefabs, ScriptableObjects, materials). Not a general
    /// YAML parser: it remembers where every value sits in the text, so an edit replaces those characters
    /// and the rest of the file stays byte for byte.
    /// </summary>
    public static class UnityYamlParser
    {
        static readonly Regex Header = new Regex(@"^--- !u!(\d+) &(-?\d+)( stripped)?");
        static readonly Regex KeyValue = new Regex(@"^([^\s'""{}\[\]#:][^:]*?):(?: (.*)|$)");
        static readonly Regex OddQuoteAtEnd = new Regex(@"(^|[^'])('')*'$");
        static readonly Regex TrailingColon = new Regex(@":\s*$");

        public static YamlFile Parse(string text)
        {
            var eol = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = Regex.Split(text, "\r?\n");
            var docs = new List<YamlDocument>();
            YamlDocument? doc = null;
            List<YamlNode>? stack = null;
            // A value Unity wrapped onto the next lines (a long flow map or quoted string): those lines
            // belong to it, and it can't be edited in place.
            Wrapped? wrapped = null;

            for (var n = 0; n < lines.Length; n++)
            {
                var line = lines[n];
                if (wrapped != null)
                {
                    wrapped.Node.ReadOnly = true;
                    wrapped.Node.EndLine = n;
                    if (wrapped.Ends(line.Trim())) wrapped = null;
                    continue;
                }
                var header = Header.Match(line);
                if (header.Success)
                {
                    doc = new YamlDocument(int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture),
                        long.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture), header.Groups[3].Success, n);
                    docs.Add(doc);
                    stack = null;
                    continue;
                }
                if (doc == null || line.StartsWith("%") || line.Trim().Length == 0) continue;
                var indent = line.Length - line.TrimStart().Length;
                if (indent == 0)
                {
                    // "TypeName:" opens the document's body.
                    doc.TypeName = TrailingColon.Replace(line, "");
                    doc.Root = new YamlNode(doc.TypeName, "", n, 0, 0, 0, YamlNodeKind.Map) { ContentIndent = 2 };
                    stack = new List<YamlNode> { doc.Root };
                    continue;
                }
                if (stack == null) continue;
                var body = line.Substring(indent);

                if (body == "-" || body.StartsWith("- "))
                {
                    var parent = PopUntil(stack, c => AcceptsItem(c, indent));
                    if (parent == null) continue;
                    parent.ListIndent = indent;
                    parent.Kind = YamlNodeKind.Sequence;
                    var item = new YamlNode(parent.Children.Count.ToString(CultureInfo.InvariantCulture), "", n, indent, indent + 2, indent + 2, YamlNodeKind.Map)
                        { IsItem = true };
                    parent.Children.Add(item);
                    var rest = body.Length > 2 ? body.Substring(2) : "";
                    var kv = SplitKey(rest);
                    if (kv != null)
                    {
                        item.ContentIndent = indent + 2;
                        stack.Add(item);
                        AddKey(stack, item, line, n, indent + 2, kv.Value);
                    }
                    else SetValue(item, n, indent + 2, rest);
                    wrapped = WrapsFrom(item.Children.Count > 0 ? item.Children[item.Children.Count - 1] : item);
                    continue;
                }

                var pair = SplitKey(body);
                if (pair == null)
                {
                    // A continuation of a multi-line scalar: the value above can't be edited safely.
                    var last = LastScalar(stack);
                    if (last != null) last.ReadOnly = true;
                    continue;
                }
                var owner = PopUntil(stack, c => AcceptsKey(c, indent));
                if (owner == null) continue;
                AddKey(stack, owner, line, n, indent, pair.Value);
                wrapped = WrapsFrom(owner.Children[owner.Children.Count - 1]);
            }

            var parsed = docs.Where(d => d.Root != null).ToList();
            foreach (var d in parsed) AssignPaths(d.Root, "");
            return new YamlFile(lines, eol, parsed);
        }

        static void AssignPaths(YamlNode node, string path)
        {
            node.Path = path;
            if (node.Items != null)
                foreach (var it in node.Items) it.Path = Join(path, it.Key);
            foreach (var c in node.Children) AssignPaths(c, Join(path, c.Key));
        }

        static string Join(string a, string b) => a.Length == 0 ? b : a + "." + b;

        sealed class Wrapped
        {
            public Wrapped(YamlNode node, Func<string, bool> ends) { Node = node; Ends = ends; }
            public YamlNode Node { get; }
            public Func<string, bool> Ends { get; }
        }

        static int Depth(string s)
        {
            var depth = 0;
            foreach (var ch in s) depth += ch == '{' || ch == '[' ? 1 : ch == '}' || ch == ']' ? -1 : 0;
            return depth;
        }

        // Does this value carry on to the next line? Then return how to find its last line.
        static Wrapped? WrapsFrom(YamlNode? nd)
        {
            if (nd == null) return null;
            var v = (nd.Raw ?? "").Trim();
            if (nd.Kind == YamlNodeKind.Flow || nd.Kind == YamlNodeKind.FlowSequence)
            {
                var depth = Depth(v);
                if (depth <= 0) return null;
                nd.ReadOnly = true;
                nd.Items = null;
                return new Wrapped(nd, s => { depth += Depth(s); return depth <= 0; });
            }
            if (nd.Kind == YamlNodeKind.Scalar && v.Length > 0 && (v[0] == '\'' || v[0] == '"') && !Closed(v, v[0]))
            {
                var q = v[0];
                nd.ReadOnly = true;
                return new Wrapped(nd, s => s.EndsWith(q.ToString()) && (q != '\'' || OddQuoteAtEnd.IsMatch(s)));
            }
            return null;
        }

        // "key: value" (value may be empty). Keys are plain in Unity's files.
        static (string Key, string Value, int ValueOffset)? SplitKey(string s)
        {
            var m = KeyValue.Match(s);
            if (!m.Success) return null;
            var key = m.Groups[1].Value;
            return m.Groups[2].Success ? (key, m.Groups[2].Value, key.Length + 2) : (key, "", s.Length);
        }

        static void AddKey(List<YamlNode> stack, YamlNode parent, string line, int n, int indent, (string Key, string Value, int ValueOffset) kv)
        {
            parent.ContentIndent ??= indent;
            if (parent.Kind == YamlNodeKind.Block) parent.Kind = YamlNodeKind.Map;
            var start = indent + kv.ValueOffset;
            var child = new YamlNode(kv.Key, kv.Value, n, start, line.Length, indent, YamlNodeKind.Scalar);
            parent.Children.Add(child);
            if (kv.Value.Trim().Length == 0)
            {
                // Empty: either an empty string, or a block map/list on the lines below. Decided by what follows.
                child.Kind = YamlNodeKind.Block;
                child.End = start + kv.Value.Length;
                stack.Add(child);
            }
            else SetValue(child, n, start, kv.Value);
        }

        static void SetValue(YamlNode nd, int n, int start, string value)
        {
            nd.Line = n;
            nd.Start = start;
            nd.End = start + value.Length;
            nd.Raw = nd.OriginalRaw = value;
            var v = value.Trim();
            if (v.StartsWith("{"))
            {
                nd.Kind = YamlNodeKind.Flow;
                nd.Items = ParseFlow(value, start, n);
                if (nd.Items == null) nd.ReadOnly = true;
            }
            else if (v.StartsWith("["))
            {
                nd.Kind = YamlNodeKind.FlowSequence;
                nd.ReadOnly = true;
            }
            else
            {
                nd.Kind = YamlNodeKind.Scalar;
                if (v.Length > 0 && (v[0] == '\'' || v[0] == '"') && !Closed(v, v[0])) nd.ReadOnly = true;   // continues on the next lines
                if (v.StartsWith("|") || v.StartsWith(">") || v.StartsWith("&") || v.StartsWith("*") || v.StartsWith("!")) nd.ReadOnly = true;
            }
        }

        static bool Closed(string v, char q)
        {
            if (v.Length < 2 || v[v.Length - 1] != q) return false;
            if (q == '\'')
            {
                // '' is an escaped quote: the closing quote is the last one when the count is even.
                var inner = v.Substring(1, v.Length - 2);
                return !OddQuoteAtEnd.IsMatch(inner);
            }
            int i = v.Length - 2, slashes = 0;
            while (i > 0 && v[i] == '\\') { slashes++; i--; }
            return slashes % 2 == 0;
        }

        // {a: 1, b: 2} → entries with offsets into the line. Nested braces aren't editable.
        static List<YamlNode>? ParseFlow(string value, int start, int line)
        {
            int open = value.IndexOf('{'), close = value.LastIndexOf('}');
            if (close < open) return null;
            var inner = value.Substring(open + 1, close - open - 1);
            if (inner.IndexOfAny(new[] { '{', '}', '[', ']' }) >= 0) return null;
            var items = new List<YamlNode>();
            var partStart = start + open + 1;
            foreach (var part in inner.Split(','))
            {
                var colon = part.IndexOf(':');
                if (colon < 0) return null;
                var v = colon + 1;
                if (v < part.Length && part[v] == ' ') v++;
                var raw = part.Substring(v).TrimEnd();
                items.Add(new YamlNode(part.Substring(0, colon).Trim(), raw, line, partStart + v, partStart + v + raw.Length, 0, YamlNodeKind.Scalar));
                partStart += part.Length + 1;
            }
            return items;
        }

        static YamlNode? PopUntil(List<YamlNode> stack, Func<YamlNode, bool> ok)
        {
            while (stack.Count > 0 && !ok(stack[stack.Count - 1])) stack.RemoveAt(stack.Count - 1);
            return stack.Count > 0 ? stack[stack.Count - 1] : null;
        }

        static bool AcceptsKey(YamlNode c, int indent)
        {
            if (c.IsItem) return c.ContentIndent == indent;
            if (c.Indent == 0 && c.ContentIndent == 2) return indent == 2;   // the document's root
            if (c.Kind == YamlNodeKind.Sequence) return false;
            if (c.Kind != YamlNodeKind.Block && c.Kind != YamlNodeKind.Map) return false;
            return indent > c.Indent && (c.ContentIndent == null || c.ContentIndent == indent);
        }

        static bool AcceptsItem(YamlNode c, int indent)
        {
            if (c.Kind == YamlNodeKind.Block && c.Children.Count == 0) return indent >= c.Indent;
            if (c.Kind == YamlNodeKind.Sequence) return c.ListIndent == indent;
            return false;
        }

        static YamlNode? LastScalar(List<YamlNode> stack)
        {
            for (var i = stack.Count - 1; i >= 0; i--)
            {
                var children = stack[i].Children;
                var last = children.Count > 0 ? children[children.Count - 1] : null;
                if (last != null && (last.Kind == YamlNodeKind.Scalar || last.Kind == YamlNodeKind.Flow)) return last;
            }
            return null;
        }
    }
}
