using Spectre.Console;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core;
using Orchestrator.Services;

namespace Orchestrator.Codespace
{
    internal static class CodeActions
    {
        internal static async Task<bool> RunStartupScriptAndStreamLogs(TokenEntry token, string codespaceName, bool isNewCodespace, CancellationToken cancellationToken)
        {
            string scriptPath = $"/workspaces/{token.Repo}/auto-start.sh";
            string startupArg = isNewCodespace ? "--new-install" : "--existing-install";
            string command = $"set -o pipefail; bash \"{scriptPath.Replace("\"", "\\\"")}\" {startupArg} | tee /tmp/startup.log";
            string args = $"codespace ssh -c \"{codespaceName}\" -- \"{command.Replace("\"", "\\\"")}\"";

            AnsiConsole.MarkupLine($"[cyan]Streaming logs from startup script ({startupArg})...[/]");
            AnsiConsole.MarkupLine("[dim](Press Ctrl+C to cancel)[/]");

            bool hasError = false;
            try
            {
                await GhService.RunGhCommandAndStreamOutputAsync(token, args, cancellationToken, (line) =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return true;
                    
                    string lowerLine = line.ToLowerInvariant();
                    if (lowerLine.Contains("error") || lowerLine.Contains("fatal") || lowerLine.Contains("failed"))
                    {
                        AnsiConsole.MarkupLine($"[red][REMOTE][/] {line.EscapeMarkup()}");
                        hasError = true;
                    }
                    else if (lowerLine.Contains("warning") || lowerLine.Contains("⚠"))
                    {
                        AnsiConsole.MarkupLine($"[yellow][REMOTE][/] {line.EscapeMarkup()}");
                    }
                    else if (lowerLine.Contains("success") || lowerLine.Contains("✓") || lowerLine.Contains("completed"))
                    {
                        AnsiConsole.MarkupLine($"[green][REMOTE][/] {line.EscapeMarkup()}");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[dim][REMOTE][/] {line.EscapeMarkup()}");
                    }
                    return true;
                });
                
                AnsiConsole.MarkupLine("[green]✓ Streaming finished.[/]");
                return !hasError;
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[yellow]Startup script streaming cancelled.[/]");
                throw;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error streaming startup script: {ex.Message.Split('\n').FirstOrDefault()?.EscapeMarkup()}[/]");
                return false;
            }
        }

        internal static async Task<string?> GetCodespaceState(TokenEntry token, string codespaceName)
        {
            try
            {
                string args = $"codespace view -c \"{codespaceName}\" --json";
                string output = await GhService.RunGhCommand(token, args, timeoutMilliseconds: 15000, useProxy: true);
                
                if (string.IsNullOrWhiteSpace(output)) return null;
                
                var jsonDoc = System.Text.Json.JsonDocument.Parse(output);
                if (jsonDoc.RootElement.TryGetProperty("state", out var stateElement))
                {
                    return stateElement.GetString();
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
