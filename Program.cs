// See https://aka.ms/new-console-template for more information

using System.Diagnostics.Metrics;
using DiscordBoostRoleBot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remora.Commands.Extensions;
using Remora.Discord.Commands.Extensions;
using Remora.Discord.Commands.Services;
using Remora.Discord.Gateway;
using Remora.Discord.Gateway.Extensions;
using Remora.Discord.Gateway.Results;
using Remora.Discord.Hosting.Extensions;
using Remora.Results;
using Remora.Discord.API;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.Rest.API;
using Remora.Discord.Rest.Extensions;
using Remora.Rest.Core;

namespace DiscordBoostRoleBot
{
    public class Program
    {
        // Get Token from Configuration file
        internal static readonly Configuration Config = Configuration.ReadConfig();

        // private static readonly IDiscordRestGuildAPI _restGuildAPI;
        public static ILogger<Program> log;
        public static HttpClient httpClient = new();

        public static async Task Main(string[] args)
        {
            //Build the service
            IHost? host = Host.CreateDefaultBuilder()
                .AddDiscordService(_ => Config.Token)
                .ConfigureServices(
                    (_, services) =>
                    {
                        services
                            .AddDbContext<Database.RoleDataDbContext>()
                            .AddDiscordRest(_ => Config.Token)
                            .AddDiscordCommands(true)
                            .AddCommandTree()
                                .WithCommandGroup<RoleCommands>();
                        // .Finish()
                        // .AddCommandTree(nameof(EmptyCommands))
                        //     .WithCommandGroup<EmptyCommands>();
                    })
                .ConfigureLogging(
                    c => c
                        .AddConsole()
                        .AddFilter("System.Net.Http.HttpClient.*.LogicalHandler", level: LogLevel.Warning)
                        .AddFilter("System.Net.Http.HttpClient.*.ClientHandler", level: LogLevel.Warning)
#if DEBUG
                        .AddDebug()
#endif
                )
                .UseConsoleLifetime()
                .Build();
            IServiceProvider? services = host.Services;
            log = services.GetRequiredService<ILogger<Program>>();
            Snowflake? debugServer = null;
            ulong? debugServerId = Config.TestServerId;
            if (debugServerId is not null)
            {
                if ((debugServer = new Snowflake(debugServerId.Value)) is null)
                {
                    log.LogWarning("Failed to parse debug server from environment");
                }
            }
            else
            {
                log.LogWarning("No debug server specified");
            }

            SlashService slashService = services.GetRequiredService<SlashService>();
            Result checkSlashSupport = slashService.SupportsSlashCommands();
            if(!checkSlashSupport.IsSuccess)
            {
                log.LogWarning
                (
                    "The registered commands of the bot don't support slash commands: {Reason}",
                    checkSlashSupport.Error?.Message
                );
            }
            else
            {
                if (args.Contains("--purge") && debugServer.HasValue){
                    Result purgeSlashCommands = await slashService
                        .UpdateSlashCommandsAsync(guildID: debugServer, treeName: nameof(EmptyCommands))
                        .ConfigureAwait(false);
                    if (!purgeSlashCommands.IsSuccess)
                    {
                        log.LogWarning("Failed to purge guild slash commands: {Reason}", purgeSlashCommands.Error?.Message);
                    }
                    else
                    {
                        log.LogInformation("Purge slash commands from {Reason}", debugServer.Value);
                    }
                }
#if DEBUG
                Result updateSlash = await slashService.UpdateSlashCommandsAsync(guildID: debugServer).ConfigureAwait(false);
#else
                Result updateSlash = await slashService.UpdateSlashCommandsAsync().ConfigureAwait(false);
#endif
                if (!updateSlash.IsSuccess)
                {
                    log.LogWarning("Failed to update slash commands: {Reason}", updateSlash.Error?.Message);
                }
                else
                {
                    log.LogInformation($"Successfully created commands {(debugServer is not null ? "on " + debugServer.Value : "global")}");
                }
            }
            await host.RunAsync().ConfigureAwait(false);

            Console.WriteLine("Bye bye");
        }

        internal static async Task<string> CheckBoosting(Snowflake server, IDiscordRestGuildAPI _restGuildAPI)
        {
            Result<IReadOnlyList<IGuildMember>> membersResult = await _restGuildAPI.ListGuildMembersAsync(guildID: server).ConfigureAwait(false);
            string messageString = string.Empty;
            if (!membersResult.IsSuccess)
            {
                log.LogError($"ListGuildMembers failed with code {membersResult.Error}");
                messageString += $"ListGuildMembers failed with code {membersResult.Error}";
                IResult? err = membersResult.Inner;
                while (err != null)
                {
                    log.LogError($"ListGuildMembers inner failed with code {membersResult.Error}");
                    messageString += $"ListGuildMembers inner failed with code {membersResult.Error}";
                    err = err.Inner;
                }

                return messageString;
            }

            IReadOnlyList<IGuildMember> members = membersResult.Entity;
            messageString += "Boost Report:";
            foreach (IGuildMember member in members)
            {
                messageString += "\n";
                if (member.PremiumSince.HasValue && member.PremiumSince.Value.HasValue)
                {
                    string? message = $"{member.User.Value.ID.User()} is boosting";
                    log.LogInformation(message: message);
                    messageString += message;
                }
                else
                {
                    string? message = $"{member.User.Value.ID.User()} is not boosting";
                    log.LogInformation(message: message);
                    messageString += message;
                }

            }
            return messageString;
        }
        /// <summary>
        /// Creates a generic application host builder.
        /// </summary>
        /// <param name="args">The arguments passed to the application.</param>
        /// <returns>The host builder.</returns>
        [Obsolete("don't use this until you fix it", true)]
        private static IHostBuilder CreateHostBuilder(string[] args) => Host.CreateDefaultBuilder(args: args)
            .AddDiscordService
            (
                services =>
                {
                    IConfiguration configuration = services.GetRequiredService<IConfiguration>();

                    return configuration.GetValue<string?>("REMORA_BOT_TOKEN") ??
                           throw new InvalidOperationException
                           (
                               "No bot token has been provided. Set the REMORA_BOT_TOKEN environment variable to a " +
                               "valid token."
                           );
                }
            )
            .ConfigureServices
            (
                (_, services) =>
                {
                    services
                        .AddDiscordCommands(true)
                        .AddCommandTree()
                        .WithCommandGroup<RoleCommands>();
                }
            )
            .ConfigureLogging
            (
                c => c
                    .AddConsole()
                    .AddFilter("System.Net.Http.HttpClient.*.LogicalHandler", level: LogLevel.Warning)
                    .AddFilter("System.Net.Http.HttpClient.*.ClientHandler", level: LogLevel.Warning)
            );
    }
}