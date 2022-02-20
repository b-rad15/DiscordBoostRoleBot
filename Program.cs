﻿// See https://aka.ms/new-console-template for more information

using System.Drawing;
using System.Linq;
using System.Text.Encodings.Web;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remora.Commands.Extensions;
using Remora.Discord.Commands.Extensions;
using Remora.Discord.Commands.Services;
using Remora.Discord.Hosting.Extensions;
using Remora.Results;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Abstractions.Results;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Feedback.Messages;
using Remora.Discord.Commands.Responders;
using Remora.Discord.Gateway.Extensions;
using Remora.Discord.Rest.Extensions;
using Remora.Extensions.Options.Immutable;
using Remora.Rest.Core;
using Remora.Rest.Results;
using SixLabors.ImageSharp.Formats;
using SQLitePCL;
using Z.EntityFramework.Plus;

namespace DiscordBoostRoleBot
{
    public class Program
    {
        // Get Token from Configuration file
        internal static readonly Configuration Config = Configuration.ReadConfig();

        private static IDiscordRestGuildAPI? _restGuildApi;
        private static IDiscordRestUserAPI? _restUserApi;
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
                            .AddDiscordRest(_ => Config.Token)
                            .AddDiscordCommands(true)
                            .AddTransient<ICommandPrefixMatcher, PrefixSetter>()
                            .AddCommandTree()
                                .WithCommandGroup<RoleCommands>()
                                .Finish()
                            .AddResponder<AddReactionsToMediaArchiveMessageResponder>()
                            .AddCommandTree()
                                .WithCommandGroup<AddReactionsToMediaArchiveCommands>()
                                .Finish()
                            .AddCommandTree()
                                .WithCommandGroup<CommandResponderConfigCommands>()
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
            LogFactory = services.GetRequiredService<ILoggerFactory>();
            _restGuildApi = services.GetRequiredService<IDiscordRestGuildAPI>();
            _restUserApi = services.GetRequiredService<IDiscordRestUserAPI>();
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
                log.LogCritical("The registered commands of the bot don't support slash commands: {Reason}",
                    checkSlashSupport.Error?.Message);
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
                    if(updateSlash.Error is RestResultError<RestError> restError)
                        log.LogCritical("Failed to update slash commands: {code} {reason}", restError.Error.Code.Humanize(LetterCasing.Title), restError.Error.Message);
                    else
                        log.LogCritical("Failed to update slash commands: {Reason}", updateSlash.Error?.Message);
                }
                else
                {
                    log.LogInformation($"Successfully created commands {(debugServer is not null ? "on " + debugServer.Value : "global")}");
                }
            }
            await host.RunAsync().ConfigureAwait(false);

            Console.WriteLine("Bye bye");
        }

        public static ILoggerFactory LogFactory { get; set; }

        internal static async Task<Result<IEnumerable<IGuildMember>>> GetGuildMembers(Snowflake guildId, IRole? role = null, bool checkIsBoosting = false, bool canBeMod = true, bool isBotOwner = false)
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
                        membersToAdd = membersToAdd.Where(gm => (gm.IsBoosting() || (canBeMod && gm.IsRoleModAdminOrOwner())) && gm.Roles.Contains(role.ID));
                    }
                    else if(role is not null)
                    {
                        membersToAdd = membersToAdd.Where(gm => gm.Roles.Contains(role.ID));
                    }
                    else if(checkIsBoosting)
                    {
                        membersToAdd = membersToAdd.Where(gm=> (gm.IsBoosting() || (canBeMod && gm.IsRoleModAdminOrOwner())));
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

        internal static async Task<Result<List<Snowflake>>> RemoveNonBoosterRoles(Snowflake serverId, CancellationToken ct = new())
        {
            List<Snowflake> peopleRemoved = new();
            Result<IEnumerable<IGuildMember>> guildBoostersResult = await GetGuildMembers(serverId, checkIsBoosting: true).ConfigureAwait(false);
            if (!guildBoostersResult.IsSuccess)
            {
                log.LogError("Could not get guild members for guild {guild} because {reason}", serverId, guildBoostersResult.Error.Message);
                return Result<List<Snowflake>>.FromError(guildBoostersResult.Error);
            }

            IEnumerable<IGuildMember> guildBoosters = guildBoostersResult.Entity;
            await using Database.DiscordDbContext database = new();
            List<Database.RoleData> rolesCreatedForGuild = await database.RolesCreated.Where(rc => rc.ServerId == serverId.Value).ToListAsync(cancellationToken: ct).ConfigureAwait(false);
            foreach (Database.RoleData roleCreated in rolesCreatedForGuild.Where(roleCreated => guildBoosters.All(gb => roleCreated.RoleUserId != gb.User.Value.ID.Value)))
            {
                Result delRoleResult = await _restGuildApi.DeleteGuildRoleAsync(serverId, new Snowflake(roleCreated.RoleId)).ConfigureAwait(false);
                if (!delRoleResult.IsSuccess)
                {
                    if (delRoleResult.Error is RestResultError<RestError> restError)
                    {
                        if (restError.Error.Code == DiscordError.UnknownRole)
                        {
                            log.LogDebug("Role {role} was deleted without using the database, tell {server} not to do that pls", roleCreated.RoleId, serverId);
                        }
                        else
                        {
                            log.LogError("Rest error deleting role {role} because reason {reason}", roleCreated.RoleId, restError.Error.Code.Humanize(LetterCasing.Title));
                        }
                    }
                    else
                    {
                        log.LogError("Failed to delete role {role} because {error}", roleCreated.RoleId, delRoleResult.Error.Message);
                    }
                }
                database.Remove(roleCreated);
                peopleRemoved.Add(new Snowflake(roleCreated.RoleUserId));
            }

            IGuildMember? botOwnerGm = guildBoosters.FirstOrDefault(gb => gb.IsOwner());
            if(botOwnerGm is not null)
            {
                Database.RoleData? ownerRole =
                    rolesCreatedForGuild.FirstOrDefault(rc => rc.RoleUserId.IsOwner());
                if (ownerRole is not null)
                {
                    await CheckBotOwnerRole(serverId, botOwnerGm, ownerRole, ct);
                }
            }
            int numRows = await database.SaveChangesAsync(cancellationToken: ct).ConfigureAwait(false);
            if (numRows != peopleRemoved.Count)
            {
                log.LogWarning("Removed {numRows} from db but removed {numRoles} roles", numRows, peopleRemoved.Count);
            }
            return Result<List<Snowflake>>.FromSuccess(peopleRemoved);
        }

        internal static async Task<Result<bool>> CheckBotOwnerRole(Snowflake serverId, IGuildMember botOwnerGm, Database.RoleData roleData, CancellationToken ct = new())
        {
            Result<IReadOnlyList<IRole>> rolesResponse = await _restGuildApi!.GetGuildRolesAsync(serverId, ct);
            if (!rolesResponse.IsSuccess)
            {
                log.LogCritical("could not get roles for server {server}", serverId);
                return false;
            }

            IReadOnlyList<IRole> roles = rolesResponse.Entity;
            IRole? ownerRole = roles.FirstOrDefault(role => role.ID.Value == roleData.RoleId);
            if (ownerRole is null)
            {
                //Prepare Image
                MemoryStream? iconStream = null;
                IImageFormat? iconFormat = null;
                if (string.IsNullOrWhiteSpace(roleData.ImageUrl))
                {
                    IResult? makeNewRole;
                    Result<(MemoryStream? iconStream, IImageFormat? imageFormat)> imageToStreamResult = await RoleCommands.ImageUrlToBase64(imageUrl: roleData.ImageUrl).ConfigureAwait(false);
                    if (!imageToStreamResult.IsSuccess)
                    {
                        log.LogCritical(imageToStreamResult.Error.Message);
                        return false;
                    }

                    (iconStream, iconFormat) = imageToStreamResult.Entity;
                }
                Result<IRole> roleResult = await _restGuildApi.CreateGuildRoleAsync(guildID: serverId, name: roleData.Name, colour: ColorTranslator.FromHtml(roleData.Color), icon: iconStream ?? default(Optional<Stream>), isHoisted: false, isMentionable: true, ct: ct).ConfigureAwait(false);
                if (!roleResult.IsSuccess)
                {
                    log.LogError($"Could not create role for {botOwnerGm.User.Value.Mention()} because {roleResult.Error}");
                    return false;
                }
                IRole role = roleResult.Entity;
                roleData.RoleUserId = role.ID.Value;
                Result roleApplyResult = await _restGuildApi.AddGuildMemberRoleAsync(guildID: serverId,
                    userID: botOwnerGm.User.Value.ID, roleID: role.ID,
                    "User is boosting, role request via BoostRoleManager bot", ct: ct).ConfigureAwait(false);
                if (!roleApplyResult.IsSuccess)
                {
                    log.LogError($"Could not make role because {roleApplyResult.Error}");
                    return false;
                }

                string msg = "";
                Result<IReadOnlyList<IRole>> getRolesResult = await _restGuildApi.GetGuildRolesAsync(serverId, ct: ct).ConfigureAwait(false);
                if (getRolesResult.IsSuccess)
                {
                    IReadOnlyList<IRole> guildRoles = getRolesResult.Entity;
                    Result<IUser> currentBotUserResult =
                        await _restUserApi.GetCurrentUserAsync(ct: ct).ConfigureAwait(false);
                    if (currentBotUserResult.IsSuccess)
                    {
                        IUser currentBotUser = currentBotUserResult.Entity;
                        Result<IGuildMember> currentBotMemberResult =
                            await _restGuildApi.GetGuildMemberAsync(serverId, currentBotUser.ID,
                                ct).ConfigureAwait(false);
                        if (currentBotMemberResult.IsSuccess)
                        {
                            IGuildMember currentBotMember = currentBotMemberResult.Entity;
                            IEnumerable<IRole> botRoles = guildRoles.Where(gr => currentBotMember.Roles.Contains(gr.ID));
                            IRole? maxPosRole = botRoles.MaxBy(br => br.Position);
                            log.LogDebug("Bot's highest role is {role_name}: {roleId}", maxPosRole.Name, maxPosRole.ID);
                            int maxPos = maxPosRole.Position;
                            Result<IReadOnlyList<IRole>> roleMovePositionResult = await _restGuildApi
                                .ModifyGuildRolePositionsAsync(serverId,
                                    new (Snowflake RoleID, Optional<int?> Position)[] { (role.ID, maxPos) }, ct: ct).ConfigureAwait(false);
                            if (!roleMovePositionResult.IsSuccess)
                            {
                                log.LogWarning($"Could not move the role because {roleMovePositionResult.Error}");
                                return false;
                            }

                            log.LogDebug(roleMovePositionResult.ToString());
                        }
                        else
                        {
                            log.LogWarning($"Could not get bot member because {currentBotMemberResult.Error}");
                            msg += "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
                        }
                    }
                    else
                    {
                        log.LogWarning($"Could not get bot user because {currentBotUserResult.Error}");
                        msg += "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
                    }
                }
                else
                {
                    log.LogWarning($"Could not move the role because {getRolesResult.Error}");
                    msg += "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
                }

                log.LogInformation($"Made Role {role.Mention()} and assigned to {botOwnerGm.Mention()}");
                return true;
            }

            return true;

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