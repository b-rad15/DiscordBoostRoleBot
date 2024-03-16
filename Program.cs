// See https://aka.ms/new-console-template for more information

using System.Drawing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Remora.Commands.Extensions;
using Remora.Discord.API.Abstractions.Gateway.Commands;
using Remora.Discord.Commands.Extensions;
using Remora.Discord.Commands.Services;
using Remora.Discord.Hosting.Extensions;
using Remora.Results;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Abstractions.Results;
using Remora.Discord.API.Objects;
using Remora.Discord.Gateway;
using Remora.Discord.Gateway.Extensions;
using Remora.Discord.Hosting.Options;
using Remora.Extensions.Options.Immutable;
using Remora.Rest.Core;
using Remora.Rest.Results;
using Serilog;
using SixLabors.ImageSharp.Formats;

// using DiscordDbContext = DiscordBoostRoleBot.Database.DiscordDbContextMongoDb;

namespace DiscordBoostRoleBot
{
    public class Program
    {
        private static IDiscordRestGuildAPI? _restGuildApi;
        private static IDiscordRestUserAPI?  _restUserApi;
        public static  bool                  IsInitialized() => _restGuildApi is not null;

        public static ILogger<Program> log;
        public static HttpClient httpClient = new();

        public static ulong BotOwnerId = 0;

        private static Database database;

        public static async Task Main(string[] args)
        {
            // Setup MongoDB type mappings
            RegisterClassMaps();
            //Build the service
            IHost? host = Host.CreateDefaultBuilder(args)
                              .ConfigureAppConfiguration((hostingContext, config) =>
                              {
                                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                                            .AddJsonFile($"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json",
                                                                                                       optional: true, reloadOnChange: true)
                                            .AddJsonFile("secrets.json", optional: true, reloadOnChange: true)
                                            .AddEnvironmentVariables()
                                            .AddCommandLine(args);
                              })  
                              .AddDiscordService(services =>
                                  services.GetRequiredService<IConfiguration>().GetValue<string>("Token")!)
                              .ConfigureServices(
                                  (hostBuilderContext, services) =>
                                  {
                                      services
                                          .Configure<MongoDbSettings>(hostBuilderContext.Configuration.GetSection("MongoDbSettings"))
                                          .AddSingleton<Database>()
                                          .AddTransient<ICommandPrefixMatcher, PrefixSetter>()
                                          .AddCommandTree()
                                              .WithCommandGroup<RoleCommands>()
                                              .WithCommandGroup<AddReactionsToMediaArchiveCommands>()
                                              .WithCommandGroup<CommandResponderConfigCommands>()
                                          .Finish()
                                          .AddResponder<AddReactionsToMediaArchiveMessageResponder>()
                                          .AddHostedService<RolesRemoveService>()
                                          // .AddResponder<BasicInteractionResponder>()
                                          .AddPreparationErrorEvent<PreparationErrorEventResponder>()
                                          .AddPostExecutionEvent<PostExecutionErrorResponder>()
                                          .Configure<DiscordGatewayClientOptions>(g =>
                                              g.Intents |= GatewayIntents.MessageContents)
                                          .AddDiscordCommands(true)
                                          ;
                                      services.Configure<DiscordServiceOptions>(_ => new()
                                          {
                                              TerminateApplicationOnCriticalGatewayErrors = true,
                                          });
                                  })
                              .UseSerilog((hostingContext, services, loggerConfiguration) => loggerConfiguration
                                  .ReadFrom.Configuration(hostingContext.Configuration))
                              .UseConsoleLifetime()
                              .Build();
            IServiceProvider? services = host.Services;
            log           = services.GetRequiredService<ILogger<Program>>();
            _restGuildApi = services.GetRequiredService<IDiscordRestGuildAPI>();
            _restUserApi  = services.GetRequiredService<IDiscordRestUserAPI>();
            database      = services.GetRequiredService<Database>();
            var configuration = services.GetRequiredService<IConfiguration>();
            BotOwnerId = configuration.GetValue<ulong>("BotOwnerId");
            Snowflake? debugServer = null;
            var debugServerId = configuration.GetValue<string?>("TestServerId");
            if (debugServerId is not null)
            {
                if (!Snowflake.TryParse(debugServerId, out debugServer))
                {
                    log.LogWarning("Failed to parse debug server from environment");
                }
            }
            else
            {
                log.LogWarning("No debug server specified");
            }

            SlashService slashService = services.GetRequiredService<SlashService>();
            if (args.Contains("--purge") && debugServer.HasValue){
                Result purgeSlashCommands = await slashService
                                                  .UpdateSlashCommandsAsync(guildID: debugServer, treeName: nameof(EmptyCommands), CancellationToken.None)
                                                  .ConfigureAwait(false);
                if (!purgeSlashCommands.IsSuccess)
                {
                    log.LogWarning("Failed to purge guild slash commands: {Reason}", purgeSlashCommands.Error?.Message);
                }
                else
                {
                    log.LogInformation("Purge slash commands from {Reason}", debugServer.Value);
                }
#if RELEASE
                purgeSlashCommands = await slashService
                                                  .UpdateSlashCommandsAsync(treeName: nameof(EmptyCommands))
                                                  .ConfigureAwait(false);
                if (!purgeSlashCommands.IsSuccess)
                {
                    log.LogWarning("Failed to purge guild slash commands: {Reason}", purgeSlashCommands.Error?.Message);
                } else
                {
                    log.LogInformation("Purge slash commands from {Reason}", debugServer.Value);
                }
#endif
            }
            Snowflake? slashDebugServer =
        #if DEBUG
                debugServer;
        #else
                null;
        #endif
        #if DEBUG
            Result updateSlashGlobal = await slashService.UpdateSlashCommandsAsync(debugServer).ConfigureAwait(false);
        #else
            Result updateSlashGlobal = await slashService.UpdateSlashCommandsAsync().ConfigureAwait(false);
        #endif
            if (!updateSlashGlobal.IsSuccess)
            {
                if (updateSlashGlobal.Error is RestResultError<RestError> restError)
                    log.LogCritical("Failed to update slash commands globally: {code} {reason}", restError.Error.Code, restError.Error.Message);
                else
                    log.LogCritical("Failed to update slash commands globally: {Reason}", updateSlashGlobal.Error?.Message);
            } else
            {
                log.LogInformation("Successfully updated slash commands globally");
            }
            // Register for each slash command type
            if (slashDebugServer is not null)
            {
                log.LogInformation("Creating commands on {debugServer}", slashDebugServer);
            }
            foreach (string? slashTree in new string?[]
                     {
                         
                     })
            {
                Result updateSlash = await slashService.UpdateSlashCommandsAsync(guildID: slashDebugServer, treeName: slashTree).ConfigureAwait(false);
                if (!updateSlash.IsSuccess)
                {
                    if(updateSlash.Error is RestResultError<RestError> restError)
                        log.LogCritical("Failed to update slash commands for tree {treeName}: {code} {reason}", slashTree, restError.Error.Code, restError.Error.Message);
                    else
                        log.LogCritical("Failed to update slash commands for tree {treeName}: {Reason}", slashTree, updateSlash.Error?.Message);
                } else
                {
                    log.LogInformation("Successfully updated slash commands for tree {treeName}", slashTree);
                }
            }
            await host.RunAsync().ConfigureAwait(false);

            Console.WriteLine("Bye bye");
        }

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
                        if(!restError.Error.Code.HasValue) log.LogWarning("Rest error getting member {memberId} because reason {reason}: {message}", memberIdSnowflake.Value, "Code Not Given", restError.Error.Message);
                        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                        switch (restError.Error.Code.Value)
                        {
                            case DiscordError.InvalidGuild:
                                log.LogWarning("Guild {guild} is invalid: {message}", guildId, restError.Error.Message);
                                break;
                            case DiscordError.UnknownGuild:
                                log.LogInformation("Guild {guild} is unknown: {message}", guildId, restError.Error.Message);
                                break;
                            case DiscordError.UnknownMember:
                                //TODO: Remove these from database
                                log.LogWarning("Member {member} in guild {guild} is invalid: {message}", memberIdSnowflake,  guildId, restError.Error.Message);
                                break;
                            default:
                                log.LogWarning("Rest error getting member {memberId} because reason {reason}: {message}", memberIdSnowflake.Value, restError.Error.Code, restError.Error.Message);
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
                            if (!restError.Error.Code.HasValue) log.LogWarning("Rest error getting member {memberId}'s permissions in {guild} because reason {reason}: {message}", memberIdSnowflake.Value, guildId, "Code Not Given", restError.Message);
                            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                            switch (restError.Error.Code.Value)
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
                                    log.LogWarning("Rest error getting member {memberId}'s permissions in {guild} because reason {reason}: {message}", memberIdSnowflake.Value, guildId, restError.Error.Code, restError.Message);
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

        internal static async Task<Result<List<IGuildMember>>> RemoveNonBoosterRoles(Snowflake serverId, CancellationToken ct = new())
        {
            List<IGuildMember> peopleRemoved = new();
#if false
            Result<IEnumerable<IGuildMember>> guildBoostersResult = await GetGuildMembers(serverId, checkIsBoosting: true).ConfigureAwait(false);
            if (!guildBoostersResult.IsSuccess)
            {
                log.LogWarning("Could not get guild members for guild {guild} because {reason}", serverId, guildBoostersResult.Error.Message);
                return Result<List<Snowflake>>.FromError(guildBoostersResult.Error);
            }

            IEnumerable<IGuildMember> guildBoosters = guildBoostersResult.Entity;
#endif
            // await using Database.DiscordDbContext database = new();
            // Get All Custom Roles Users have created for this guild
            List<Database.RoleData> customRolesCreatedForGuild = await database.GetRoles(guildId: serverId);
            // Get All Guild Roles that are allowed to make custom roles
            // (These are in addition to boosters and anyone with ManageRoles Permission, who can always make roles)
            List<Snowflake>? allowedCreatorRolesSnowflakes = await database.GetAllowedRoles(serverId);
            // Get Guild Member objects for all users that have a custom role in the database, using the store user id snowflakes 
            Result<IEnumerable<IGuildMember>> members = await GetSpecifiedGuildMembers(serverId, customRolesCreatedForGuild.Select(rcfg => rcfg.RoleUserId));
            //TODO: Handle Error-ed Guild Member Objects
            // For each User with a role in this server, check if they are allowed to have a custom role
            foreach (IGuildMember member in members.Entity)
            {
                //Check if they are boosting or have ManageRoles permissions
                try
                {
                    if (member.IsBoosting() || member.IsRoleModAdminOrOwner())
                    {
                        continue;
                    }
                } catch (Exception e)
                {
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
                Result delRoleResult = await _restGuildApi.DeleteGuildRoleAsync(serverId, roleCreated.RoleId, ct: ct).ConfigureAwait(false);
                if (!delRoleResult.IsSuccess)
                {
                    if (delRoleResult.Error is RestResultError<RestError> restError)
                    {
                        if (restError.Error.Code == DiscordError.UnknownRole)
                        {
                            log.LogDebug("Role {role} was deleted without using the database, tell {server} not to do that pls", roleCreated.RoleId, serverId);
                        } else
                        {
                            log.LogError("Rest error deleting role {role} because reason {reason}", roleCreated.RoleId, restError.Error.Code);
                        }
                    } else
                    {
                        log.LogError("Failed to delete role {role} because {error}", roleCreated.RoleId, delRoleResult.Error.Message);
                    }
                }
                // TODO: aggregate the roles and bulk remove them
                await database.RemoveRoleFromDatabase(roleCreated.ServerId, roleCreated.RoleId);
                peopleRemoved.Add(member);
            }
            return Result<List<IGuildMember>>.FromSuccess(peopleRemoved);
        }

        internal static async Task<Result<IGuildMember>> AddGuildMemberPermissions(IGuildMember guildMember,
            Snowflake guildId, CancellationToken ct = default)
        {
            Result<IReadOnlyList<IRole>> guildRoles = await _restGuildApi.GetGuildRolesAsync(guildId, ct: ct);
            if(!guildRoles.IsSuccess) 
                return Result<IGuildMember>.FromError(guildRoles.Error);
            IReadOnlyList<IRole> memberRoles = guildRoles.Entity.Where(role => guildMember.Roles.Contains(role.ID)).ToList();
            IDiscordPermissionSet discordPermissionSet = DiscordPermissionSet.ComputePermissions(guildMember.User.Value.ID,
                guildRoles.Entity.FirstOrDefault(role => role.ID == guildId)
                ?? guildRoles.Entity.First(), memberRoles);
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
                        icon: iconStream ?? default(Optional<Stream?>), isHoisted: false, isMentionable: true, ct: ct)
                    .ConfigureAwait(false);
                if (!roleResult.IsSuccess)
                {
                    log.LogError(
                        $"Could not create role for {botOwnerGm.User.Value.Mention()} because {roleResult.Error}");
                    return false;
                }

                roleData.ImageHash = roleResult.Entity.Icon.Value?.Value;
                IRole role = roleResult.Entity;
                roleData.RoleId = role.ID;
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
                        log.LogWarning("Could not get bot user because {error}", currentBotUserResult.Error);
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
                if (!botOwnerGm.Roles.Contains(roleData.RoleId))
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

        internal static void RegisterClassMaps()
        {
            // Convert Snowflakes to ulongs
            BsonSerializer.RegisterSerializer(new SnowflakeSerializer());

            BsonClassMap.RegisterClassMap<Database.RoleData>(cm =>
            {
                cm.AutoMap();
                cm.MapMember(c => c.RoleId).SetElementName("role_id").SetIsRequired(true);
                cm.MapMember(c => c.ServerId).SetElementName("server_id").SetIsRequired(true);
                cm.MapMember(c => c.RoleUserId).SetElementName("role_user_id").SetIsRequired(true);
                cm.MapMember(c => c.Color).SetElementName("color");
                cm.MapMember(c => c.Name).SetElementName("name");
                cm.MapMember(c => c.ImageUrl).SetElementName("image_url");
                cm.MapMember(c => c.ImageHash).SetElementName("image_hash");
                cm.SetIgnoreExtraElements(true);
            });

            BsonClassMap.RegisterClassMap<Database.ServerSettings>(cm =>
            {
                cm.AutoMap();
                cm.MapMember(c => c.ServerId).SetElementName("server_id").SetIsRequired(true);
                cm.MapMember(c => c.Prefix).SetElementName("prefix").SetDefaultValue('&');
                cm.MapMember(c => c.AllowedRolesSnowflakes).SetElementName("allowed_roles_snowflakes")
                  .SetDefaultValue(new List<Snowflake>()).SetIgnoreIfDefault(false).SetIgnoreIfNull(true);
                cm.SetIgnoreExtraElements(true);
            });

            BsonClassMap.RegisterClassMap<Database.MessageReactorSettings>(cm =>
            {
                cm.AutoMap();
                cm.MapMember(c => c.ServerId).SetElementName("server_id").SetIsRequired(true);
                cm.MapMember(c => c.ChannelId).SetElementName("channel_id");
                cm.MapMember(c => c.UserIds).SetElementName("user_ids");
                cm.MapMember(c => c.Emotes).SetElementName("emotes");
                cm.SetIgnoreExtraElements(true);
            });
        }

        class SnowflakeSerializer : SerializerBase<Snowflake>
        {
            public override Snowflake Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
            {
                switch (context.Reader.CurrentBsonType)
                {
                    case BsonType.Null:
                        context.Reader.ReadNull();
                        return default;
                    case BsonType.Decimal128:
                        return new((ulong)context.Reader.ReadDecimal128());
                    case BsonType.Int64:
                        return new((ulong)context.Reader.ReadInt64());
                    case BsonType.Int32:
                        return new((ulong)context.Reader.ReadInt32());
                    default:
                        throw new FormatException($"Cannot deserialize a Snowflake from a {context.Reader.CurrentBsonType}");
                }
            }

            public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args,
                Snowflake                                           value)
            {
                context.Writer.WriteDecimal128(value.Value);
            }
        }
    }
}
