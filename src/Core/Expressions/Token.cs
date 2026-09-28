using CircuitRF.Text;

namespace CircuitRF.Core.Expressions;

public enum TokenKind
{
    // Atoms. A Quantity is a number with a unit glued to it — `10um`, `2.4GHz`, `50Ω` — lexed as ONE
    // token so that nothing that walks identifier tokens (a rename) can mistake its unit for a name.
    Number, Identifier, Quantity,
    // Arithmetic
    Plus, Minus, Star, Slash, Caret,
    // Comparison
    Less, LessEqual, Greater, GreaterEqual, EqualEqual, BangEqual,
    // Logic
    AmpAmp, PipePipe, Bang,
    // Ternary / punctuation
    Question, Colon, Comma, Dot,
    LParen, RParen,
    // Indexing
    LBracket, RBracket, Tilde,
    // String literal: "foo"  (storage-only config params; no string operations)
    StringLiteral,
    // Sentinel
    Eof
}

public readonly struct Token(TokenKind kind, string text, int position, string? unit = null)
{
    public TokenKind Kind     { get; } = kind;
    public string    Text     { get; } = text;
    public int       Position { get; } = position;

    /// <summary>A <see cref="TokenKind.Quantity"/>'s unit, in its ENGINE spelling (<c>Ω</c> → <c>Ohm</c>,
    /// <c>µ</c> → <c>u</c>); null for every other kind. The number is <see cref="Text"/> up to <see cref="UnitStart"/>.</summary>
    public string?   Unit      { get; } = unit;

    /// <summary>Where the unit starts inside <see cref="Text"/> (a Quantity only).</summary>
    public int       UnitStart { get; init; }

    public override string ToString() => $"{Kind}({Text})@{Position}";
}

public sealed class Tokenizer(string source)
{
    // A decimal comma is rewritten to a decimal point HERE, once, at the lexical layer where the
    // question belongs — see NumericText.NormalizeDecimalSeparator for the rule and for why a comma
    // inside an argument list is left alone. The rewrite is one character for one character, so
    // every Token.Position below still indexes the character the user typed.
    private readonly string _source = NumericText.NormalizeDecimalSeparator(source);
    private int _pos;

    public Token[] Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var t = NextToken();
            tokens.Add(t);
            if (t.Kind == TokenKind.Eof) break;
        }
        return [.. tokens];
    }

    private Token NextToken()
    {
        SkipWhitespace();
        if (_pos >= _source.Length) return Make(TokenKind.Eof, "", _pos);

        int start = _pos;
        char c = _source[_pos];

        if (char.IsDigit(c) || (c == '.' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1])))
            return ReadNumber(start);

        if (char.IsLetter(c) || c == '_')
            return ReadIdentifier(start);

        if (c == '"')
            return ReadStringLiteral(start);

        return c switch
        {
            '+' => Advance(TokenKind.Plus,   start),
            '-' => Advance(TokenKind.Minus,  start),
            '*' => Advance(TokenKind.Star,   start),
            '/' => Advance(TokenKind.Slash,  start),
            '^' => Advance(TokenKind.Caret,  start),
            ',' => Advance(TokenKind.Comma,  start),
            '(' => Advance(TokenKind.LParen,   start),
            ')' => Advance(TokenKind.RParen,   start),
            '[' => Advance(TokenKind.LBracket, start),
            ']' => Advance(TokenKind.RBracket, start),
            '~' => Advance(TokenKind.Tilde,    start),
            '.' => Advance(TokenKind.Dot,     start),
            '?' => Advance(TokenKind.Question, start),
            ':' => Advance(TokenKind.Colon,  start),
            '<' => _pos + 1 < _source.Length && _source[_pos + 1] == '='
                        ? Advance2(TokenKind.LessEqual,    start)
                        : Advance(TokenKind.Less,          start),
            '>' => _pos + 1 < _source.Length && _source[_pos + 1] == '='
                        ? Advance2(TokenKind.GreaterEqual, start)
                        : Advance(TokenKind.Greater,       start),
            '=' => _pos + 1 < _source.Length && _source[_pos + 1] == '='
                        ? Advance2(TokenKind.EqualEqual,   start)
                        : throw new ParseException($"Unexpected '=' (did you mean '=='?)", start),
            '!' => _pos + 1 < _source.Length && _source[_pos + 1] == '='
                        ? Advance2(TokenKind.BangEqual,    start)
                        : Advance(TokenKind.Bang,          start),
            '&' => _pos + 1 < _source.Length && _source[_pos + 1] == '&'
                        ? Advance2(TokenKind.AmpAmp,       start)
                        : throw new ParseException("Expected '&&'", start),
            '|' => _pos + 1 < _source.Length && _source[_pos + 1] == '|'
                        ? Advance2(TokenKind.PipePipe,     start)
                        : throw new ParseException("Expected '||'", start),
            _ => throw new ParseException($"Unexpected character '{c}'", start)
        };
    }

    private Token ReadStringLiteral(int start)
    {
        _pos++; // skip opening "
        int contentStart = _pos;
        while (_pos < _source.Length && _source[_pos] != '"')
            _pos++;
        if (_pos >= _source.Length)
            throw new ParseException("Unterminated string literal", start);
        var content = _source[contentStart.._pos];
        _pos++; // skip closing "
        return Make(TokenKind.StringLiteral, content, start);
    }

    private Token ReadNumber(int start)
    {
        while (_pos < _source.Length && (char.IsDigit(_source[_pos]) || _source[_pos] == '.'))
            _pos++;
        // Optional exponent — taken only when digits follow it, so `2e` is not a number with an empty exponent
        // (which double.Parse refused with a FormatException no caller expected) but a number with an unknown suffix.
        if (_pos < _source.Length && (_source[_pos] == 'e' || _source[_pos] == 'E'))
        {
            int sign = _pos + 1 < _source.Length && (_source[_pos + 1] == '+' || _source[_pos + 1] == '-') ? 1 : 0;
            if (_pos + 1 + sign < _source.Length && char.IsDigit(_source[_pos + 1 + sign]))
            {
                _pos += 1 + sign;
                while (_pos < _source.Length && char.IsDigit(_source[_pos]))
                    _pos++;
            }
        }
        if (_pos < _source.Length && IsWordStart(_source[_pos]) && !IsImplicitImaginary(_pos))
            return ReadQuantity(start);
        return Make(TokenKind.Number, _source[start.._pos], start);
    }

    // `10j` is the implicit imaginary unit, and `10j2` was an error the parser named — both are left exactly as
    // they were: the number stops before the j. No unit starts with j.
    private bool IsImplicitImaginary(int at)
        => _source[at] == 'j' && (at + 1 >= _source.Length || !IsWordChar(_source[at + 1]) || char.IsDigit(_source[at + 1]));

    /// <summary>
    /// A number with a unit glued to it (no whitespace): <c>10um</c>, <c>1mil</c>, <c>1.5nH</c>, <c>50Ω</c>. The suffix
    /// is the whole identifier run after the number, so <c>10mils</c> is the unknown <c>mils</c>, never <c>10mil</c>
    /// followed by a name <c>s</c>. Whitespace separates: <c>2 GHz</c> is not a literal, it stays the assignment-level
    /// spelling <see cref="Units.LiftInlineUnit"/> lifts.
    /// </summary>
    private Token ReadQuantity(int start)
    {
        int unitStart = _pos;
        while (_pos < _source.Length && IsWordChar(_source[_pos]))
            _pos++;
        string written = _source[unitStart.._pos];
        string unit = UnitNormalizer.ToEngineUnit(written);
        if (Units.IsLogarithmic(unit))
            throw new ParseException(
                $"'{_source[start.._pos]}': a {unit} value is not a multiplier, so it cannot be a unit suffix; use {(unit == "dB" ? "dB" : "dBm")}(…) or a field whose unit is {unit}", unitStart);
        if (!Units.IsSuffixUnit(unit))
            throw new ParseException(
                $"'{written}' in '{_source[start.._pos]}' is not a unit this build knows. The length units are " +
                "metre, mm, um, nm, cm, mil, in; a bare 'm' is MILLI, not the metre", unitStart);
        return new Token(TokenKind.Quantity, _source[start.._pos], start, unit) { UnitStart = unitStart - start };
    }

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsWordChar(char c)  => char.IsLetterOrDigit(c) || c == '_';

    private Token ReadIdentifier(int start)
    {
        // Bare 'j' immediately followed by a digit: stop after 'j' so the tokenizer
        // produces two tokens (j, <number>), enabling the implicit j*n shorthand.
        if (_source[_pos] == 'j' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
        {
            _pos++;
            return Make(TokenKind.Identifier, "j", start);
        }
        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
            _pos++;
        return Make(TokenKind.Identifier, _source[start.._pos], start);
    }

    private Token Advance(TokenKind kind, int start)  { _pos++;     return Make(kind, _source[start.._pos], start); }
    private Token Advance2(TokenKind kind, int start) { _pos += 2;  return Make(kind, _source[start.._pos], start); }
    private static Token Make(TokenKind kind, string text, int pos) => new(kind, text, pos);

    private void SkipWhitespace()
    {
        while (_pos < _source.Length && char.IsWhiteSpace(_source[_pos]))
            _pos++;
    }
}
