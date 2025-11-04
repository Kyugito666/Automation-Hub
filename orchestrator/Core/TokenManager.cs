using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator.Core
{
    public class TokenEntry
    {
        public string Username { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string Proxy { get; set; } = string.Empty;
        public bool IsPrimary { get; set; } = false;
        public bool IsCollaborator { get; set; } = false;
    }
    
    public class TokenState
    {
        public string ActiveCodespaceName { get; set; } = string.Empty;
        
        [JsonIgnore]
        public bool IsCodespaceActive => !string.IsNullOrEmpty(ActiveCodespaceName);
    }

    public static class TokenManager
    {
        private static readonly string _configDir = Path.Combine(AppContext.BaseDirectory, "config");
        private static readonly string _tokenFile = Path.Combine(_configDir, "github_tokens.txt");
        private static readonly string _stateFile = Path.Combine(_configDir, "state.json");
        
        private static List<TokenEntry>? _tokens;
        private static TokenState _currentState = new TokenState();
        private static bool _useProxyGlobally = false;

        private static void LoadTokens()
        {
            if (_tokens != null) return;
            
            _tokens = new List<TokenEntry>();
            if (!File.Exists(_tokenFile))
            {
                AnsiConsole.MarkupLine($"[red]Token file not found: {_tokenFile}[/]");
                return;
            }

            var lines = File.ReadAllLines(_tokenFile);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                var parts = line.Split(';');
                if (parts.Length < 2) continue;

                var entry = new TokenEntry
                {
                    Username = parts[0].Trim(),
                    Token = parts[1].Trim(),
                    Proxy = parts.Length > 2 ? parts[2].Trim() : string.Empty,
                    IsPrimary = parts.Length > 3 && (parts[3].Trim().Equals("primary", StringComparison.OrdinalIgnoreCase)),
                    IsCollaborator = parts.Length > 3 && (parts[3].Trim().Equals("collab", StringComparison.OrdinalIgnoreCase))
                };
                _tokens.Add(entry);
            }
        }
        
        public static void LoadState()
        {
            try
            {
                if (File.Exists(_stateFile))
                {
                    var json = File.ReadAllText(_stateFile);
                    _currentState = JsonSerializer.Deserialize<TokenState>(json) ?? new TokenState();
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Could not load state file: {ex.Message.EscapeMarkup()}[/]");
                _currentState = new TokenState();
            }
        }
        
        public static void SaveState()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_currentState, options);
                File.WriteAllText(_stateFile, json);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to save state file: {ex.Message.EscapeMarkup()}[/]");
            }
        }
        
        public static TokenState GetState() => _currentState;
        
        public static void SetState(string activeCodespace)
        {
            _currentState.ActiveCodespaceName = activeCodespace;
            SaveState();
        }

        public static List<TokenEntry> GetAllTokens()
        {
            LoadTokens();
            return _tokens ?? new List<TokenEntry>();
        }

        public static TokenEntry? GetCurrentToken()
        {
            return GetAllTokens().FirstOrDefault(t => t.IsPrimary);
        }

        public static List<TokenEntry> GetCollaboratorTokens()
        {
            return GetAllTokens().Where(t => t.IsCollaborator).ToList();
        }
        
        public static void SetProxyUsage(bool useProxy)
        {
            _useProxyGlobally = useProxy;
        }

        public static bool IsProxyGloballyEnabled() => _useProxyGlobally;

        public static void ShowStatus()
        {
            var token = GetCurrentToken();
            if (token == null)
            {
                AnsiConsole.MarkupLine("[red]Primary token not set.[/]");
                return;
            }

            var table = new Table().Title("Current Status");
            table.AddColumn("Item");
            table.AddColumn("Value");
            
            table.AddRow("Primary User", token.Username.EscapeMarkup());
            table.AddRow("Proxy Enabled", IsProxyGloballyEnabled() ? "[green]Yes[/]" : "[red]No[/]");
            table.AddRow("Active Codespace", string.IsNullOrEmpty(_currentState.ActiveCodespaceName) 
                ? "[grey]None[/]" 
                : $"[blue]{_currentState.ActiveCodespaceName.EscapeMarkup()}[/]");
            
            AnsiConsole.Write(table);
        }
    }
}
