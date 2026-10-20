using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace QoderSecs.Core.Messages;

public sealed record ParsedSml(byte Stream, byte Function, bool ReplyExpected, XElement Structure);

/// <summary>
/// 解析一条 SML 文本。同时接受标准写法（S1F1 W、双引号、可省略 [个数]）
/// 和 Secs4Net SmlWriter 的写法（'S1F1'W、单引号、带 [个数]）。
/// </summary>
public static class SmlParser
{
    // 只写实测确认过的 Maps Type：见 SecsExpress s6800.sxml。其余类型不写 Maps，SecsExpress 也能打开。
    private static readonly Dictionary<SecsItemType, int> MapsTypeCodes = new()
    {
        [SecsItemType.U1] = 16,
        [SecsItemType.U2] = 32,
        [SecsItemType.U4] = 64,
        [SecsItemType.B] = 1024,
        [SecsItemType.Boolean] = 2048,
        [SecsItemType.A] = 4096
    };

    public static bool TryParse(string sml, out ParsedSml? parsed, out string? error)
    {
        parsed = null;
        error = null;
        try
        {
            parsed = new Reader(Tokenize(sml)).ParseMessage();
            return true;
        }
        catch (SmlParseException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static List<Token> Tokenize(string sml)
    {
        var tokens = new List<Token>();
        var line = 1;
        var index = 0;
        while (index < sml.Length)
        {
            var c = sml[index];
            if (c == '\n')
            {
                line++;
                index++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }

            if (c is '<')
            {
                tokens.Add(new Token(TokenKind.Open, "<", line));
                index++;
                continue;
            }

            if (c is '>')
            {
                tokens.Add(new Token(TokenKind.Close, ">", line));
                index++;
                continue;
            }

            if (c is '"' or '\'')
            {
                var end = sml.IndexOf(c, index + 1);
                if (end < 0)
                {
                    throw new SmlParseException($"第 {line} 行：字符串缺少结束的 {c} 引号。");
                }

                var text = sml[(index + 1)..end];
                line += text.Count(ch => ch == '\n');
                tokens.Add(new Token(TokenKind.String, text, line));
                index = end + 1;
                continue;
            }

            var start = index;
            while (index < sml.Length && !IsDelimiter(sml[index]))
            {
                index++;
            }

            var bare = sml[start..index];
            if (bare == ".")
            {
                tokens.Add(new Token(TokenKind.Dot, ".", line));
                continue;
            }

            if (bare.EndsWith('.'))
            {
                tokens.Add(new Token(TokenKind.Text, bare[..^1], line));
                tokens.Add(new Token(TokenKind.Dot, ".", line));
                continue;
            }

            tokens.Add(new Token(TokenKind.Text, bare, line));
        }

        return tokens;
    }

    private static bool IsDelimiter(char c) => c is '<' or '>' or '"' or '\'' || char.IsWhiteSpace(c);

    private enum TokenKind
    {
        Text,
        String,
        Open,
        Close,
        Dot
    }

    private readonly record struct Token(TokenKind Kind, string Value, int Line);

    private sealed class SmlParseException(string message) : Exception(message);

    private sealed class Reader(List<Token> tokens)
    {
        private static readonly Regex HeaderPattern = new(@"^[Ss](?<stream>\d{1,3})[Ff](?<function>\d{1,3})(?<wbit>[Ww])?$");
        private static readonly Regex CountPattern = new(@"^\[\d+\]$");

        private readonly List<Token> _tokens = tokens;
        private int _position;

        public ParsedSml ParseMessage()
        {
            var header = Next("消息头");
            if (header.Kind is not (TokenKind.Text or TokenKind.String))
            {
                throw Fail(header, "消息头必须写成 S1F1 或 S1F1 W。");
            }

            var match = HeaderPattern.Match(header.Value);
            if (!match.Success)
            {
                throw Fail(header, $"消息头「{header.Value}」不是 SxFy 形式。");
            }

            var stream = int.Parse(match.Groups["stream"].Value, CultureInfo.InvariantCulture);
            var function = int.Parse(match.Groups["function"].Value, CultureInfo.InvariantCulture);
            if (stream is < 1 or > 255 || function is < 1 or > 255)
            {
                throw Fail(header, $"「{header.Value}」的 S 和 F 必须在 1 到 255 之间。");
            }

            var replyExpected = match.Groups["wbit"].Success;
            if (!replyExpected && Peek() is { Kind: TokenKind.Text } wBit && wBit.Value.Equals("W", StringComparison.OrdinalIgnoreCase))
            {
                Advance();
                replyExpected = true;
            }

            XElement? item = null;
            if (Peek() is { Kind: TokenKind.Open })
            {
                item = ParseItem();
            }

            if (Peek() is { Kind: TokenKind.Dot })
            {
                Advance();
            }

            if (Peek() is { } rest)
            {
                throw Fail(rest, rest.Kind is TokenKind.Open
                    ? "只支持粘贴一条消息，请去掉后面的内容。"
                    : $"「{rest.Value}」出现在消息结束之后，只支持粘贴一条消息。");
            }

            var structure = new XElement("Structure");
            if (item is not null)
            {
                structure.Add(item);
            }

            return new ParsedSml((byte)stream, (byte)function, replyExpected, structure);
        }

        private XElement ParseItem()
        {
            Advance();
            var nameToken = Next("元素类型");
            if (nameToken.Kind is not TokenKind.Text)
            {
                throw Fail(nameToken, "< 后面必须是元素类型名，例如 <A 或 <L。");
            }

            // 个数标注可以紧贴类型名（<A[4]'x'>）也可以分开写（<A [4] 'x'>），两种都忽略。
            var bracket = nameToken.Value.IndexOf('[');
            var name = bracket < 0 ? nameToken.Value : nameToken.Value[..bracket];
            if (!SecsItemTypes.TryParse(name, out var type))
            {
                throw Fail(nameToken, $"未知元素类型「{name}」，v1 不支持。");
            }

            if (bracket >= 0 && !CountPattern.IsMatch(nameToken.Value[bracket..]))
            {
                throw Fail(nameToken, $"「{nameToken.Value[bracket..]}」不是合法的个数标注，应写成 [4]。");
            }

            SecsItemTypes.TryGetElementName(name, out var elementName);
            SkipCount();

            var element = new XElement(elementName);
            if (type is SecsItemType.List)
            {
                while (true)
                {
                    var token = Next("列表的结束符 >");
                    if (token.Kind is TokenKind.Close)
                    {
                        break;
                    }

                    if (token.Kind is not TokenKind.Open)
                    {
                        throw Fail(token, $"列表里出现意外的内容「{token.Value}」。");
                    }

                    Back();
                    element.Add(ParseItem());
                }

                return element;
            }

            var values = ReadValueTokens(elementName);
            element.Add(new XElement("Value", ReadValueText(type, values, elementName)));
            if (MapsTypeCodes.TryGetValue(type, out var code))
            {
                element.Add(new XElement("Maps", new XAttribute("Type", code)));
            }

            var validationError = new StructureNode(element).ValidationError;
            if (validationError is not null)
            {
                throw Fail(values.Count == 0 ? nameToken : values[0], validationError);
            }

            return element;
        }

        private void SkipCount()
        {
            if (Peek() is not { Kind: TokenKind.Text } token)
            {
                return;
            }

            if (!token.Value.StartsWith('['))
            {
                return;
            }

            if (!CountPattern.IsMatch(token.Value))
            {
                throw Fail(token, $"「{token.Value}」不是合法的个数标注，应写成 [2]。");
            }

            Advance();
        }

        private List<Token> ReadValueTokens(string elementName)
        {
            var values = new List<Token>();
            while (true)
            {
                var token = Next($"{elementName} 的结束符 >");
                if (token.Kind is TokenKind.Close)
                {
                    return values;
                }

                if (token.Kind is TokenKind.Open)
                {
                    throw Fail(token, $"{elementName} 里不能再嵌套元素。");
                }

                if (token.Kind is TokenKind.Dot)
                {
                    throw Fail(token, $"{elementName} 缺少结束符 >。");
                }

                values.Add(token);
            }
        }

        /// <summary>
        /// 值怎么落成一段文本：B 拼十六进制，A/J 只准一段，数值和 Boolean 把多个值用空格连起来
        /// 存进同一个 &lt;Value&gt;（跟 SecsExpress 的存法一致，数组不发明第二种 XML 形状）。
        /// </summary>
        private static string ReadValueText(SecsItemType type, List<Token> values, string elementName) => type switch
        {
            SecsItemType.B => ReadBinary(values),
            SecsItemType.A or SecsItemType.J => ReadPlainText(values, elementName),
            _ => string.Join(' ', values.Select(value => value.Value))
        };

        private static string ReadPlainText(List<Token> values, string elementName)
        {
            if (values.Count == 0)
            {
                return string.Empty;
            }

            if (values.Count > 1)
            {
                throw new SmlParseException(
                    $"第 {values[1].Line} 行：{elementName} 是一段字节文本，只能有一个值；[个数] 写的是它的字节数，不是元素个数。");
            }

            return values[0].Value;
        }

        private static string ReadBinary(List<Token> values)
        {
            var bytes = new List<byte>();
            foreach (var token in values)
            {
                var text = token.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token.Value[2..] : token.Value;

                // 手写模板里常见 <B 0x0>，按一个字节 0x00 处理。
                if (text.Length % 2 != 0)
                {
                    text = "0" + text;
                }

                if (!text.All(Uri.IsHexDigit))
                {
                    throw new SmlParseException($"第 {token.Line} 行：「{token.Value}」不是合法的 B 值，应写成 0x1F 这样的十六进制。");
                }

                for (var offset = 0; offset < text.Length; offset += 2)
                {
                    bytes.Add(byte.Parse(text.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                }
            }

            return string.Concat(bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        }

        private Token Next(string what)
        {
            if (_position >= _tokens.Count)
            {
                throw new SmlParseException($"SML 提前结束，缺少{what}。");
            }

            return _tokens[_position++];
        }

        private Token? Peek() => _position < _tokens.Count ? _tokens[_position] : null;

        private void Advance() => _position++;

        private void Back() => _position--;

        private static SmlParseException Fail(Token token, string message) =>
            new($"第 {token.Line} 行：{message}");
    }
}
