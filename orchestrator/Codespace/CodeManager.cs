using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Core;
using Orchestrator.Util;

namespace Orchestrator.Codespace
{
    public static class CodeManager
    {
        public static async Task<string> CreateCodespaceAsync(TokenEntry token, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("[cyan]Mencoba membuat codespace baru...[/]");
            var (stdout, stderr, exitCode) = await CodeActions.RunCommandAsync(token, 
                "kyugito666/automation-hub", 
                "gh codespace create -r kyugito666/automation-hub -b main --default-image-name 'mcr.microsoft.com/devcontainers/universal:2' -m 'standardLinux' --retention-period '1d' --display-name 'AutomationHubRunner'", 
                cancellationToken, 
                useProxy: false);

            if (exitCode != 0)
            {
                if (stderr.Contains("could not create codespace") && stderr.Contains("quota"))
                {
                    throw new Exception("Gagal membuat codespace: Kuota habis. Hapus codespace lama.");
                }
                throw new Exception($"Gagal membuat codespace (Exit Code: {exitCode}): {stderr}");
            }
            
            string? newName = null;
            if (stdout.Contains("Creating codespace"))
            {
                 AnsiConsole.MarkupLine("[yellow]Codespace dibuat, mengambil nama...[/]");
                 await Task.Delay(5000, cancellationToken); 
                 var foundName = await FindActiveCodespaceAsync(token, cancellationToken);
                 if(foundName == null)
                 {
                    throw new Exception("Gagal mengambil nama codespace setelah dibuat. Coba lagi.");
                 }
                 newName = foundName;
            } 
            else 
            {
                newName = stdout.Trim();
                if (string.IsNullOrEmpty(newName)) throw new Exception("Gagal parse nama codespace dari output gh.");
            }
            
            AnsiConsole.MarkupLine($"[green]✓ Codespace [blue]{newName.EscapeMarkup()}[/] berhasil dibuat.[/]");
            return newName;
        }

        public static async Task<string?> FindActiveCodespaceAsync(TokenEntry token, CancellationToken cancellationToken)
        {
            var (stdout, stderr, exitCode) = await CodeActions.RunCommandAsync(token, 
                "kyugito666/automation-hub", 
                "gh codespace list --json name,repository,state,displayName --jq '.[] | select(.repository.nameWithOwner == \"kyugito666/automation-hub\" and .state == \"Available\" and .displayName == \"AutomationHubRunner\") | .name'", 
                cancellationToken, 
                useProxy: false);

            if (exitCode != 0)
            {
                throw new Exception($"Gagal list codespace (Exit Code: {exitCode}): {stderr}");
            }

            var codespaceName = stdout.Trim().Split('\n').FirstOrDefault();
            return string.IsNullOrEmpty(codespaceName) ? null : codespaceName;
        }

        public static async Task StartTmuxSessionAsync(TokenEntry token, string codespaceName, string sessionName, string windowName, string command, CancellationToken cancellationToken)
        {
            string safeWindowName = Regex.Replace(windowName, @"[:\.]", "-");
            string safeCommand = command.Replace("\"", "\\\"");

            string checkSessionCmd = $"gh codespace ssh --codespace \"{codespaceName}\" -- tmux has-session -t {sessionName}";
            var (_, _, exitCode) = await CodeActions.RunCommandAsync(token, null, checkSessionCmd, cancellationToken, useProxy: false, timeoutMs: 10000);

            if (exitCode != 0)
            {
                string newSessionCmd = $"gh codespace ssh --codespace \"{codespaceName}\" -- tmux new-session -d -s {sessionName} -n \"{safeWindowName}\" \"{safeCommand}\"";
                await CodeActions.RunCommandAsync(token, null, newSessionCmd, cancellationToken, useProxy: false);
            }
            else
            {
                string newWindowCmd = $"gh codespace ssh --codespace \"{codespaceName}\" -- tmux new-window -t {sessionName} -n \"{safeWindowName}\" \"{safeCommand}\"";
                await CodeActions.RunCommandAsync(token, null, newWindowCmd, cancellationToken, useProxy: false);
            }
        }
        
        public static async Task<List<string>> GetTmuxSessions(TokenEntry token, string codespaceName)
        {
            var (stdout, stderr, exitCode) = await CodeActions.RunCommandAsync(token, codespaceName, "tmux list-windows -F \"#{window_name}\"", CancellationToken.None, useProxy: false);
            if (exitCode != 0)
            {
                if (stderr.Contains("no server running")) return new List<string>(); 
                throw new Exception($"Failed to list tmux sessions (Exit Code: {exitCode}): {stderr}");
            }
            return stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        public static void StopCodespace(CancellationToken cancellationToken)
        {
             var token = TokenManager.GetCurrentToken();
             var state = TokenManager.GetState();
             var activeCodespace = state.ActiveCodespaceName;
             
             if (token == null || string.IsNullOrEmpty(activeCodespace))
             {
                AnsiConsole.MarkupLine("[yellow]Tidak ada token atau codespace aktif untuk distop.[/]");
                return;
             }
             
             AnsiConsole.MarkupLine($"[yellow]Stopping codespace [blue]{activeCodespace.EscapeMarkup()}[/]...[/]");
             try
             {
                 CodeActions.RunCommandAsync(token, null, $"gh codespace stop -c \"{activeCodespace}\"", cancellationToken, useProxy: false)
                    .GetAwaiter().GetResult();
                 AnsiConsole.MarkupLine("[green]✓ Codespace stopped.[/]");
             }
             catch (Exception ex)
             {
                 AnsiConsole.MarkupLine($"[red]Gagal stop codespace: {ex.Message.EscapeMarkup()}[/]");
             }
        }
    }
}
