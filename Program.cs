// See https://aka.ms/new-console-template for more information

using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Encodings.Web;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remora.Commands.Extensions;
using Remora.Discord.API.Abstractions.Gateway.Commands;
using Remora.Discord.Commands.Extensions;
using Remora.Discord.Commands.Services;
using Remora.Discord.Hosting.Extensions;
using Remora.Results;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Abstractions.Results;
using Remora.Discord.API.Gateway.Events;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Feedback.Messages;
using Remora.Discord.Commands.Responders;
using Remora.Discord.Gateway;
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
                            .AddHostedService<RolesRemoveService>()
                            .Configure<DiscordGatewayClientOptions>(g => g.Intents |= GatewayIntents.MessageContents);
                    })
                .ConfigureLogging(
                    c => c
                        .AddConsole()
                        .AddFilter("System.Net.Http.HttpClient.*.LogicalHandler", level: LogLevel.Warning)
                        .AddFilter("System.Net.Http.HttpClient.*.ClientHandler", level: LogLevel.Warning)
                        // .AddFilter("Microsoft.Extensions.Http.DefaultHttpClientFactory", level: LogLevel.Trace)
#if DEBUG
                        .AddDebug()
                        .SetMinimumLevel(LogLevel.Debug)
#else
                        .SetMinimumLevel(LogLevel.Information)
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
        internal static async Task<Result<IEnumerable<IGuildMember>>> GetSpecifiedGuildMembers(Snowflake guildId, IEnumerable<Snowflake> membersToGet)
        {
            //Check role meets criteria to be added
            List<IGuildMember> membersList = new();
            Optional<Snowflake> lastGuildMemberSnowflake = default;
            foreach (Snowflake memberIdSnowflake in membersToGet)
            {
                Result<IGuildMember> guildMemberResult = await _restGuildApi.GetGuildMemberAsync(guildID: guildId, userID: memberIdSnowflake).ConfigureAwait(false);
                if (!guildMemberResult.IsSuccess)
                {
                    if (guildMemberResult.Error is RestResultError<RestError> restError)
                    {
                        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                        switch (restError.Error.Code)
                        {
                            case DiscordError.InvalidGuild:
                                log.LogWarning("Guild {guild} is invalid", guildId);
                                break;
                            case DiscordError.UnknownGuild:
                                log.LogWarning("Guild {guild} is unknown", guildId);
                                break;
                            case DiscordError.UnknownMember:
                                //TODO: Remove these from database
                                log.LogWarning("Member {member} in guild {guild} is invalid", memberIdSnowflake,  guildId);
                                break;
                            default:
                                log.LogWarning("Rest error getting member {memberId} because reason {reason}", memberIdSnowflake.Value, restError.Error.Code.Humanize(LetterCasing.Title));
                                break;
                        }
                    } else
                    {
                        log.LogWarning("Failed to get guild member {memberId} because {error}", memberIdSnowflake.Value, guildMemberResult.Error.Message);
                    }
                    continue;
                }

                var guildMember = guildMemberResult.Entity;
                if (!guildMember.Permissions.HasValue)
                {
                    Result<IGuildMember> getPermsResult = await Program.AddGuildMemberPermissions(guildMember, guildId);
                    if (!getPermsResult.IsSuccess)
                    {
                        if (getPermsResult.Error is RestResultError<RestError> restError)
                        {
                            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                            switch (restError.Error.Code)
                            {
                                case DiscordError.InvalidGuild:
                                    log.LogWarning("Guild {guild} is invalid", guildId);
                                    break;
                                case DiscordError.UnknownGuild:
                                    log.LogWarning("Guild {guild} is unknown", guildId);
                                    break;
                                case DiscordError.UnknownMember:
                                    //TODO: Remove these from database
                                    log.LogWarning("Member {member} in guild {guild} is invalid", memberIdSnowflake, guildId);
                                    break;
                                default:
                                    log.LogWarning("Rest error getting member {memberId}'s permissions in {guild} because reason {reason}", memberIdSnowflake.Value, guildId, restError.Error.Code.Humanize(LetterCasing.Title));
                                    break;
                            }
                        } else
                        {
                            log.LogWarning("Failed to get guild member {memberId} permissions in {guild} because {error}", memberIdSnowflake.Value, guildId, getPermsResult.Error.Message);
                        }
                        continue;
                    }

                    guildMember = getPermsResult.Entity;
                }
                membersList.Add(guildMember);
            }
            return Result<IEnumerable<IGuildMember>>.FromSuccess(membersList);
        }

        internal static async Task<Result<List<Snowflake>>> RemoveNonBoosterRoles(Snowflake serverId, CancellationToken ct = new())
        {
            List<Snowflake> peopleRemoved = new();
#if false
            Result<IEnumerable<IGuildMember>> guildBoostersResult = await GetGuildMembers(serverId, checkIsBoosting: true).ConfigureAwait(false);
            if (!guildBoostersResult.IsSuccess)
            {
                log.LogWarning("Could not get guild members for guild {guild} because {reason}", serverId, guildBoostersResult.Error.Message);
                return Result<List<Snowflake>>.FromError(guildBoostersResult.Error);
            }

            IEnumerable<IGuildMember> guildBoosters = guildBoostersResult.Entity;
#endif
            await using Database.DiscordDbContext database = new();
            // Get All Custom Roles Users have created for this guild
            List<Database.RoleData> customRolesCreatedForGuild = await database.RolesCreated.Where(rc => rc.ServerId == serverId.Value).ToListAsync(cancellationToken: ct).ConfigureAwait(false);
            // Get All Guild Roles that are allowed to make custom roles
            // (These are in addition to boosters and anyone with ManageRoles Permission, who can always make roles)
            List<Snowflake>? allowedCreatorRolesSnowflakes = await database.ServerwideSettings.Where(ss => ss.ServerId == serverId.Value).Select(ss => ss.AllowedRolesSnowflakes).AsNoTracking().FirstOrDefaultAsync(cancellationToken: ct).ConfigureAwait(false);
            // Get Guild Member objects for all users that have a custom role in the database, using the store user id snowflakes 
            Result<IEnumerable<IGuildMember>> members = await GetSpecifiedGuildMembers(serverId, customRolesCreatedForGuild.Select(rcfg => new Snowflake(rcfg.RoleUserId)));
            //TODO: Handle Error-ed Guild Member Objects
            // For each User with a role in this server, check if they are allowed to have a custom role
            foreach (IGuildMember member in members.Entity)
            {
                //Check if they are boosting or have ManageRoles permissions
                try
                {

                } catch (Exception e)
                {
                    if (member.IsBoosting() || member.IsRoleModAdminOrOwner())
                    {
                        continue;
                    }
                    log.LogCritical($"Guild User Object for Snowflake {member.User.Value.ID.Value} has thrown error while checking boost status and permissions: {e}");
                    continue;
                }

                //Not a booster or mod, check configured allowed guild roles
                var hasAllowedRole = false;
                if (allowedCreatorRolesSnowflakes is { Count: > 0 })
                {
                    if (allowedCreatorRolesSnowflakes.Any(allowedRoleSnowflake => member.Roles.Contains(allowedRoleSnowflake)))
                    {
                        hasAllowedRole = true;
                    }
                }

                if (hasAllowedRole)
                {
                    // User has one or more of the allowed roles, stop checking
                    continue;
                }

                //Not a booster or mod or allowed role user
                Database.RoleData? roleCreated = customRolesCreatedForGuild.FirstOrDefault(rcfg => rcfg.RoleUserId == member.User.Value.ID.Value);
                if (roleCreated == null)
                {
                    //if no role is found in the database, this is messed up because the member's list was pulled from there, just give up
                    log.LogCritical("Something is very messed up, check this out, the role for {} in Guild {} is no longer in the list", member.User.Value.ID.Value, serverId.Value);
                    break;
                }
                //Not allowed to have, delete role
                Result delRoleResult = await _restGuildApi.DeleteGuildRoleAsync(serverId, new(roleCreated.RoleId), ct: ct).ConfigureAwait(false);
                if (!delRoleResult.IsSuccess)
                {
                    if (delRoleResult.Error is RestResultError<RestError> restError)
                    {
                        if (restError.Error.Code == DiscordError.UnknownRole)
                        {
                            log.LogDebug("Role {role} was deleted without using the database, tell {server} not to do that pls", roleCreated.RoleId, serverId);
                        } else
                        {
                            log.LogError("Rest error deleting role {role} because reason {reason}", roleCreated.RoleId, restError.Error.Code.Humanize(LetterCasing.Title));
                        }
                    } else
                    {
                        log.LogError("Failed to delete role {role} because {error}", roleCreated.RoleId, delRoleResult.Error.Message);
                    }
                }
                database.Remove(roleCreated);
                peopleRemoved.Add(new(roleCreated.RoleUserId));
            }

            int numRows;
            try
            {
                numRows = await database.SaveChangesAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                log.LogCritical("Error Saving Database {error}", e);
                throw;
            }
            if (numRows != peopleRemoved.Count && numRows-1 != peopleRemoved.Count)
            {
                log.LogWarning("Removed {numRows} from db but removed {numRoles} roles", numRows, peopleRemoved.Count);
            }
            return Result<List<Snowflake>>.FromSuccess(peopleRemoved);
        }

        internal static async Task<Result<IGuildMember>> AddGuildMemberPermissions(IGuildMember guildMember,
            Snowflake guildId, CancellationToken ct = default)
        {
            Result<IReadOnlyList<IRole>> guildRoles = await _restGuildApi.GetGuildRolesAsync(guildId, ct: ct);
            if(!guildRoles.IsSuccess) 
                return Result<IGuildMember>.FromError(guildRoles.Error);
            IDiscordPermissionSet discordPermissionSet = DiscordPermissionSet.ComputePermissions(guildMember.User.Value.ID,
                guildRoles.Entity.FirstOrDefault(role => role.ID == guildId)
                ?? guildRoles.Entity.First(), guildRoles.Entity);
            guildMember = ((guildMember as GuildMember)!) with { Permissions = new(discordPermissionSet) };
            return Result<IGuildMember>.FromSuccess(guildMember);
        }

        internal static async Task<Result<bool>> CheckBotOwnerRole(Snowflake serverId, IGuildMember botOwnerGm,
            Database.RoleData roleData, CancellationToken ct = new())
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
                if (!string.IsNullOrWhiteSpace(roleData.ImageUrl))
                {
                    Result<(MemoryStream? iconStream, IImageFormat? imageFormat)> imageToStreamResult =
                        await RoleCommands.ImageUrlToBase64(imageUrl: roleData.ImageUrl, ct: ct).ConfigureAwait(false);
                    if (!imageToStreamResult.IsSuccess)
                    {
                        log.LogCritical(imageToStreamResult.Error.Message);
                        return false;
                    }

                    (iconStream, iconFormat) = imageToStreamResult.Entity;
                }

                Result<IRole> roleResult = await _restGuildApi.CreateGuildRoleAsync(guildID: serverId,
                        name: roleData.Name, colour: ColorTranslator.FromHtml(roleData.Color),
                        icon: iconStream ?? default(Optional<Stream>), isHoisted: false, isMentionable: true, ct: ct)
                    .ConfigureAwait(false);
                if (!roleResult.IsSuccess)
                {
                    log.LogError(
                        $"Could not create role for {botOwnerGm.User.Value.Mention()} because {roleResult.Error}");
                    return false;
                }

                roleData.ImageHash = roleResult.Entity.Icon.Value?.Value;
                IRole role = roleResult.Entity;
                roleData.RoleId = role.ID.Value;
                Result roleApplyResult = await _restGuildApi.AddGuildMemberRoleAsync(guildID: serverId,
                    userID: botOwnerGm.User.Value.ID, roleID: role.ID,
                    "User is boosting, role request via BoostRoleManager bot", ct: ct).ConfigureAwait(false);
                if (!roleApplyResult.IsSuccess)
                {
                    log.LogError($"Could not apply role because {roleApplyResult.Error}");
                    return false;
                }

                string msg = "";
                Result<IReadOnlyList<IRole>> getRolesResult =
                    await _restGuildApi.GetGuildRolesAsync(serverId, ct: ct).ConfigureAwait(false);
                if (getRolesResult.IsSuccess)
                {
                    IReadOnlyList<IRole> guildRoles = getRolesResult.Entity;
                    Result<IUser> currentBotUserResult =
                        await _restUserApi.GetCurrentUserAsync(ct: ct).ConfigureAwait(false);
                    if (currentBotUserResult.IsSuccess)
                    {
                        IUser currentBotUser = currentBotUserResult.Entity;
                        Result<IGuildMember> currentBotMemberResult =
                            await _restGuildApi.GetGuildMemberAsync(serverId, currentBotUser.ID, ct)
                                .ConfigureAwait(false);
                        if (currentBotMemberResult.IsSuccess)
                        {
                            IGuildMember currentBotMember = currentBotMemberResult.Entity;
                            IEnumerable<IRole> botRoles =
                                guildRoles.Where(gr => currentBotMember.Roles.Contains(gr.ID));
                            IRole? maxPosRole = botRoles.MaxBy(br => br.Position);
                            log.LogDebug("Bot's highest role is {role_name}: {roleId}", maxPosRole.Name, maxPosRole.ID);
                            int maxPos = maxPosRole.Position;
                            Result<IReadOnlyList<IRole>> roleMovePositionResult = await _restGuildApi
                                .ModifyGuildRolePositionsAsync(serverId,
                                    new (Snowflake RoleID, Optional<int?> Position)[] { (role.ID, maxPos) }, ct: ct)
                                .ConfigureAwait(false);
                            if (!roleMovePositionResult.IsSuccess)
                            {
                                log.LogWarning($"Could not move the role because {roleMovePositionResult.Error}");
                                return false;
                            }

                            log.LogInformation("Move role to position {pos}", roleMovePositionResult.ToString());
                        }
                        else
                        {
                            log.LogWarning($"Could not get bot member because {currentBotMemberResult.Error}");
                            msg +=
                                "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
                        }
                    }
                    else
                    {
                        log.LogWarning($"Could not get bot user because {currentBotUserResult.Error}");
                        msg +=
                            "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
                    }
                }
                else
                {
                    log.LogWarning($"Could not move the role because {getRolesResult.Error}");
                    msg +=
                        "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
                }

                log.LogInformation($"Made Role {role.Mention()} and assigned to {botOwnerGm.Mention()}");
                return true;
            }
            else
            {
                Color? newRoleColor = null;
                if (ColorTranslator.FromHtml(roleData.Color).ToArgb() != ownerRole.Colour.ToArgb())
                {
                    newRoleColor = ColorTranslator.FromHtml(roleData.Color);
                }

                MemoryStream? imageStream = null;
                string? imageEmoji = null;
                if (roleData.ImageUrl is not null)
                {
                    if (RoleCommands.IsUnicodeEmoji(roleData.ImageUrl))
                    {
                        if (!ownerRole.UnicodeEmoji.HasValue || ownerRole.UnicodeEmoji.Value != roleData.ImageUrl)
                        {
                            imageEmoji = roleData.ImageUrl;
                        }
                    }
                    else
                    {
                        if (roleData.ImageHash is null || !ownerRole.Icon.HasValue ||
                            ownerRole.Icon.Value?.Value != roleData.ImageHash)
                        {
                            Result<(MemoryStream?, IImageFormat?)> imageToStreamResult =
                                await RoleCommands.ImageUrlToBase64(roleData.ImageUrl, ct);
                            if (!imageToStreamResult.IsSuccess)
                            {
                                log.LogCritical("Cannot convert image {imageUrl} to stream", roleData.ImageUrl);
                                imageStream = null;
                            }

                            (imageStream, _) = imageToStreamResult.Entity;
                        }
                    }
                }

                string? newName = null;
                if (roleData.Name != ownerRole.Name)
                {
                    newName = roleData.Name;
                }

                if (newRoleColor is not null || imageStream is not null || imageEmoji is not null ||
                    newName is not null)
                {
                    Result<IRole> modifyRoleResult;
                    if (imageEmoji is not null)
                    {
                        modifyRoleResult = await _restGuildApi.ModifyGuildRoleAsync(serverId, ownerRole.ID,
                            newName ?? default(Optional<string?>),
                            color: newRoleColor ?? default(Optional<Color?>),
                            unicodeEmoji: imageEmoji,
                            reason: $"You changed my role?", ct: ct).ConfigureAwait(false);
                    }
                    else
                    {
                        modifyRoleResult = await _restGuildApi.ModifyGuildRoleAsync(serverId, ownerRole.ID,
                            newName ?? default(Optional<string?>),
                            color: newRoleColor ?? default(Optional<Color?>),
                            icon: imageStream ?? default(Optional<Stream?>),
                            reason: $"You changed my role?", ct: ct).ConfigureAwait(false);
                    }

                    if (!modifyRoleResult.IsSuccess)
                    {
                        log.LogCritical("Role Fix Failed in {server}", serverId);
                    }
                    else
                    {
                        log.LogInformation("Role fixed in {server}", serverId);
                        roleData.ImageHash = modifyRoleResult.Entity.Icon.HasValue
                            ? modifyRoleResult.Entity.Icon.Value?.Value
                            : null;
                    }
                }

                log.LogInformation("Role not changed in {server}", serverId);
                if (!botOwnerGm.Roles.Contains(new Snowflake(roleData.RoleId)))
                {
                    Result roleApplyResult = await _restGuildApi.AddGuildMemberRoleAsync(guildID: serverId,
                        userID: botOwnerGm.User.Value.ID, roleID: ownerRole.ID,
                        "User is boosting, role request via BoostRoleManager bot", ct: ct).ConfigureAwait(false);
                    if (!roleApplyResult.IsSuccess)
                    {
                        log.LogError($"Could not apply role because {roleApplyResult.Error}");
                        return false;
                    }

                    log.LogInformation("Reapplied role in {server}", serverId);
                }

                Result<IReadOnlyList<IRole>> getRolesResult =
                    await _restGuildApi.GetGuildRolesAsync(serverId, ct: ct).ConfigureAwait(false);
                if (getRolesResult.IsSuccess)
                {
                    IReadOnlyList<IRole> guildRoles = getRolesResult.Entity;
                    Result<IUser> currentBotUserResult =
                        await _restUserApi.GetCurrentUserAsync(ct: ct).ConfigureAwait(false);
                    if (currentBotUserResult.IsSuccess)
                    {
                        IUser currentBotUser = currentBotUserResult.Entity;
                        Result<IGuildMember> currentBotMemberResult =
                            await _restGuildApi.GetGuildMemberAsync(serverId, currentBotUser.ID, ct)
                                .ConfigureAwait(false);
                        if (currentBotMemberResult.IsSuccess)
                        {
                            IGuildMember currentBotMember = currentBotMemberResult.Entity;
                            IEnumerable<IRole> botRoles =
                                guildRoles.Where(gr => currentBotMember.Roles.Contains(gr.ID));
                            IRole? maxPosRole = botRoles.MaxBy(br => br.Position);
                            if (maxPosRole != null && ownerRole.Position < maxPosRole.Position - 1)
                            {
                                log.LogDebug("Bot's highest role is {role_name}: {roleId}", maxPosRole.Name,
                                    maxPosRole.ID);
                                int maxPos = maxPosRole.Position;
                                Result<IReadOnlyList<IRole>> roleMovePositionResult = await _restGuildApi
                                    .ModifyGuildRolePositionsAsync(serverId,
                                        new (Snowflake RoleID, Optional<int?> Position)[] { (ownerRole.ID, maxPos) },
                                        ct: ct).ConfigureAwait(false);
                                if (!roleMovePositionResult.IsSuccess)
                                {
                                    log.LogWarning($"Could not move the role because {roleMovePositionResult.Error}");
                                    return false;
                                }

                                log.LogDebug(roleMovePositionResult.ToString());
                            }
                        }
                        else
                        {
                            log.LogWarning($"Could not get bot member because {currentBotMemberResult.Error}");
                        }
                    }
                    else
                    {
                        log.LogWarning($"Could not get bot user because {currentBotUserResult.Error}");
                    }
                }

                return true;

            }
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
