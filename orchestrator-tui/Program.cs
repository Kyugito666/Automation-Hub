// BAGIAN DARI Program.cs - Replace fungsi RunOrchestratorLoopAsync dengan ini

private static async Task RunOrchestratorLoopAsync(CancellationToken cancellationToken) 
{
    Console.WriteLine("Starting Orchestrator Loop...");
    
    const int MAX_CONSECUTIVE_ERRORS = 3;
    int consecutiveErrors = 0;
    
    while (!cancellationToken.IsCancellationRequested) 
    {
        TokenEntry currentToken = TokenManager.GetCurrentToken(); 
        TokenState currentState = TokenManager.GetState(); 
        string? activeCodespace = currentState.ActiveCodespaceName;
        
        var username = currentToken.Username ?? "unknown";
        Console.WriteLine($"\n========== Token #{currentState.CurrentIndex + 1} (@{username}) ==========");
        
        try 
        { 
            // Step 1: Check billing
            Console.WriteLine("Checking billing..."); 
            var billingInfo = await BillingManager.GetBillingInfo(currentToken); 
            BillingManager.DisplayBilling(billingInfo, currentToken.Username ?? "unknown");
            
            if (!billingInfo.IsQuotaOk) 
            { 
                Console.WriteLine("Quota insufficient. Rotating..."); 
                
                if (!string.IsNullOrEmpty(activeCodespace)) 
                { 
                    await CodespaceManager.DeleteCodespace(currentToken, activeCodespace); 
                    currentState.ActiveCodespaceName = null; 
                    TokenManager.SaveState(currentState); 
                }
                
                currentToken = TokenManager.SwitchToNextToken(); 
                await Task.Delay(5000, cancellationToken); 
                consecutiveErrors = 0; // Reset error counter on rotation
                continue; 
            }
            
            // Step 2: Ensure codespace exists and is healthy
            Console.WriteLine("Ensuring codespace..."); 
            activeCodespace = await CodespaceManager.EnsureHealthyCodespace(currentToken);
            
            bool isNewCodespace = currentState.ActiveCodespaceName != activeCodespace;
            
            if (isNewCodespace) 
            { 
                currentState.ActiveCodespaceName = activeCodespace; 
                TokenManager.SaveState(currentState); 
                
                Console.WriteLine($"Active CS: {activeCodespace}");
                Console.WriteLine("New/Recreated CS detected..."); 
                
                // Step 3: Upload configs (dengan retry)
                bool uploadSuccess = false;
                for (int uploadAttempt = 1; uploadAttempt <= 3; uploadAttempt++)
                {
                    try
                    {
                        await CodespaceManager.UploadConfigs(currentToken, activeCodespace);
                        uploadSuccess = true;
                        break;
                    }
                    catch (Exception uploadEx)
                    {
                        AnsiConsole.MarkupLine($"[red]Upload attempt {uploadAttempt}/3 failed: {uploadEx.Message}[/]");
                        if (uploadAttempt < 3)
                        {
                            AnsiConsole.MarkupLine("[yellow]Retrying in 10 seconds...[/]");
                            await Task.Delay(10000, cancellationToken);
                        }
                    }
                }
                
                if (!uploadSuccess)
                {
                    throw new Exception("Failed to upload configs after 3 attempts");
                }
                
                // Step 4: Trigger startup script (dengan retry)
                bool startupSuccess = false;
                for (int startupAttempt = 1; startupAttempt <= 3; startupAttempt++)
                {
                    try
                    {
                        await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                        startupSuccess = true;
                        break;
                    }
                    catch (Exception startupEx)
                    {
                        AnsiConsole.MarkupLine($"[red]Startup attempt {startupAttempt}/3 failed: {startupEx.Message}[/]");
                        if (startupAttempt < 3)
                        {
                            AnsiConsole.MarkupLine("[yellow]Retrying in 15 seconds...[/]");
                            await Task.Delay(15000, cancellationToken);
                        }
                    }
                }
                
                if (!startupSuccess)
                {
                    throw new Exception("Failed to trigger startup script after 3 attempts");
                }
                
                Console.WriteLine("Initial startup complete."); 
            } 
            else 
            { 
                Console.WriteLine("Codespace healthy (reusing existing)."); 
            }
            
            // Reset consecutive error counter pada sukses
            consecutiveErrors = 0;
            
            // Step 5: Keep-alive sleep
            Console.WriteLine($"Sleeping for Keep-Alive ({KeepAliveInterval.TotalMinutes} min)..."); 
            await Task.Delay(KeepAliveInterval, cancellationToken);
            
            // Step 6: Keep-alive health check
            currentState = TokenManager.GetState(); 
            activeCodespace = currentState.ActiveCodespaceName; 
            
            if (string.IsNullOrEmpty(activeCodespace)) 
            { 
                Console.WriteLine("No active codespace in state. Will recreate next cycle."); 
                continue; 
            }
            
            Console.WriteLine("Keep-Alive: Checking SSH..."); 
            
            if (!await CodespaceManager.CheckSshHealthWithRetry(currentToken, activeCodespace)) 
            { 
                Console.WriteLine("Keep-Alive: SSH check FAILED!"); 
                currentState.ActiveCodespaceName = null; 
                TokenManager.SaveState(currentState); 
                Console.WriteLine("Will recreate next cycle."); 
            } 
            else 
            { 
                Console.WriteLine("Keep-Alive: SSH check OK.");
                
                // Optional: Re-trigger startup script untuk keep-alive
                try
                {
                    Console.WriteLine("Keep-Alive: Re-triggering startup script...");
                    await CodespaceManager.TriggerStartupScript(currentToken, activeCodespace);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Keep-alive startup trigger failed: {ex.Message}");
                    // Non-fatal, continue loop
                }
            }
        } 
        catch (OperationCanceledException) 
        { 
            Console.WriteLine("Loop cancelled by user."); 
            break; 
        } 
        catch (Exception ex) 
        { 
            consecutiveErrors++;
            Console.WriteLine("ERROR loop:"); 
            Console.WriteLine(ex.ToString());
            
            // Handle consecutive errors
            if (consecutiveErrors >= MAX_CONSECUTIVE_ERRORS)
            {
                AnsiConsole.MarkupLine($"[red]CRITICAL: {MAX_CONSECUTIVE_ERRORS} consecutive errors detected![/]");
                AnsiConsole.MarkupLine("[yellow]Attempting token rotation and full reset...[/]");
                
                // Force cleanup
                if (!string.IsNullOrEmpty(currentState.ActiveCodespaceName))
                {
                    try
                    {
                        await CodespaceManager.DeleteCodespace(currentToken, currentState.ActiveCodespaceName);
                    }
                    catch { }
                }
                
                currentState.ActiveCodespaceName = null;
                TokenManager.SaveState(currentState);
                
                // Rotate token
                currentToken = TokenManager.SwitchToNextToken();
                consecutiveErrors = 0;
                
                AnsiConsole.MarkupLine("[cyan]Waiting 30 seconds before retry with new token...[/]");
                await Task.Delay(30000, cancellationToken);
            }
            else
            {
                Console.WriteLine($"Retrying in {ErrorRetryDelay.TotalMinutes} minutes... (Error {consecutiveErrors}/{MAX_CONSECUTIVE_ERRORS})");
                await Task.Delay(ErrorRetryDelay, cancellationToken);
            }
        }
    }
}
