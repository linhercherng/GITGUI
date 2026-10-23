using System.Text;
using GitGui.Core;

namespace GitGui.Engine.Cli;

public sealed class TextConfigurationReader(GitProcessRunner runner)
{
    public async Task<RepositoryTextSettings> ReadAsync(string root, CancellationToken ct)
    {
        var result = await runner.ReadAsync(root, ["config", "--null", "--get-regexp", "^(core\\.(autocrlf|safecrlf|eol)|i18n\\.commitencoding)$"], ct);
        if (result.ExitCode is not (0 or 1)) throw new GitCommandException(result);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Encoding.UTF8.GetString(result.Output).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = field.IndexOf('\n');
            if (split >= 0) values[field[..split]] = field[(split + 1)..];
        }
        string Value(string key, string fallback = "unset") => values.GetValueOrDefault(key, fallback);
        return new(Value("core.autocrlf"), Value("core.safecrlf"), Value("core.eol"), Value("i18n.commitencoding", "UTF-8"));
    }
}
