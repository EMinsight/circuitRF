// brief-emi-4 R-emi4-0a — the only code that puts text into EMerge's input. Every string — a key or a value, a name from a
// user's file or a word of the format — goes through PyStr; everything else this writer emits is punctuation, a number in
// the invariant culture, or true/false/null. A source scan (EmergeLoweringTests) holds that: no interpolated string in the
// writer's files, and Raw only ever handed a literal.

using System.Globalization;
using System.Text;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static partial class EmergeScriptWriter
{
    /// <summary>
    /// R-emi4-0a — <paramref name="text"/> as a string literal: <c>"…"</c> with every character outside printable ASCII
    /// written <c>\uXXXX</c> (one per UTF-16 unit, so a character past U+FFFF is its surrogate pair, which a JSON reader
    /// joins), and <c>"</c> and <c>\</c> escaped. Python's <c>json</c> module reads it back to exactly
    /// <paramref name="text"/>, and nothing in it can end the literal early.
    /// </summary>
    public static string PyStr(string text)
    {
        var b = new StringBuilder(text.Length + 2);
        b.Append('"');
        foreach (char c in text)
        {
            if (c == '"') b.Append("\\\"");
            else if (c == '\\') b.Append("\\\\");
            else if (c >= ' ' && c <= '~') b.Append(c);
            else b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }
        return b.Append('"').ToString();
    }

    /// <summary>A number as the input carries it: shortest round-trip, invariant culture. A non-finite one is a bug in the
    /// lowering (the format has no spelling for it), never written.</summary>
    internal static string Num(double v)
        => double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture)
                              : throw new InvalidOperationException("EMerge's input has no spelling for a non-finite number.");

    /// <summary>
    /// A small JSON writer, two-space indented, one member per line; an array of numbers stays on one line. Keys and string
    /// values are <see cref="PyStr"/>'s; it has no other way to write text.
    /// </summary>
    internal sealed class Emitter
    {
        private readonly StringBuilder _b = new();
        private readonly Stack<(bool Inline, bool First)> _open = new();
        private bool _afterKey;

        public override string ToString() => _b.ToString();

        private void Raw(string token) => _b.Append(token);
        private void Raw(char token) => _b.Append(token);

        /// <summary>Before a value: the separator and, outside an inline array, the line break and indent.</summary>
        private void Before()
        {
            if (_afterKey) { _afterKey = false; return; }
            if (_open.Count == 0) return;
            var (inline, first) = _open.Pop();
            if (!first) Raw(',');
            if (inline) { if (!first) Raw(' '); }
            else NewLine(_open.Count + 1);
            _open.Push((inline, false));
        }

        private void NewLine(int depth)
        {
            Raw('\n');
            for (int i = 0; i < depth; i++) Raw("  ");
        }

        public Emitter Key(string key)
        {
            Before();
            Raw(PyStr(key));
            Raw(": ");
            _afterKey = true;
            return this;
        }

        public Emitter BeginObject() { Before(); Raw('{'); _open.Push((false, true)); return this; }

        public Emitter BeginArray(bool inline = false) { Before(); Raw('['); _open.Push((inline, true)); return this; }

        public Emitter EndObject() => End('}');

        public Emitter EndArray() => End(']');

        private Emitter End(char close)
        {
            var (inline, first) = _open.Pop();
            if (!inline && !first) NewLine(_open.Count);
            Raw(close);
            return this;
        }

        public Emitter Str(string value) { Before(); Raw(PyStr(value)); return this; }

        public Emitter Number(double value) { Before(); Raw(Num(value)); return this; }

        public Emitter Int(long value) { Before(); Raw(value.ToString(CultureInfo.InvariantCulture)); return this; }

        public Emitter Bool(bool value) { Before(); Raw(value ? "true" : "false"); return this; }

        public Emitter Null() { Before(); Raw("null"); return this; }

        public Emitter OptionalNumber(double? value) => value is { } v ? Number(v) : Null();

        public Emitter Point(Point3 p) => BeginArray(inline: true).Number(p.X).Number(p.Y).Number(p.Z).EndArray();

        public Emitter Numbers(IEnumerable<double> values)
        {
            BeginArray(inline: true);
            foreach (double v in values) Number(v);
            return EndArray();
        }

        public Emitter Ints(IEnumerable<int> values)
        {
            BeginArray(inline: true);
            foreach (int v in values) Int(v);
            return EndArray();
        }

        public Emitter Strings(IEnumerable<string> values)
        {
            BeginArray(inline: true);
            foreach (string v in values) Str(v);
            return EndArray();
        }

        /// <summary>Rings of points, each ring one line.</summary>
        public Emitter Rings(IEnumerable<IEnumerable<Point3>> rings)
        {
            BeginArray();
            foreach (var ring in rings)
            {
                BeginArray(inline: true);
                foreach (var p in ring) Point(p);
                EndArray();
            }
            return EndArray();
        }
    }
}
