using System.Text;
using System.Text.Json;

namespace Sonde.Cli;

/// <summary>Renders a launch environment for a shell, PowerShell, a <c>.env</c> file or JSON.</summary>
internal static class EnvFormat
{
    public static readonly string[] Names = ["shell", "powershell", "dotenv", "json"];

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static string Render(string format, IReadOnlyDictionary<string, string?> environment)
    {
        var builder = new StringBuilder();
        switch (format)
        {
            case "shell":
                foreach (var (key, value) in environment)
                    builder.Append("export ").Append(key).Append('=').Append(ShellQuote(value ?? "")).Append('\n');
                break;
            case "powershell":
                foreach (var (key, value) in environment)
                    builder.Append("$env:").Append(key).Append(" = '").Append((value ?? "").Replace("'", "''", StringComparison.Ordinal)).Append("'\n");
                break;
            case "dotenv":
                foreach (var (key, value) in environment)
                    builder.Append(key).Append('=').Append(value).Append('\n');
                break;
            case "json":
                builder.Append(JsonSerializer.Serialize(environment, Pretty)).Append('\n');
                break;
            default:
                throw new ArgumentException($"Unknown format '{format}'. One of: {string.Join(", ", Names)}.", nameof(format));
        }
        return builder.ToString();
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
