using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Orchestrator;

public static class CodespaceManager
{
    // Kita hanya mengizinkan 1 codespace runner per repo
    private const string CODESPACE_DISPLAY_NAME = "automation-hub-runner";
    private const string MACHINE_TYPE = "standardLinux16gb"; // 4-core, 16GB RAM
    private const int SSH_TIMEOUT_MS = 30000;
    private const int CREATE_TIMEOUT_MS = 600000; // 10 menit

    // Path di root project
    private static readonly string ConfigRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config"));
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string MasterProxyFile = Path.Combine(ProjectRoot, "proxysync", "proxy.txt");

    /// <summary>
    /// Inti: Memastikan 1 codespace sehat berjalan menggunakan token saat ini.
    /// Akan reuse, delete+recreate, atau create baru.
    /// </summary>
    public static async Task<string> EnsureHealthyCodespace(TokenEntry token)
    {
        AnsiConsole.MarkupLine("\n[cyan]Inspecting existing codespaces...[/]");
        string repoFullName = $"{token.Owner}/{token.Repo}";
        
        try
        {
            var (existing, all) = await FindExistingCodespace(token);

            if (existing != null)
            {
                AnsiConsole.MarkupLine($"[green]✓ Found existing runner:[/][dim] {existing.Name} (State: {existing.State})[/]");

                // 1. Jika state "Available", cek SSH
                if (existing.State == "Available")
                {
                    if (await CheckSshHealth(token, existing.Name))
                    {
                        AnsiConsole.MarkupLine("[green]  ✓ Health check PASSED. Reusing this codespace.[/]");
                        return existing.Name;
                    }
                    
                    AnsiConsole.MarkupLine("[red]  ✗ Health check FAILED. Deleting unhealthy codespace...[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]  Codespace state is '{existing.State}', not 'Available'. Deleting...[/]");
                }

                // 2. Jika tidak sehat atau state jelek, hapus
                await DeleteCodespace(token, existing.Name);
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No existing runner found.[/]");
            }
            
            // 3. Hapus codespace lain (jika ada) yang mungkin stuck
            foreach (var cs in all.Where(cs => cs.Name != existing?.Name && cs.DisplayName == CODESPACE_DISPLAY_NAME))
            {
                AnsiConsole.MarkupLine($"[yellow]Deleting STUCK codespace: {cs.Name} (State: {cs.State})[/]");
                await DeleteCodespace(token, cs.Name);
            }

            // 4. Buat baru
            AnsiConsole.MarkupLine($"[cyan]Creating new '{CODESPACE_DISPLAY_NAME}' ({MACHINE_TYPE})...[/]");
            AnsiConsole.MarkupLine("[dim]This may take several minutes...[/]");
            
            string args = $"codespace create -r {repoFullName} -m {MACHINE_TYPE} --display-name {CODESPACE_DISPLAY_NAME} --idle-timeout 240m";
            var newName = await ShellHelper.RunGhCommand(token, args, CREATE_TIMEOUT_MS);
            
            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new Exception("Failed to create codespace (no name returned)");
            }
            
            AnsiConsole.MarkupLine($"[green]✓ Created: {newName}[/]");
            return newName;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]FATAL: Gagal memastikan codespace: {ex.Message}[/]");
            if (ex.Message.Contains("403") || ex.Message.Contains("quota"))
            {
                 AnsiConsole.MarkupLine("[red]Ini kemungkinan besar masalah kuota. Coba rotasi token.[/]");
            }
            throw; // Lemparkan ke loop utama untuk rotasi token
        }
    }

    /// <summary>
    /// Upload semua file konfigurasi yang dibutuhkan oleh deploy_bots.py
    /// </summary>
    public static async Task UploadConfigs(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("\n[cyan]Uploading configs to codespace...[/]");
        string remoteDir = $"/workspaces/{token.Repo}/config";

        // 1. Upload bots_config.json
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "bots_config.json"), $"{remoteDir}/bots_config.json");
        // 2. Upload apilist.txt
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "apilist.txt"), $"{remoteDir}/apilist.txt");
        // 3. Upload paths.txt
        await UploadFile(token, codespaceName, Path.Combine(ConfigRoot, "paths.txt"), $"{remoteDir}/paths.txt");
        // 4. Upload proxy.txt (yang sudah di-generate lokal)
        await UploadFile(token, codespaceName, MasterProxyFile, $"{remoteDir}/proxy.txt");
    }

    private static async Task UploadFile(TokenEntry token, string csName, string localPath, string remotePath)
    {
        if (!File.Exists(localPath))
        {
            AnsiConsole.MarkupLine($"[yellow]  SKIP: {Path.GetFileName(localPath)} not found locally.[/]");
            return;
        }
        AnsiConsole.Markup($"[dim]  Uploading {Path.GetFileName(localPath)}... [/]");
        string args = $"codespace cp \"{localPath}\" \"{csName}:{remotePath}\"";
        await ShellHelper.RunGhCommand(token, args);
        AnsiConsole.MarkupLine("[green]Done[/]");
    }

    /// <summary>
    /// Menjalankan auto-start.sh di remote secara non-blocking (nohup)
    /// </summary>
    public static async Task TriggerStartupScript(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine("\n[cyan]Triggering remote startup script (auto-start.sh)...[/]");
        string remoteScript = $"/workspaces/{token.Repo}/auto-start.sh";
        
        // Gunakan 'nohup ... &' untuk detachment
        string cmd = $"\"nohup bash {remoteScript} > /tmp/startup.log 2>&1 &\"";
        string args = $"codespace ssh -c {codespaceName} -- {cmd}";

        await ShellHelper.RunGhCommand(token, args);
        AnsiConsole.MarkupLine("[green]✓ Startup script triggered (detached).[/]");
        AnsiConsole.MarkupLine("[dim]   Gunakan 'gh codespace ssh' untuk memantau 'tmux ls' atau 'tail -f /tmp/startup.log'[/]");
    }

    public static async Task DeleteCodespace(TokenEntry token, string codespaceName)
    {
        AnsiConsole.MarkupLine($"[yellow]Deleting codespace: {codespaceName}...[/]");
        try
        {
            string args = $"codespace delete -c {codespaceName} --force";
            await ShellHelper.RunGhCommand(token, args);
            AnsiConsole.MarkupLine("[green]✓ Deleted.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to delete {codespaceName}: {ex.Message}[/]");
        }
    }

    private static async Task<bool> CheckSshHealth(TokenEntry token, string codespaceName)
    {
        try
        {
            string args = $"codespace ssh -c {codespaceName} -- echo 'alive'";
            string result = await ShellHelper.RunGhCommand(token, args, SSH_TIMEOUT_MS);
            return result.Contains("alive");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]  SSH check failed: {ex.Message}[/]");
            return false;
        }
    }

    private static async Task<(CodespaceInfo? existing, List<CodespaceInfo> all)> FindExistingCodespace(TokenEntry token)
    {
        string args = "codespace list --json name,displayName,state,machineName";
        string jsonResult = await ShellHelper.RunGhCommand(token, args);

        var allCodespaces = JsonSerializer.Deserialize<List<CodespaceInfo>>(jsonResult) ?? new List<CodespaceInfo>();

        var existing = allCodespaces.FirstOrDefault(cs => cs.DisplayName == CODESPACE_DISPLAY_NAME);
        
        return (existing, allCodespaces);
    }
    
    // Helper class untuk parsing JSON
    private class CodespaceInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";
        [JsonPropertyName("state")]
        public string State { get; set; } = "";
        [JsonPropertyName("machineName")]
        public string MachineName { get; set; } = "";
    }
}
