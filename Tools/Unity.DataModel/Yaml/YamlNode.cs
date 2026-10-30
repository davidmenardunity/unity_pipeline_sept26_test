using System.Collections.Generic;
using System.Linq;

namespace Unity.DataModel.Yaml
{
    public enum YamlNodeKind
    {
        /// <summary>A plain or quoted value on one line.</summary>
        Scalar,
        /// <summary>A flow map, <c>{x: 0, y: 1}</c>; its entries are <see cref="YamlNode.Items"/>.</summary>
        Flow,
        /// <summary>A flow list, <c>[]</c> or <c>[a, b]</c>. Read only.</summary>
        FlowSequence,
        /// <summary>A block list (<c>- …</c> lines); its items are <see cref="YamlNode.Children"/>, keyed "0", "1"…</summary>
        Sequence,
        /// <summary>A block map; its keys are <see cref="YamlNode.Children"/>.</summary>
        Map,
        /// <summary>A key with nothing after it and nothing below it (an empty value).</summary>
        Block,
    }

    /// <summary>
    /// One value in a Unity YAML document, with where its text sits in the file (<see cref="Line"/>,
    /// [<see cref="Start"/>, <see cref="End"/>)), so an edit replaces exactly those characters.
    /// </summary>
    public sealed class YamlNode
    {
        internal YamlNode(string key, string raw, int line, int start, int end, int indent, YamlNodeKind kind)
        {
            Key = key;
            Raw = OriginalRaw = raw;
            Line = line;
            Start = start;
            End = end;
            Indent = indent;
            Kind = kind;
        }

        /// <summary>The key in its map, or the index ("0", "1"…) of a list item.</summary>
        public string Key { get; }

        /// <summary>The property path inside its document: keys and list indices joined with ".".</summary>
        public string Path { get; internal set; } = "";

        /// <summary>The value's text as written in the file, with this session's edits.</summary>
        public string Raw { get; internal set; }

        /// <summary>The value's text as it was read.</summary>
        public string OriginalRaw { get; internal set; }

        public int Line { get; internal set; }
        public int Start { get; internal set; }
        public int End { get; internal set; }
        public int Indent { get; }
        public YamlNodeKind Kind { get; internal set; }

        /// <summary>Map keys, or list items.</summary>
        public List<YamlNode> Children { get; } = new List<YamlNode>();

        /// <summary>A flow map's entries (each a scalar), or null.</summary>
        public List<YamlNode>? Items { get; internal set; }

        /// <summary>
        /// Values that span several lines (wrapped flow maps, multi-line strings), flow lists and anchors
        /// are read but not edited in place.
        /// </summary>
        public bool ReadOnly { get; internal set; }

        /// <summary>A list item (its <see cref="Key"/> is its index).</summary>
        public bool IsItem { get; internal set; }

        internal int? ContentIndent;
        internal int? ListIndent;
        internal int? EndLine;

        public YamlNode? Child(string key) => Children.FirstOrDefault(c => c.Key == key);
        public YamlNode? Item(string key) => Items?.FirstOrDefault(i => i.Key == key);

        /// <summary>The last line this value takes in the file (its deepest last child's).</summary>
        public int LastLine
        {
            get
            {
                var n = this;
                while (n.Children.Count > 0) n = n.Children[n.Children.Count - 1];
                return n.EndLine ?? n.Line;
            }
        }

        /// <summary>The value as plain text: unquoted, escapes resolved.</summary>
        public string Text => YamlText.Unquote(Raw);

        public override string ToString() => $"{Path}: {Raw}";
    }
}
