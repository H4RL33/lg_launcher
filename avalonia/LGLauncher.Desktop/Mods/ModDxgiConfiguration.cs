using System.Text;

namespace LGLauncher;

internal enum ModDxgiSpoofing { Automatic, Enabled, Disabled }

internal static class ModDxgiConfiguration
{
    internal const int MaximumSize = 1024 * 1024;

    private const string HorizontalWhitespace = " \t";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static ModDxgiSpoofing Read(byte[] bytes) => ParseMode(ReadValue(bytes, "Spoofing", "Dxgi"));

    internal static string ReadReShade(byte[] bytes) => ReadValue(bytes, "Plugins", "LoadReshade");

    internal static byte[] WithReShade(byte[] bytes, string value)
    {
        ParseMode(value);
        var setting = FindSetting(bytes, "Plugins", "LoadReshade");
        return Replace(setting, bytes, value);
    }

    internal static byte[] WithMode(byte[] bytes, ModDxgiSpoofing mode)
    {
        var setting = FindSetting(bytes, "Spoofing", "Dxgi");
        if (ParseMode(setting.Text.AsSpan(setting.Start, setting.End - setting.Start)) == mode) return bytes;
        var value = mode switch
        {
            ModDxgiSpoofing.Automatic => "auto",
            ModDxgiSpoofing.Enabled => "true",
            ModDxgiSpoofing.Disabled => "false",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return Replace(setting, bytes, value);
    }

    private static byte[] Replace((string Text, int Start, int End) setting, byte[] bytes, string value)
    {
        var (text, start, end) = setting;
        if (text.AsSpan(start, end - start).SequenceEqual(value)) return bytes;
        var changed = String.Concat(text.AsSpan(0, start), value, text.AsSpan(end));
        var result = Utf8.GetBytes(changed);
        CheckSize(result);
        return result;
    }

    private static string ReadValue(byte[] bytes, string section, string key)
    {
        var (text, start, end) = FindSetting(bytes, section, key);
        return text[start..end];
    }

    private static (string Text, int Start, int End) FindSetting(byte[] bytes, string sectionName, string option)
    {
        CheckSize(bytes);
        string text;
        try
        {
            text = Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("The OptiScaler INI must be UTF-8.", error);
        }
        if (text.Contains('\0')) throw InvalidLayout(sectionName, option);

        var inSection = false;
        var foundSection = false;
        (int Start, int End)? setting = null;
        // Keep the BOM in the decoded text so replacing the value preserves it.
        var position = text.StartsWith('\uFEFF') ? 1 : 0;
        while (position < text.Length)
        {
            var line = text.AsSpan(position);
            var newline = line.IndexOfAny('\r', '\n');
            if (newline >= 0) line = line[..newline];
            var content = line.TrimStart(HorizontalWhitespace);
            var contentStart = position + line.Length - content.Length;
            position += line.Length;
            if (newline >= 0)
                position += text.AsSpan(position).StartsWith("\r\n", StringComparison.Ordinal) ? 2 : 1;

            content = content.TrimEnd(HorizontalWhitespace);
            if (content.IsEmpty || content[0] is ';' or '#') continue;

            if (content[0] == '[')
            {
                var closing = content.IndexOf(']');
                if (closing < 0) throw InvalidLayout(sectionName, option);
                var section = content[1..closing].Trim(HorizontalWhitespace);
                var suffix = content[(closing + 1)..].TrimStart(HorizontalWhitespace);
                if (section.IsEmpty || section.Contains('[') || (!suffix.IsEmpty && suffix[0] is not (';' or '#')))
                    throw InvalidLayout(sectionName, option);
                inSection = section.Equals(sectionName, StringComparison.OrdinalIgnoreCase);
                if (inSection && foundSection) throw InvalidLayout(sectionName, option);
                foundSection |= inSection;
                continue;
            }
            if (!inSection) continue;

            var separator = content.IndexOfAny('=', ':');
            var key = separator < 0 ? content : content[..separator].TrimEnd(HorizontalWhitespace);
            if (!key.Equals(option, StringComparison.OrdinalIgnoreCase)) continue;
            if (setting.HasValue || separator < 0 || content[separator] != '=') throw InvalidLayout(sectionName, option);

            // OptiScaler reads the entire value and treats trailing text as an invalid boolean.
            var value = content[(separator + 1)..].TrimStart(HorizontalWhitespace);
            if (!value.Equals("auto", StringComparison.OrdinalIgnoreCase) && !value.Equals("true", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("false", StringComparison.OrdinalIgnoreCase)) throw InvalidLayout(sectionName, option);
            var end = contentStart + content.Length;
            setting = (end - value.Length, end);
        }

        if (setting is not { } match) throw InvalidLayout(sectionName, option);
        return (text, match.Start, match.End);
    }

    private static ModDxgiSpoofing ParseMode(ReadOnlySpan<char> value)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return ModDxgiSpoofing.Automatic;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return ModDxgiSpoofing.Enabled;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return ModDxgiSpoofing.Disabled;
        throw new InvalidDataException("An OptiScaler boolean setting must use auto, true, or false.");
    }

    private static void CheckSize(byte[] bytes)
    {
        if (bytes.Length > MaximumSize) throw new InvalidDataException("The OptiScaler INI is too large.");
    }

    private static InvalidDataException InvalidLayout(string section, string option) =>
        new($"The OptiScaler INI must contain one valid {option} option in its {section} section.");
}
