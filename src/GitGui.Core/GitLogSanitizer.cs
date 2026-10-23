using System.Text.RegularExpressions;

namespace GitGui.Core;

public static class GitLogSanitizer
{
    private static bool SecretOption(string value)
    {
        if (value == "-H") return true;
        if (!value.StartsWith("--", StringComparison.Ordinal)) return false;
        return value[2..].Replace('_', '-').ToLowerInvariant() is "password" or "passwd" or "token" or
            "access-token" or "accesstoken" or "secret" or "header" or "authorization" or "proxy-authorization";
    }

    private static bool SecretConfiguration(string name) => name.Split('.').Any(part =>
        part.Equals("credential", StringComparison.OrdinalIgnoreCase) || part.Equals("extraheader", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("proxy", StringComparison.OrdinalIgnoreCase) || part.Equals("password", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("passwd", StringComparison.OrdinalIgnoreCase) || part.Equals("token", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("secret", StringComparison.OrdinalIgnoreCase));

    // The command panel preserves full, copyable ordinary arguments. Secrets are
    // removed before shell quoting, so embedded quotes cannot expose a suffix.
    public static IReadOnlyList<string> DisplayArguments(IReadOnlyList<string> arguments)
    {
        var result = new List<string>(); var hideNext = false; var config = false;
        foreach (var value in arguments)
        {
            if (hideNext) { result.Add("***"); hideNext = false; continue; }
            var equals = value.IndexOf('=');
            if (equals > 0 && SecretOption(value[..equals])) { result.Add(value[..(equals + 1)] + "***"); continue; }
            var safe = Text(value);
            var safeEquals = safe.IndexOf('=');
            if (safeEquals > 0 && SecretConfiguration(safe[..safeEquals])) safe = safe[..(safeEquals + 1)] + "***";
            result.Add(safe);
            hideNext = SecretOption(value) || config && SecretConfiguration(value);
            if (value == "config") config = true;
        }
        return result;
    }

    public static string Text(string text)
    {
        text = Regex.Replace(text, @"((?:https?|ssh|git)://)[^\s/@]+@", "$1***@", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"((?:Proxy-)?Authorization\s*[:=]\s*)[^\r\n]+", "$1***", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"((?:access[-_]?token|token|password|passwd|secret)\s*[=:]\s*)[^\s&]+", "$1***", RegexOptions.IgnoreCase);
        return Regex.Replace(text, "((?:--)(?:access[-_]?token|token|password|passwd|secret)\\s+)(?:\"[^\"]*\"|'[^']*'|[^\\s&;]+)", "$1***", RegexOptions.IgnoreCase);
    }

    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> arguments)
    {
        var result = new List<string>(); var hideNext = false; var config = false; var configKey = false;
        var readConfig = arguments.Any(a => a.StartsWith("--get", StringComparison.Ordinal) || a is "--list" or "-l");
        for (var i = 0; i < Math.Min(arguments.Count, 64); i++)
        {
            var value = arguments[i];
            if (hideNext) { result.Add("***"); hideNext = false; continue; }
            if (value.StartsWith("--message=", StringComparison.OrdinalIgnoreCase)) { result.Add("--message=***"); continue; }
            if (value.StartsWith("-m", StringComparison.Ordinal) && value.Length > 2) { result.Add("-m***"); continue; }
            if (value == "-c" && i + 1 < arguments.Count)
            {
                result.Add(value); var setting = Text(arguments[++i]); var equals = setting.IndexOf('=');
                result.Add(equals < 0 ? Text(setting) : Text(setting[..(equals + 1)]) + "***"); continue;
            }
            if (config && !readConfig && configKey) { result.Add("***"); continue; }
            if (config && !value.StartsWith('-')) configKey = true;
            if (value == "config") config = true;
            result.Add(Text(value).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal));
            var option = value.TrimStart('-').Replace('_', '-').ToLowerInvariant();
            hideNext = value.StartsWith('-') && (option is "m" or "message" or "password" or "passwd" or "token" or "access-token" or "accesstoken" or "secret" or "header" or "h" or "authorization" or "proxy-authorization");
        }
        if (arguments.Count > 64) result.Add($"[{arguments.Count - 64} more arguments omitted]");
        return result.Select(value => value.Length > 2048 ? value[..2048] + "[truncated]" : value).ToArray();
    }
}
