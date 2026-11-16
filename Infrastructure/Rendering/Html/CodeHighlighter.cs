using System.Net;
using System.Text;

namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Rendering.Html;

/// <summary>
/// <c>code-highlight</c> 的确定性分词器：不引第三方依赖，按封闭语言枚举使用固定词表与固定分隔符。
/// 只产出受控 token（关键字/字符串/注释/数字/纯文本），所有文本统一 <see cref="WebUtility.HtmlEncode(string)"/>，
/// 标签与 class 名由代码固定，不为高亮新开未转义输出通道。
/// </summary>
public static class CodeHighlighter
{
    private enum TokenKind
    {
        Plain,
        Keyword,
        String,
        Comment,
        Number,
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    private sealed record LanguageSpec(
        string? LineComment,
        string? BlockCommentStart,
        string? BlockCommentEnd,
        char[] StringQuotes,
        HashSet<string> Keywords,
        bool RecognizeNumbers);

    private static readonly LanguageSpec CSharp = new(
        "//",
        "/*",
        "*/",
        ['"', '\''],
        Set(
            "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch",
            "char", "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
            "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed",
            "float", "for", "foreach", "get", "goto", "if", "implicit", "in", "init", "int",
            "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object",
            "operator", "out", "override", "params", "private", "protected", "public", "readonly",
            "record", "ref", "return", "sbyte", "sealed", "set", "short", "sizeof", "stackalloc",
            "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint",
            "ulong", "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile",
            "when", "while", "with", "yield"),
        true);

    private static readonly LanguageSpec JavaScript = new(
        "//",
        "/*",
        "*/",
        ['"', '\'', '`'],
        Set(
            "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger",
            "default", "delete", "do", "else", "export", "extends", "false", "finally", "for",
            "function", "if", "import", "in", "instanceof", "let", "new", "null", "of", "return",
            "static", "super", "switch", "this", "throw", "true", "try", "typeof", "undefined",
            "var", "void", "while", "with", "yield"),
        true);

    private static readonly LanguageSpec Json = new(
        null,
        null,
        null,
        ['"'],
        Set("true", "false", "null"),
        true);

    private static readonly LanguageSpec Bash = new(
        "#",
        null,
        null,
        ['"', '\''],
        Set(
            "alias", "case", "cd", "do", "done", "echo", "elif", "else", "esac", "exit", "export",
            "fi", "for", "function", "if", "in", "local", "readonly", "return", "select", "set",
            "shift", "source", "then", "time", "unset", "until", "while"),
        false);

    /// <summary>
    /// 写高亮输出。语言必须是 <c>UserBlockStructureRules</c> 的封闭枚举值（调用方已校验）；
    /// 空源文本零输出，行号开启时每行前缀 <c>hl-lineno</c> 受控标签。
    /// </summary>
    public static void Write(string source, string language, bool lineNumbers, StringBuilder html)
    {
        if (source.Length == 0)
        {
            return;
        }

        var tokens = Tokenize(source, language);
        if (!lineNumbers)
        {
            foreach (var token in tokens)
            {
                AppendSegment(html, token.Kind, token.Text);
            }

            return;
        }

        var line = 1;
        AppendLineNumber(html, line);
        foreach (var token in tokens)
        {
            var text = token.Text;
            var start = 0;
            while (true)
            {
                var newline = text.IndexOf('\n', start);
                var end = newline < 0 ? text.Length : newline;
                AppendSegment(html, token.Kind, text[start..end]);
                if (newline < 0)
                {
                    break;
                }

                line++;
                AppendLineNumber(html, line);
                start = newline + 1;
            }
        }
    }

    private static List<Token> Tokenize(string source, string language) => language switch
    {
        "csharp" => TokenizeCode(source, CSharp),
        "javascript" => TokenizeCode(source, JavaScript),
        "json" => TokenizeCode(source, Json),
        "bash" => TokenizeCode(source, Bash),
        "xml" => TokenizeXml(source),
        _ => [new Token(TokenKind.Plain, source)],
    };

    private static List<Token> TokenizeCode(string source, LanguageSpec spec)
    {
        var tokens = new List<Token>();
        var plain = new StringBuilder();
        var index = 0;

        while (index < source.Length)
        {
            if (spec.BlockCommentStart is { } blockStart && Matches(source, index, blockStart))
            {
                var close = source.IndexOf(spec.BlockCommentEnd!, index + blockStart.Length, StringComparison.Ordinal);
                var end = close < 0 ? source.Length : close + spec.BlockCommentEnd!.Length;
                AddToken(tokens, plain, TokenKind.Comment, source[index..end]);
                index = end;
                continue;
            }

            if (spec.LineComment is { } lineComment && Matches(source, index, lineComment))
            {
                var newline = source.IndexOf('\n', index);
                var end = newline < 0 ? source.Length : newline;
                AddToken(tokens, plain, TokenKind.Comment, source[index..end]);
                index = end;
                continue;
            }

            var current = source[index];
            if (Array.IndexOf(spec.StringQuotes, current) >= 0)
            {
                var end = ScanString(source, index, current);
                AddToken(tokens, plain, TokenKind.String, source[index..end]);
                index = end;
                continue;
            }

            if (spec.RecognizeNumbers && current is >= '0' and <= '9')
            {
                var end = ScanNumber(source, index);
                AddToken(tokens, plain, TokenKind.Number, source[index..end]);
                index = end;
                continue;
            }

            if (IsIdentifierStart(current))
            {
                var end = index + 1;
                while (end < source.Length && IsIdentifierPart(source[end]))
                {
                    end++;
                }

                var word = source[index..end];
                if (spec.Keywords.Contains(word))
                {
                    AddToken(tokens, plain, TokenKind.Keyword, word);
                }
                else
                {
                    plain.Append(word);
                }

                index = end;
                continue;
            }

            plain.Append(current);
            index++;
        }

        FlushPlain(tokens, plain);
        return tokens;
    }

    /// <summary>
    /// XML 只区分四类：注释、标签/属性名、引号值、其余文本。
    /// 标签外的文本按纯文本输出，避免把正文内容误标成关键字。
    /// </summary>
    private static List<Token> TokenizeXml(string source)
    {
        var tokens = new List<Token>();
        var plain = new StringBuilder();
        var index = 0;
        var insideTag = false;

        while (index < source.Length)
        {
            if (Matches(source, index, "<!--"))
            {
                var close = source.IndexOf("-->", index + 4, StringComparison.Ordinal);
                var end = close < 0 ? source.Length : close + 3;
                AddToken(tokens, plain, TokenKind.Comment, source[index..end]);
                index = end;
                continue;
            }

            var current = source[index];
            if (current == '<')
            {
                insideTag = true;
                plain.Append(current);
                index++;
                continue;
            }

            if (current == '>')
            {
                insideTag = false;
                plain.Append(current);
                index++;
                continue;
            }

            if (current is '"' or '\'')
            {
                var end = ScanString(source, index, current);
                AddToken(tokens, plain, TokenKind.String, source[index..end]);
                index = end;
                continue;
            }

            if (insideTag && IsIdentifierStart(current))
            {
                var end = index + 1;
                while (end < source.Length && IsNamePart(source[end]))
                {
                    end++;
                }

                AddToken(tokens, plain, TokenKind.Keyword, source[index..end]);
                index = end;
                continue;
            }

            plain.Append(current);
            index++;
        }

        FlushPlain(tokens, plain);
        return tokens;
    }

    private static int ScanString(string source, int start, char quote)
    {
        var index = start + 1;
        while (index < source.Length)
        {
            var value = source[index];
            if (value == '\\' && index + 1 < source.Length)
            {
                index += 2;
                continue;
            }

            // 未闭合字符串不跨行吞掉后续代码：换行即视为结束。
            if (value == quote || value == '\n')
            {
                return value == quote ? index + 1 : index;
            }

            index++;
        }

        return source.Length;
    }

    private static int ScanNumber(string source, int start)
    {
        var index = start + 1;
        while (index < source.Length)
        {
            var value = source[index];
            if (char.IsLetterOrDigit(value) || value is '.' or '_'
                || (value is '+' or '-' && source[index - 1] is 'e' or 'E'))
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }

    private static void AppendSegment(StringBuilder html, TokenKind kind, string text)
    {
        if (kind == TokenKind.Plain)
        {
            html.Append(WebUtility.HtmlEncode(text));
            return;
        }

        html.Append("<span class=\"").Append(ClassName(kind)).Append("\">")
            .Append(WebUtility.HtmlEncode(text))
            .Append("</span>");
    }

    private static string ClassName(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => "hl-keyword",
        TokenKind.String => "hl-string",
        TokenKind.Comment => "hl-comment",
        TokenKind.Number => "hl-number",
        _ => "hl-plain",
    };

    private static void AppendLineNumber(StringBuilder html, int line) =>
        html.Append("<span class=\"hl-lineno\">").Append(line).Append("</span>");

    private static void AddToken(List<Token> tokens, StringBuilder plain, TokenKind kind, string text)
    {
        FlushPlain(tokens, plain);
        tokens.Add(new Token(kind, text));
    }

    private static void FlushPlain(List<Token> tokens, StringBuilder plain)
    {
        if (plain.Length == 0)
        {
            return;
        }

        tokens.Add(new Token(TokenKind.Plain, plain.ToString()));
        plain.Clear();
    }

    private static bool Matches(string source, int index, string value) =>
        index + value.Length <= source.Length
        && string.CompareOrdinal(source, index, value, 0, value.Length) == 0;

    private static bool IsIdentifierStart(char value) =>
        value is '_' or '$' || char.IsLetter(value);

    private static bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '$';

    private static bool IsNamePart(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '-' or ':' or '.';

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
