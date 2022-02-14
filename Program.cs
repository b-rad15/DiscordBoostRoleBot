// See https://aka.ms/new-console-template for more information

using System.Diagnostics.Metrics;
using System.Reflection.Metadata.Ecma335;
using DiscordBoostRoleBot;
using Microsoft.EntityFrameworkCore;
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

        private static IDiscordRestGuildAPI? _restGuildApi;
        public static bool IsInitialized() => _restGuildApi is not null;

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
                                .WithCommandGroup<RoleCommands>()
                                .Finish()
                            .AddHostedService<RolesRemoveService>();
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
            _restGuildApi = services.GetRequiredService<IDiscordRestGuildAPI>();
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

        internal static async Task<Result<IEnumerable<IGuildMember>>> GetGuildMembers(Snowflake guildId, IRole? role = null, bool checkIsBoosting = false, bool canBeMod = true)
        {

            //Check role meets criteria to be added
            List<IGuildMember> membersList = new();
            Optional<Snowflake> lastGuildMemberSnowflake = default;
            while (true)
            {
                Result<IReadOnlyList<IGuildMember>> getMembersResult =
                    await _restGuildApi!.ListGuildMembersAsync(guildId, limit: 1000, after: lastGuildMemberSnowflake).ConfigureAwait(false);
                if (!getMembersResult.IsSuccess)
                {
                    return Result<IEnumerable<IGuildMember>>.FromError(getMembersResult);
                }

                if (getMembersResult.Entity.Any())
                {
                    IEnumerable<IGuildMember> membersToAdd = getMembersResult.Entity;
                    if (role is not null && checkIsBoosting)
                    {
                        membersToAdd = membersToAdd.Where(gm => (gm.IsBoosting() || (canBeMod && gm.IsModAdminOrOwner())) && gm.Roles.Contains(role.ID));
                    }
                    else if(role is not null)
                    {
                        membersToAdd = membersToAdd.Where(gm => gm.Roles.Contains(role.ID));
                    }
                    else if(checkIsBoosting)
                    {
                        membersToAdd = membersToAdd.Where(gm=> (gm.IsBoosting() || (canBeMod && gm.IsModAdminOrOwner())));
                    }
                    membersList.AddRange(membersToAdd);
                    lastGuildMemberSnowflake = new Optional<Snowflake>(getMembersResult.Entity.Last().User.Value.ID);
                }
                else
                {
                    break;
                }
            }
            return Result<IEnumerable<IGuildMember>>.FromSuccess(membersList);
        }

        internal static async Task<Result<List<Snowflake>>> RemoveNonBoosterRoles(Snowflake guildId)
        {
            List<Snowflake> peopleRemoved = new();
            Result<IEnumerable<IGuildMember>> guildBoostersResult = await GetGuildMembers(guildId, checkIsBoosting: true).ConfigureAwait(false);
            if (!guildBoostersResult.IsSuccess)
            {
                log.LogError("Could not get guild members for guild {guild} because {reason}", guildId, guildBoostersResult.Error.Message);
                return Result<List<Snowflake>>.FromError(guildBoostersResult.Error);
            }

            IEnumerable<IGuildMember> guildBoosters = guildBoostersResult.Entity;
            await using Database.RoleDataDbContext database = new();
            List<Database.RoleData> rolesCreatedForGuild = await database.RolesCreated.Where(rc => rc.ServerId == guildId.Value).ToListAsync().ConfigureAwait(false);
            foreach (Database.RoleData roleCreated in rolesCreatedForGuild.Where(roleCreated => guildBoosters.All(gb => roleCreated.RoleUserId != gb.User.Value.ID.Value)))
            {
                Result delRoleResult = await _restGuildApi.DeleteGuildRoleAsync(guildId, new Snowflake(roleCreated.RoleId)).ConfigureAwait(false);
                if (!delRoleResult.IsSuccess)
                {
                    log.LogError("Failed to delete role {role} because {error}", roleCreated.RoleId, delRoleResult.Error.Message);
                }
                database.Remove(roleCreated);
                peopleRemoved.Add(new Snowflake(roleCreated.RoleUserId));
            }
            return Result<List<Snowflake>>.FromSuccess(peopleRemoved);
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