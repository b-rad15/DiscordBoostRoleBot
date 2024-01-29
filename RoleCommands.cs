using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Remora.Commands.Attributes;
using Remora.Commands.Groups;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.Commands.Attributes;
using Remora.Discord.Commands.Contexts;
using Remora.Discord.Commands.Feedback.Messages;
using Remora.Discord.Commands.Feedback.Services;
using Remora.Discord.Extensions.Embeds;
using Remora.Discord.Commands.Conditions;
using Remora.Rest.Core;
using Remora.Results;
using System.Reflection;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Responders;
using Remora.Discord.Commands.Results;
using Remora.Rest.Results;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using Color = System.Drawing.Color;
using Image = SixLabors.ImageSharp.Image;

namespace DiscordBoostRoleBot
{
    public class RoleCommands : CommandGroup
    {
        private readonly FeedbackService _feedbackService;
        private readonly ICommandContext _context;
        private static IDiscordRestGuildAPI _restGuildApi;
        private readonly IDiscordRestUserAPI _restUserApi;
        private static IDiscordRestChannelAPI _restChannelApi;
        private static ILogger<RoleCommands> _log;

        /// <summary>
        /// Initializes a new instance of the <see cref="RoleCommands"/> class.
        /// </summary>
        /// <param name="feedbackService">The feedback service.</param>
        /// <param name="context">The command context.</param>
        /// <param name="restGuildApi">The DiscordRestGuildAPI to allow guild api access.</param>
        /// <param name="log">The logger used</param>
        /// <param name="restUserApi">Access to the User rest API</param>
        /// <param name="restChannelApi">Access to the Channel rest API</param>
        public RoleCommands(FeedbackService feedbackService, ICommandContext context, IDiscordRestGuildAPI restGuildApi, ILogger<RoleCommands> log, IDiscordRestUserAPI restUserApi, IDiscordRestChannelAPI restChannelApi)
        {
            _feedbackService = feedbackService;
            _context = context;
            _restGuildApi = restGuildApi;
            _log = log;
            _restUserApi = restUserApi;
            _restChannelApi = restChannelApi;
        }

        public static Color GetColorFromString(string colorString) => ColorTranslator.FromHtml(colorString);

        public static readonly Regex Base64Regex = new(@"^([A-Za-z0-9+/]{4})*([A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{2}==)?$");

        public static async Task<Result<IGuildMember>> ExecutorHasPermissions(ICommandContext _context, params DiscordPermission[] permissions)
        {
            IGuildMember executorGuildMember;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executorGuildMember = interactionContext.Interaction.Member.Value;
                    break;
                case TextCommandContext messageContext:
                {
                    Result<IGuildMember> guildMemberResult = await _restGuildApi.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.Message.Author.Value.ID).ConfigureAwait(false);
                    if (!guildMemberResult.IsSuccess)
                    {
                        _log.LogWarning($"Error responding to message {messageContext.Message} because {guildMemberResult.Error}");
                        return Result<IGuildMember>.FromError(new InvalidOperationError("Make sure you are in a server"));
                    }
                    executorGuildMember = guildMemberResult.Entity;
                    break;
                }
                default:
                    _log.LogWarning("I don't know how you invoked this command");
                    return Result<IGuildMember>.FromError(new InvalidOperationError("I don't know how you invoked this command"));
            }
            if (!executorGuildMember.HasAllPermsAdminOrOwner(permissions))
            {
                return Result<IGuildMember>.FromError(new PermissionDeniedError($"You do not have the required permission{(permissions.Length != 1 ? (permissions.Length != 0 ? "s:" : "s") : ":")} {string.Join(", ", permissions)}"));
            }
            return Result<IGuildMember>.FromSuccess(executorGuildMember);
        }

        public async Task<Result<IGuildMember>> ExecutorHasPermissions(params DiscordPermission[] permissions) =>
            await ExecutorHasPermissions(_context, permissions);

        [Command("role-creator-add")]
        [CommandType(ApplicationCommandType.ChatInput)]
        [Description("Add a role that is allowed to make their own custom roles")]
        public async Task<IResult> AddAllowedRoleMaker([Description("The Role to allow to make their own Custom Roles")] IRole roleToAdd)
        {
            Result<IGuildMember> permCheckResult = await ExecutorHasPermissions(DiscordPermission.ManageRoles);
            if (!permCheckResult.IsSuccess)
            {
                Result<IReadOnlyList<IMessage>> errResponse = await _feedbackService.SendContextualErrorAsync(permCheckResult.Error.Message);
                return errResponse.IsSuccess ?
                    Result.FromSuccess() :
                    Result.FromError(errResponse);
            }
            PartialGuild executionGuild = new(_context switch
            {
                InteractionContext interactionContext => interactionContext.Interaction.GuildID.Value,
                TextCommandContext messageContext => messageContext.GuildID.Value,
                _ => throw new ArgumentOutOfRangeException(nameof(_context)),
            });
            Result<IReadOnlyList<IMessage>> response;
            await using Database.DiscordDbContext database = new();
            Database.ServerSettings? serverSettings = await database.ServerwideSettings.Where(ss => ss.ServerId == executionGuild.ID.Value.Value).FirstOrDefaultAsync().ConfigureAwait(false);
            if (serverSettings == null)
            {
                serverSettings = new()
                {
                    AllowedRolesSnowflakes = new(){roleToAdd.ID},
                    ServerId = executionGuild.ID.Value.Value
                };
                database.Add(serverSettings);
            } else
            {
                if (serverSettings.AllowedRolesSnowflakes.Contains(roleToAdd.ID))
                {
                    response = await _feedbackService.SendContextualInfoAsync(
                        $"Role {roleToAdd.Mention()} is already allowed to make custom roles");
                    return response.IsSuccess ? Result.FromSuccess() : Result.FromError(response);
                }

                serverSettings.AllowedRolesSnowflakes.Add(roleToAdd.ID);
            }
            int numRows = await database.SaveChangesAsync().ConfigureAwait(false);
            response = await _feedbackService.SendContextualInfoAsync(
                $"Role {roleToAdd.Mention()} is now allowed to make custom roles");
            return response.IsSuccess ? Result.FromSuccess() : Result.FromError(response);
        }
        [Command("role-creator-remove")]
        [CommandType(ApplicationCommandType.ChatInput)]
        [Description("Remove a role that is allowed to make their own custom roles")]
        public async Task<IResult> RemoveAllowedRoleMaker([Description("The Role to remove the ability to make their own Custom Roles")] IRole roleToRemove)
        {
            Result<IGuildMember> permCheckResult = await ExecutorHasPermissions(DiscordPermission.ManageRoles);
            if (!permCheckResult.IsSuccess)
            {
                Result<IReadOnlyList<IMessage>> errResponse = await _feedbackService.SendContextualErrorAsync(permCheckResult.Error.Message);
                return errResponse.IsSuccess ?
                    Result.FromSuccess() :
                    Result.FromError(errResponse);
            }
            PartialGuild executionGuild = new(_context switch
            {
                InteractionContext interactionContext => interactionContext.Interaction.GuildID.Value,
                TextCommandContext messageContext     => messageContext.GuildID.Value,
                _                                     => throw new ArgumentOutOfRangeException(nameof(_context)),
            });
            Result<IReadOnlyList<IMessage>> response;
            var removeWarning = "(Note: mods, role admins, and boosters are still allowed, use slash command permissions to ban users/roles)";
            await using Database.DiscordDbContext database = new();
            Database.ServerSettings? serverSettings = await database.ServerwideSettings.Where(ss => ss.ServerId == executionGuild.ID.Value.Value).FirstOrDefaultAsync().ConfigureAwait(false);
            if (serverSettings == null || serverSettings.AllowedRolesSnowflakes.Count == 0)
            {
                response = await _feedbackService.SendContextualInfoAsync(
                    $"No specific roles are setup to allow role creation, only mods, admins, and boosters");
                return response.IsSuccess ? Result.FromSuccess() : Result.FromError(response);
            }
            if (!serverSettings.AllowedRolesSnowflakes.Contains(roleToRemove.ID))
            {
                response = await _feedbackService.SendContextualInfoAsync(
                    $"Role {roleToRemove.Mention()} was not allowed to make custom roles, no change made {removeWarning}");
                return response.IsSuccess ? Result.FromSuccess() : Result.FromError(response);
            }
            serverSettings.AllowedRolesSnowflakes.Remove(roleToRemove.ID);
            int numRows = await database.SaveChangesAsync().ConfigureAwait(false);
            response = await _feedbackService.SendContextualInfoAsync(
                $"Role {roleToRemove.Mention()} is now not allowed to make custom roles {removeWarning}");
            return response.IsSuccess ? Result.FromSuccess() : Result.FromError(response);
        }

        [Command("role-creator-list")]
        [CommandType(ApplicationCommandType.ChatInput)]
        [Description("Lists roles allowed to make their own custom roles")]
        public async Task<IResult> ListAllowedRolemaker()
        {
            Result<IReadOnlyList<IMessage>> response;
            PartialGuild executionGuild = new(_context switch
            {
                InteractionContext interactionContext => interactionContext.Interaction.GuildID.Value,
                TextCommandContext messageContext     => messageContext.GuildID.Value,
                _                                     => throw new ArgumentOutOfRangeException(nameof(_context)),
            });
            await using Database.DiscordDbContext database = new();
            List<Snowflake>? allowedRolesSnowflakes = await database.ServerwideSettings.Where(ss => ss.ServerId == executionGuild.ID.Value.Value).Select(ss => ss.AllowedRolesSnowflakes).AsNoTracking().FirstOrDefaultAsync().ConfigureAwait(false);
            if (allowedRolesSnowflakes == null || allowedRolesSnowflakes.Count == 0)
            {
                response = await _feedbackService.SendContextualInfoAsync(
                    $"No specific roles are setup to allow role creation, only mods, admins, and boosters");
                return response.IsSuccess ? Result.FromSuccess() : Result.FromError(response);
            }

            string responseString =
                $@"Current roles allowed to make custom roles:
{string.Join('\n', allowedRolesSnowflakes.Select(snowflake => snowflake.Role()))}
Note: All users with server role change permissions and boosters are allowed to make custom roles.";
            response = await _feedbackService.SendContextualInfoAsync(responseString);
            return response.IsSuccess ? Result.FromSuccess() : Result.FromError(response);
        }

        public string? EmoteToDiscordUrl(string emote)
        {
            Match regexMatch = AddReactionsToMediaArchiveMessageResponder.EmoteWithRequiredIdRegex.Match(emote);
            return regexMatch.Success ? $"https://cdn.discordapp.com/emojis/{regexMatch.Groups["id"]}.{(regexMatch.Groups["animated"].Success ? "gif" : "png")}" : null;
        }

        //[RequireContext(ChannelContext.Guild)]
        // [RequireDiscordPermission(DiscordPermission.ManageRoles | DiscordPermission.Administrator)]
        [Command("make-role")]
        [CommandType(type: ApplicationCommandType.ChatInput)]
        [RequireContext(ChannelContext.Guild)]
        [Description("Make a new role, attach an image to add it to the role")]
        public async Task<IResult> MakeNewRole([Description("Role Name")] string role_name,
            [Description("Color in #XxXxXx format or common name, use black or #000000 to keep current color")] string color = "#000000",
            [Description("The User to assign the role to")] IGuildMember? assign_to_member = null,
            [Description("The image url to use for the icon")] string? image_url = null
            )
        {
            IGuildMember                    executorGuildMember;
            IPartialChannel                        executionChannel;
            IPartialGuild                           executionGuild;
            Result<IReadOnlyList<IMessage>> errResponse;
            Result                          deleteResponse;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executorGuildMember = interactionContext.Interaction.Member.Value;
                    executionGuild      = new PartialGuild(interactionContext.Interaction.GuildID.Value);
                    Optional<IMessage> interactionMessage = interactionContext.Interaction.Message;
                    if (interactionMessage.HasValue)
                    {
                        IReadOnlyList<IAttachment> attachments = interactionMessage.Value.Attachments;
                        if (attachments[0].ContentType is { HasValue: true, Value: "image/jpeg" or "image/png" or "image/gif" })
                        {
                            image_url = attachments[0].Url;
                        }
                    }
                    executionChannel = interactionContext.Interaction.Channel.Value;
                    // Result<IReadOnlyList<IMessage>> errResponse = await _feedbackService.SendContextualErrorAsync("This can only be executed via slash command");
                    // return errResponse.IsSuccess
                    //     ? Result.FromSuccess()
                    //     : Result.FromError(errResponse);
                    break;
                case TextCommandContext messageContext:
                {
                    Optional<IUser>           commandUser     = messageContext.Message.Author;
                    executionChannel = new PartialChannel(messageContext.Message.ChannelID);
                    executionGuild   = new PartialGuild(messageContext.GuildID.Value);
                    Result<IGuildMember> guildMemberResult = await _restGuildApi.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: commandUser.Value.ID).ConfigureAwait(false);
                    if (!guildMemberResult.IsSuccess)
                    {
                        _log.LogWarning($"Error responding to message {messageContext.Message.ID.Value} because {guildMemberResult.Error}");
                        errResponse = await _feedbackService.SendContextualErrorAsync("Make sure you are in a server").ConfigureAwait(false);
                        if (true)
                        {
                            return errResponse.IsSuccess
                                ? Result.FromSuccess()
                                : Result.FromError(result: errResponse);
                        }
                    }
                    executorGuildMember = guildMemberResult.Entity;
                    if (messageContext.Message.Attachments.HasValue && messageContext.Message.Attachments.Value.Count > 0)
                    {
                        IReadOnlyList<IAttachment> attachments = messageContext.Message.Attachments.Value;
                        if (attachments[0].ContentType == "image/jpeg" || attachments[0].ContentType == "image/png" ||
                            attachments[0].ContentType == "image/gif")
                        {
                            image_url = attachments[0].Url;
                        }
                    }

                    break;
                }
                default:
                    errResponse = await _feedbackService.SendContextualErrorAsync("I don't know how you invoked this command").ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                    break;
            }
            if (!executorGuildMember.Permissions.HasValue)
            {
                Result<IGuildMember> getPermsResult = await Program.AddGuildMemberPermissions(executorGuildMember, executionGuild.ID.Value);
                if (!getPermsResult.IsSuccess)
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync("Could not determine User's permission, please evoke via slash command", options: new FeedbackMessageOptions
                    {
                        MessageFlags = MessageFlags.Ephemeral
                    }).ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }

            assign_to_member ??= executorGuildMember;
            //Run input checks
            //If you are (not the user you're trying to assign to or are not premium) and you are not a mod/owner then deny you
            if (!executorGuildMember.IsRoleModAdminOrOwner())
            {
                //Not a mod, check if assigning to self
                if (executorGuildMember.User.Value.ID != assign_to_member.User.Value.ID)
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync(
                        $"Non-mods can only assign roles to themselves").ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
                //Not mod and assigning to "assign_to_member", check if boosting
                if (assign_to_member.IsNotBoosting())
                {
                    //Not Boosting, check if has allowed role
                    await using Database.DiscordDbContext database = new();
                    List<Snowflake>? allowedRolesSnowflakes = await database.ServerwideSettings.Where(ss => ss.ServerId == executionGuild.ID.Value.Value).Select(ss => ss.AllowedRolesSnowflakes).AsNoTracking().FirstOrDefaultAsync().ConfigureAwait(false);
                    bool hasAllowedRole = false;
                    if (allowedRolesSnowflakes != null)
                    {
                        if (allowedRolesSnowflakes.Any(allowedRoleSnowflake => assign_to_member.Roles.Contains(allowedRoleSnowflake)))
                        {
                            hasAllowedRole = true;
                        }
                    }

                    if (!hasAllowedRole)
                    {
                        errResponse = await _feedbackService.SendContextualErrorAsync(
                            $"Non-boosters need an approved role to be allowed to use this bot").ConfigureAwait(false);
                        return errResponse.IsSuccess
                            ? Result.FromSuccess()
                            : Result.FromError(result: errResponse);
                    }
                }
            }
            //Check if you are not a mod and have more than one role
            if (!executorGuildMember.IsRoleModAdminOrOwner() && 0 < await Database.GetRoleCount(executionGuild.ID.Value.Value, assign_to_member.User.Value.ID.Value).ConfigureAwait(false))
            {
                return await SendErrorReply("You are only allowed one booster role on this server").ConfigureAwait(false);
            }

            //Declare necessary variables
            Result<IReadOnlyList<IMessage>> reply;
            //Check arguments and initialize variables
            //Prepare Color
            Color roleColor;
            try
            {
                roleColor = GetColorFromString(color);
            } catch (ArgumentException e)
            {
                _log.LogWarning("Color not found {color} because {e}", color, e);
                errResponse = await _feedbackService
                    .SendContextualErrorAsync(
                        $"Invalid color {color}, must be in the format #XxXxXx or a common color name, check your spelling")
                    .ConfigureAwait(false);
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(result: errResponse);
            } catch (FormatException e) {
                _log.LogWarning("Color not found {color} because {e}", color, e);
                errResponse = await _feedbackService
                    .SendContextualErrorAsync(
                                               $"Invalid color {color}, must be in the format #XxXxXx or a common color name, check your spelling (e.g. make sure 0s aren't Os, hex codes are only 0-9 and a-f letters)")
                    .ConfigureAwait(false);
                if (true)
                {
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            } catch (Exception e)
            {
                _log.LogError(e, "Error getting color from string {color}: {reason}", color, e.Message);
                errResponse = await _feedbackService.SendContextualErrorAsync($"Error getting color from string {color}").ConfigureAwait(false);
                if (true)
                {
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }
            if (!executionGuild.ID.HasValue)
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("You are not sending this command in a guild, somehow your permissions are broken", ct: this.CancellationToken).ConfigureAwait(false);
                if (true)
                {
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }
            //Prepare server
            Snowflake requestServer = executionGuild.ID.Value;
            //Prepare Image
            MemoryStream? iconStream = null;
            IImageFormat? iconFormat = null;
            if (image_url is not null)
            {
                if (AddReactionsToMediaArchiveMessageResponder.EmoteWithRequiredIdRegex.IsMatch(image_url))
                {
                    image_url = EmoteToDiscordUrl(image_url);
                } else if(AddReactionsToMediaArchiveMessageResponder.EmoteWithoutRequiredIdRegex.IsMatch(image_url))
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync("Please Choose Emoji from selection menu, simply typing the emoji make getting the image impossible");
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(errResponse);
                }
                Result<(MemoryStream?, IImageFormat?)> imageToStreamResult = await ImageUrlToBase64(imageUrl: image_url).ConfigureAwait(false);
                if (!imageToStreamResult.IsSuccess)
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync(imageToStreamResult.Error.Message);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(errResponse);
                }

                (iconStream, iconFormat) = imageToStreamResult.Entity;
            }

            Result<IRole> roleResult = await _restGuildApi.CreateGuildRoleAsync(guildID: requestServer, name: role_name, colour: roleColor, icon: iconStream ?? default(Optional<Stream>),
                isHoisted: false, isMentionable: true, ct: this.CancellationToken).ConfigureAwait(false);
            if (!roleResult.IsSuccess)
            {
                _log.LogError($"Could not create role for {assign_to_member.User.Value.Mention()} because {roleResult.Error}");
                errResponse = await _feedbackService.SendContextualErrorAsync(
                    $"Could not create role for {assign_to_member.User.Value.Mention()}, make sure the bot's permissions are set correctly. Error = {roleResult.Error.Message}", ct: this.CancellationToken).ConfigureAwait(false);
                if (true)
                {
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }
            IRole role = roleResult.Entity;
            bool addedToDb = await Database.AddRoleToDatabase(executionGuild.ID.Value.Value, assign_to_member.User.Value.ID.Value,
                role.ID.Value, color: color, name: role.Name, imageUrl: image_url, role.Icon.HasValue ? role.Icon.Value?.Value : null).ConfigureAwait(false);
            if (!addedToDb)
            {
                _log.LogError($"Could not add role to database");
                errResponse = await _feedbackService.SendContextualErrorAsync(
                    "Failed to track role, try again later", ct: this.CancellationToken).ConfigureAwait(false);
                if (true)
                {
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }
            Result roleApplyResult = await _restGuildApi.AddGuildMemberRoleAsync(guildID: executionGuild.ID.Value,
                userID: assign_to_member.User.Value.ID, roleID: role.ID,
                "User is boosting, role request via BoostRoleManager bot", ct: this.CancellationToken).ConfigureAwait(false);
            if (!roleApplyResult.IsSuccess)
            {
                _log.LogError($"Could not make role because {roleApplyResult.Error}");
                errResponse = await _feedbackService.SendContextualErrorAsync(
                    "Could not make role, make sure the bot's permissions are set correctly", ct: this.CancellationToken).ConfigureAwait(false);
                if (true)
                {
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }

            var msg = string.Empty;
            Result<IReadOnlyList<IRole>> getRolesResult = await _restGuildApi.GetGuildRolesAsync(executionGuild.ID.Value, ct: this.CancellationToken).ConfigureAwait(false);
            if (getRolesResult.IsSuccess)
            {
                IReadOnlyList<IRole> guildRoles = getRolesResult.Entity;
                Result<IUser> currentBotUserResult =
                    await _restUserApi.GetCurrentUserAsync(ct: this.CancellationToken).ConfigureAwait(false);
                if (currentBotUserResult.IsSuccess)
                {
                    IUser currentBotUser = currentBotUserResult.Entity;
                    Result<IGuildMember> currentBotMemberResult =
                        await _restGuildApi.GetGuildMemberAsync(executionGuild.ID.Value, currentBotUser.ID,
                            this.CancellationToken).ConfigureAwait(false);
                    if (currentBotMemberResult.IsSuccess)
                    {
                        IGuildMember currentBotMember = currentBotMemberResult.Entity;
                        IEnumerable<IRole> botRoles = guildRoles.Where(gr => currentBotMember.Roles.Contains(gr.ID));
                        IRole? maxPosRole = botRoles.MaxBy(br => br.Position);
                        _log.LogDebug("Bot's highest role is {role_name}: {roleId}", maxPosRole.Name, maxPosRole.ID);
                        int maxPos = maxPosRole.Position;
                        Result<IReadOnlyList<IRole>> roleMovePositionResult = await _restGuildApi
                            .ModifyGuildRolePositionsAsync(executionGuild.ID.Value,
                                new (Snowflake RoleID, Optional<int?> Position)[] { (role.ID, maxPos) }).ConfigureAwait(false);
                        if (!roleMovePositionResult.IsSuccess)
                        {
                            _log.LogWarning("Could not move the role because {error}", roleMovePositionResult.Error);
                            errResponse = await _feedbackService
                                .SendContextualErrorAsync(
                                    "Could not move role in list, check the bot's permissions and try again",
                                    ct: this.CancellationToken).ConfigureAwait(false);
                            if (true)
                            {
                                return errResponse.IsSuccess
                                    ? Result.FromSuccess()
                                    : Result.FromError(result: errResponse);
                            }
                        }

                        IRole thisRoleData = roleMovePositionResult.Entity.Single();
                        _log.LogDebug("Role {role_name} moved to position {position}", thisRoleData.Name, thisRoleData.Position);
                    }
                    else
                    {
                        _log.LogWarning("Could not get bot member because {error}", currentBotMemberResult.Error);
                        msg += "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
                    }
                }
                else
                {
                    _log.LogWarning("Could not get bot user because {error}", currentBotUserResult.Error);
                    msg += "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
                }
            }
            else
            {
                _log.LogWarning("Could not move the role because {error}", getRolesResult.Error);
                msg += "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
            }

            msg += $"Made Role {role.Mention()} and assigned to {assign_to_member.Mention()}\n";
            FeedbackMessage message = new(msg.TrimEnd(), Colour: role.Colour);
            reply = await _feedbackService.SendContextualMessageAsync(message: message, ct: this.CancellationToken).ConfigureAwait(false);
            if (true)
            {
                return reply.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(result: reply);
            }
        }

        //TODO: Convert to Result<(MemoryStream?, IImageFormat?)> or something similar
        internal static async Task<Result<(MemoryStream?, IImageFormat?)>> ImageUrlToBase64(string imageUrl, CancellationToken ct = new())
        {
            MemoryStream? iconStream = null;
            //if this isn't the base64, we overwrite it anyway
            string dataUri = imageUrl;
            IImageFormat? imageFormat = null;
            byte[]? imgData;
            if (Base64Regex.IsMatch(input: dataUri))
            {
                return Result<(MemoryStream?, IImageFormat?)>.FromError(new ArgumentInvalidError("Image Url", "This is not a url, give an image URL"));
            }
            
            Result<IReadOnlyList<IMessage>> errResponse;
            try
            {
                imgData = await Program.httpClient
                    .GetByteArrayAsync(requestUri: imageUrl, cancellationToken: ct)
                    .ConfigureAwait(false);
                MemoryStream imageStream = new(imgData);
                imageFormat = await Image.DetectFormatAsync(imageStream, ct);
                
                if (imageFormat is not JpegFormat && imageFormat is not PngFormat && imageFormat is not GifFormat)
                {
                    try
                    {
                        Image? imgToConvert = Image.Load(imgData);
#if FORCE_IMAGES_SQUARE
                        if (imgToConvert.Height != imgToConvert.Width)
                        {
                            //Seemingly not needed, discord pads non-square icons
                            return Result<(MemoryStream?, IImageFormat?)>.FromError(
                                new ArgumentInvalidError("Image Url", "Image is not square"));
                        }
#endif
                        if (imgToConvert is null)
                        {
                            Result<(MemoryStream?, IImageFormat?)> imgConvFailResult = Result<(MemoryStream?, IImageFormat?)>.FromError(new ArgumentInvalidError("Image Url", $"Format {imageFormat.Name} is not allowed please convert to JPG or PNG"));
                            return imgConvFailResult;
                        }

                        iconStream = new MemoryStream();
                        await imgToConvert.SaveAsync(iconStream, new PngEncoder(), ct).ConfigureAwait(false);
                        iconStream.Position = 0;
                    }
                    catch
                    {
                        return Result<(MemoryStream?, IImageFormat?)>.FromError(new ArgumentInvalidError("Image Url", $"Format {imageFormat.Name} is not allowed please convert to JPG or PNG"));
                    }

                }
                else
                {

                    iconStream = new MemoryStream(imgData);
                }

                // dataUri = $"data:{imageFormat.DefaultMimeType};base64,{Convert.ToBase64String(inArray: imgData)}";
                // _log.LogInformation("Image is {dataUri}", dataUri);
            }
            catch (Exception e)
            {
                Program.log.LogWarning(e.ToString());
                return Result<(MemoryStream?, IImageFormat?)>.FromError(new ArgumentInvalidError("Image Url", $"{imageUrl} is an invalid url, make sure that you can load this in a browser and that it is a link directly to an image (i.e. not an image on a website)"));
            }

            const long maxImageSize = 256_000;
            if (iconStream.Length > maxImageSize)
            {
                Program.log.LogDebug("Image too large {length} > {max}{conv}", iconStream.Length, maxImageSize, imageFormat is not JpegFormat && imageFormat is not PngFormat && imageFormat is not GifFormat ? " after conversion, convert to a jpg or png before submitting" : "");
                return Result<(MemoryStream?, IImageFormat?)>.FromError(new ArgumentInvalidError("Image Url", $"{imageUrl} is larger than 256KB, please resize it"));
            }

            return Result<(MemoryStream? iconStream, IImageFormat? imageFormat)>.FromSuccess((iconStream, imageFormat));
        }

        [Command("untrack-role")]
        [Description("Stops the bot from managing this role")]
        public async Task<IResult> UntrackRole([Description("The role to stop tracking")] IRole role, [Description("Whether the role should be deleted")] bool delete_role = true)
        {
            Result<IReadOnlyList<IMessage>> replyResult;
            IGuildMember                    executingMember;
            PartialGuild                    executionGuild;
            Result<IReadOnlyList<IMessage>> errResponse;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executionGuild  = new PartialGuild(interactionContext.Interaction.GuildID);
                    executingMember = interactionContext.Interaction.Member.Value;
                    break;
                case TextCommandContext messageContext:
                {
                    executionGuild = new PartialGuild(messageContext.GuildID);
                    Result<IGuildMember> guildMemberResult = await _restGuildApi.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.Message.Author.Value.ID).ConfigureAwait(false);
                    if (!guildMemberResult.IsSuccess)
                    {
                        _log.LogWarning($"Error responding to message {messageContext.Message.ID} because {guildMemberResult.Error}");
                        errResponse = await _feedbackService.SendContextualErrorAsync("Make sure you are in a server").ConfigureAwait(false);
                        return errResponse.IsSuccess
                            ? Result.FromSuccess()
                            : Result.FromError(result: errResponse);
                    }
                    executingMember = guildMemberResult.Entity;
                    break;
                }
                default:
                    errResponse = await _feedbackService.SendContextualErrorAsync("I don't know how you invoked this command").ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
            }
            if (!executingMember.Permissions.HasValue)
            {
                Result<IGuildMember> getPermsResult = await Program.AddGuildMemberPermissions(executingMember, executionGuild.ID.Value);
                if (!getPermsResult.IsSuccess)
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync("Could not determine User's permission, please evoke via slash command", options: new FeedbackMessageOptions
                    {
                        MessageFlags = MessageFlags.Ephemeral
                    }).ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }

            if (!delete_role && !executingMember.IsRoleModAdminOrOwner())
            {
                return await SendErrorReply("Only mods are allowed to untrack a role without deleting it").ConfigureAwait(false);
            }
            //Run input checks
            //If you are (not the user you're trying to assign to or are not premium) and you are not a mod/owner then deny you
            await using Database.DiscordDbContext database = new();
            Database.RoleData? roleCreated = await database.RolesCreated.Where(rc =>
                rc.ServerId == executionGuild.ID.Value.Value && rc.RoleId == role.ID.Value).FirstOrDefaultAsync();
            if (roleCreated is null)
            {
                replyResult = await _feedbackService.SendContextualErrorAsync(
                    $"Role {role.Mention()} not found in database, make sure it was created using the bot or added using /track-role",
                    ct: this.CancellationToken).ConfigureAwait(false);
                return !replyResult.IsSuccess
                    ? Result.FromError(replyResult)
                    : Result.FromSuccess();
            }

            // if (roleCreated.RoleUserId.IsOwner() && !_context.User.IsOwner())
            // {
            //     _log.LogCritical("{executeUser} tried to remove role {role} for user {roleUser} in server {server}", _context.User.Mention(), role.Mention(), new Snowflake(roleCreated.RoleUserId).User(), executionGuild.ID.Value.Value);
            //     return await SendErrorReply("You really gonna do that?");
            // }
            if (roleCreated.RoleUserId != executingMember.User.Value.ID.Value && !executingMember.IsRoleModAdminOrOwner())
            {
                return await SendErrorReply("You do not have permission to untrack this role, you either you did not create it or do not have it and you don't have the mod permissions to manage roles").ConfigureAwait(false);
            }
            (int result, ulong ownerId) = await Database.RemoveRoleFromDatabase(role).ConfigureAwait(false);
            string replyMessageText = "";
            switch (result)
            {
                case -1:
                {
                    replyMessageText += $"Role {role.Mention()} not found in database, make sure it was created using the bot or added using /track-role";
                    replyResult = await _feedbackService.SendContextualErrorAsync(
                        replyMessageText,
                        ct: this.CancellationToken).ConfigureAwait(false);
                    return !replyResult.IsSuccess
                        ? Result.FromError(replyResult)
                        : Result.FromSuccess();
                }
                case 0:
                {
                    replyMessageText += $"Role {role.Mention()} could not be removed from database";
                    _log.LogWarning(replyMessageText);
                    replyResult = await _feedbackService.SendContextualErrorAsync(
                        $"Role {role.Mention()} could not be removed from database, try again later",
                        ct: this.CancellationToken).ConfigureAwait(false);
                    return !replyResult.IsSuccess
                        ? Result.FromError(replyResult)
                        : Result.FromSuccess();
                }
                case 1:
                {
                    replyMessageText += $"Role {role.Mention()} untracked successfully";
                    replyResult = await _feedbackService.SendContextualSuccessAsync(
                        replyMessageText,
                        ct: this.CancellationToken).ConfigureAwait(false);
                    if (!replyResult.IsSuccess)
                        return Result.FromError(replyResult);
                    if (!delete_role) 
                        return Result.FromSuccess();
                    Result removeRoleResult = await _restGuildApi.RemoveGuildMemberRoleAsync(executionGuild.ID.Value, new Snowflake(ownerId), role.ID, reason: "Removing role to prep for deletion", ct: this.CancellationToken).ConfigureAwait(false);
                    if (!removeRoleResult.IsSuccess)
                    {
                        _log.LogError("Could not remove role {role} : {roleMention} from member {memberId} because {error}", role.Name,
                            role.Mention(), ownerId, removeRoleResult.Error);
                    }
                    Result deleteResult = await _restGuildApi.DeleteGuildRoleAsync(executionGuild.ID.Value, role.ID, reason: $"User requested deletion", ct: this.CancellationToken).ConfigureAwait(false);
                    if (!deleteResult.IsSuccess)
                    {
                        _log.LogError("Could not remove role {role} : {roleMention} because {error}", role.Name,
                            role.Mention(), deleteResult.Error);
                        return await SendErrorReply($"Could not remove role {role.Mention()}, remove it manually").ConfigureAwait(false);
                    }
                    //TODO: Add editing reply message
                    replyMessageText = $"Deleted role {role.Name} : {role.Mention()} from server";
                    // await _restChannelApi.DeleteMessageAsync(_context.ChannelID, replyResult.Entity.First().ID);
                    replyResult =
                        await _feedbackService.SendContextualSuccessAsync(replyMessageText).ConfigureAwait(false);
                    return replyResult.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(replyResult);

                }
                default:
                    _log.LogCritical($"Role {role.Mention()} removed multiple times from the database, somehow, oh no");
                    replyResult = await _feedbackService.SendContextualErrorAsync(
                        $"Role {role.Mention()} removed multiple times from the database, somehow, oh no",
                        ct: this.CancellationToken).ConfigureAwait(false);
                    return !replyResult.IsSuccess
                        ? Result.FromError(replyResult)
                        : Result.FromSuccess();
            }
        }

        [Command("track-role")]
        [Description("Track an existing role with the bot")]
        public async Task<IResult> TrackRole([Description("The role to track")] IRole role,
            [Description("If this role has no members, assign it to this user")] IUser? new_owner = null)
        {
            await using Database.DiscordDbContext database = new();
            if (await database.RolesCreated.Where(rd => rd.RoleId == role.ID.Value).AnyAsync().ConfigureAwait(false))
            {
                return await SendErrorReply("This role is already being tracked").ConfigureAwait(false);
            }
            IGuildMember executorGuildMember;
            PartialGuild                    executionGuild;
            Result<IReadOnlyList<IMessage>> errResponse;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executorGuildMember = interactionContext.Interaction.Member.Value;
                    executionGuild      = new(interactionContext.Interaction.GuildID);
                    break;
                case TextCommandContext messageContext:
                {
                    executionGuild = new(messageContext.GuildID);
                    Result<IGuildMember> guildMemberResult = await _restGuildApi.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.Message.Author.Value.ID).ConfigureAwait(false);
                    if (!guildMemberResult.IsSuccess)
                    {
                        _log.LogWarning($"Error responding to message {messageContext.Message.ID} because {guildMemberResult.Error}");
                        errResponse = await _feedbackService.SendContextualErrorAsync("Make sure you are in a server").ConfigureAwait(false);
                        return errResponse.IsSuccess
                            ? Result.FromSuccess()
                            : Result.FromError(result: errResponse);
                    }
                    executorGuildMember = guildMemberResult.Entity;
                    break;
                }
                default:
                    errResponse = await _feedbackService.SendContextualErrorAsync("I don't know how you invoked this command").ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
            }
            if (!executorGuildMember.Permissions.HasValue)
            {
                Result<IGuildMember> getPermsResult = await Program.AddGuildMemberPermissions(executorGuildMember, executionGuild.ID.Value);
                if (!getPermsResult.IsSuccess)
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync("Could not determine User's permission, please evoke via slash command", options: new FeedbackMessageOptions
                    {
                        MessageFlags = MessageFlags.Ephemeral
                    }).ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }

            //If you are not a mod/owner then deny you
            //Boosters are not allowed this perm due to the off chance 1 boosting member claims a (non-booster) role where they 
            if (!executorGuildMember.IsRoleModAdminOrOwner())
            {
                if (await Database.GetRoleCount(executionGuild.ID.Value.Value, executorGuildMember.User.Value.ID.Value).ConfigureAwait(false) > 0 && !executorGuildMember.IsRoleModAdminOrOwner())
                {
                    return await SendErrorReply("You are only allowed one booster role on this server").ConfigureAwait(false);
                }
                errResponse = await _feedbackService.SendContextualErrorAsync(
                    $"You do not have the required permissions to create this role for {executorGuildMember.Mention()}, you don't have ManageRoles mod permissions").ConfigureAwait(false);
                return !errResponse.IsSuccess
                    ? Result.FromError(result: errResponse)
                    : Result.FromSuccess();
            }

            //Check role meets criteria to be added
            List<IGuildMember> membersList = new();
            Result<List <IGuildMember>> membersListResult;
            Optional<Snowflake> lastGuildMemberSnowflake = default;
            while (true)
            {
                Result<IReadOnlyList<IGuildMember>> getMembersResult =
                    await _restGuildApi.ListGuildMembersAsync(executionGuild.ID.Value, limit: 1000, after: lastGuildMemberSnowflake).ConfigureAwait(false);
                if (!getMembersResult.IsSuccess)
                {
                    return await SendErrorReply(
                        "Could not find how many have this role, invalid. Try again or recreate this role using the bot if necessary.").ConfigureAwait(false);
                }
                
                if (getMembersResult.Entity.Any())
                {
                    membersList.AddRange(getMembersResult.Entity.Where(gm => gm.Roles.Contains(role.ID)));
                    lastGuildMemberSnowflake = new Optional<Snowflake>(getMembersResult.Entity.Last().User.Value.ID);
                }
                else
                {
                    break;
                }
            }

            Snowflake ownerMemberSnowflake = new(0);
            switch (membersList.Count)
            {
                //TODO: handle when new_owner specified but different member found with role
                case > 1:
                    return await SendErrorReply("More than 1 member has this role, cannot add to the bot").ConfigureAwait(false);
                case 1:
                    ownerMemberSnowflake = membersList.First().User.Value.ID;
                    break;
                case 0:
                    if (new_owner is null)
                    {
                        return await SendErrorReply("No user has this role; Add a user or specify one in this command before tracking it").ConfigureAwait(false);
                    }

                    ownerMemberSnowflake = new_owner.ID;
                    Result addRoleResult = await _restGuildApi.AddGuildMemberRoleAsync(executionGuild.ID.Value,
                        ownerMemberSnowflake, role.ID,
                        reason: $"User added role when starting tracking").ConfigureAwait(false);
                    if (!addRoleResult.IsSuccess)
                    {
                        return await SendErrorReply($"No user has this role and the bot failed to add {new_owner.Mention()}").ConfigureAwait(false);
                    }
                    break;
            }
            
            Database.RoleData roleData = new()
            {
                Color = ColorTranslator.ToHtml(role.Colour),
                Name = role.Name,
                RoleId = role.ID.Value,
                ServerId = executionGuild.ID.Value.Value,
                RoleUserId = ownerMemberSnowflake.Value
            };
            database.RolesCreated.Add(roleData);
            await database.SaveChangesAsync().ConfigureAwait(false);
            return await SendSuccessReply($"Successfully started tracking {role.Mention()}, you can now modify it via bot commands").ConfigureAwait(false);
        }

        private const int DeleteOwnerMessageDelay = 1000;

        [Command("modify-role")]
        [Description("Modify a role's properties")]
        public async Task<IResult> ModifyRole([Description("The role to change")] IRole role,
            [Description("The new name to give it")]string? new_name = null,
            [Description("Color in #XxXxXx format, leave blank or #000000 to keep current color")] string? new_color_string = null,
            [Description("The image url to use for the icon")] string? new_image = null
            )
        {
            if (new_name == null && new_color_string == null && new_image == null)
            {
                return await SendErrorReply("You must specify at least one property of the role to change").ConfigureAwait(false);
            }
            //Check Server Member has permissions to use command
            IGuildMember                    executingMember;
            PartialGuild                    executionGuild;
            Result<IReadOnlyList<IMessage>> errResponse;
            Result                          deleteResponse;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executionGuild  = new(interactionContext.Interaction.GuildID);
                    executingMember = interactionContext.Interaction.Member.Value;
                    break;
                case TextCommandContext messageContext:
                    executionGuild = new PartialGuild(messageContext.GuildID);
                    Result<IGuildMember> guildMemberResult = await _restGuildApi.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.Message.Author.Value.ID).ConfigureAwait(false);
                    if (!guildMemberResult.IsSuccess)
                    {
                        _log.LogWarning($"Error responding to message {messageContext.Message.Author.Value} because {guildMemberResult.Error}");
                        errResponse = await _feedbackService.SendContextualErrorAsync("Make sure you are in a server").ConfigureAwait(false);
                        if (true)
                        {
                            return errResponse.IsSuccess
                                ? Result.FromSuccess()
                                : Result.FromError(result: errResponse);
                        }
                    }

                    executingMember = guildMemberResult.Entity;
                    break;
                default:
                    errResponse = await _feedbackService.SendContextualErrorAsync("I don't know how you invoked this command").ConfigureAwait(false);
                    if (true)
                    {
                        return errResponse.IsSuccess
                            ? Result.FromSuccess()
                            : Result.FromError(result: errResponse);
                    }
            }
            if (!executingMember.Permissions.HasValue)
            {
                Result<IGuildMember> getPermsResult = await Program.AddGuildMemberPermissions(executingMember, executionGuild.ID.Value);
                if (!getPermsResult.IsSuccess)
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync("Could not determine User's permission, please evoke via slash command", options: new FeedbackMessageOptions
                    {
                        MessageFlags = MessageFlags.Ephemeral
                    }).ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }
            await using Database.DiscordDbContext database = new();
            Database.RoleData? roleData = await database.RolesCreated
                .Where(rd => executionGuild.ID.Value.Value == rd.ServerId && role.ID.Value == rd.RoleId).FirstOrDefaultAsync().ConfigureAwait(false);
            if (roleData == null)
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("Role not found in database, check that this command is tracking it").ConfigureAwait(false); 
                if (true)
                {
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }
            
            // If they don't have ManageRoles perm and if they either did not create the role or do not have the role, deny access
            if (roleData.RoleUserId != executingMember.User.Value.ID.Value && !executingMember.IsRoleModAdminOrOwner())
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("You do not have permission to modify this role, you either you did not create it or do not have it and you don't have the mod permissions to manage roles").ConfigureAwait(false);
                if (true)
                {
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }

            //Handle Name Change
            if (new_name is not null)
            {
                roleData.Name = new_name;
            }
            //Handle Color Change
            Color? newRoleColor = null;
            if (new_color_string is not null)
            {
                try
                {
                    newRoleColor = GetColorFromString(new_color_string);
                    roleData.Color = ColorTranslator.ToHtml(newRoleColor.Value);
                }
                catch (ArgumentException e)
                {
                    _log.LogWarning("Color not found {color} because {e}", new_color_string, e);
                    errResponse = await _feedbackService.SendContextualErrorAsync($"Invalid color {new_color_string}, must be in the format #XxXxXx or a common color name, check your spelling").ConfigureAwait(false);
                    if (true)
                    {
                        return !errResponse.IsSuccess
                            ? Result.FromError(result: errResponse)
                            : Result.FromSuccess();
                    }
                }
            }
            //Handle Image Change
            IImageFormat? newIconFormat = null;
            Result<IRole> modifyRoleResult;
            if (new_image is null)
            {
                modifyRoleResult = await _restGuildApi.ModifyGuildRoleAsync(executionGuild.ID.Value, role.ID,
                    new_name ?? default(Optional<string?>),
                    color: newRoleColor ?? default(Optional<Color?>),
                    reason: $"Member requested to modify role").ConfigureAwait(false);
                roleData.ImageHash = null;
            }
            else if (IsUnicodeEmoji(new_image))
            {
                modifyRoleResult = await _restGuildApi.ModifyGuildRoleAsync(executionGuild.ID.Value, role.ID,
                    new_name ?? default(Optional<string?>),
                    color: newRoleColor ?? default(Optional<Color?>),
                    unicodeEmoji: new_image,
                    reason: $"Member requested to modify role").ConfigureAwait(false);
                roleData.ImageHash = null;
            } else
            {
                //Get guild info to tell premium tier
                Result<IGuild> getGuildResult = await _restGuildApi.GetGuildAsync(executionGuild.ID.Value).ConfigureAwait(false);
                if (!getGuildResult.IsSuccess)
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync("Could not get info about the guild you are in, try again later").ConfigureAwait(false);
                    if (true)
                    {
                        return errResponse.IsSuccess
                            ? Result.FromSuccess()
                            : Result.FromError(result: errResponse);
                    }
                }

                if (getGuildResult.Entity.PremiumTier < PremiumTier.Tier2)
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync($"Server must be boosted at least to tier 2 before adding role icons.\n{7 - getGuildResult.Entity.PremiumSubscriptionCount.Value} more boost needed").ConfigureAwait(false);
                    if (true)
                    {
                        return errResponse.IsSuccess
                            ? Result.FromSuccess()
                            : Result.FromError(result: errResponse);
                    }
                }
                if (AddReactionsToMediaArchiveMessageResponder.EmoteWithRequiredIdRegex.IsMatch(new_image))
                {
                    new_image = EmoteToDiscordUrl(new_image);
                } else if (AddReactionsToMediaArchiveMessageResponder.EmoteWithoutRequiredIdRegex.IsMatch(new_image))
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync("Please Choose Emoji from selction menu, simply typing the emoji make getting the image impossible");
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(errResponse);
                }

                MemoryStream? newIconStream = null;
                Result<(MemoryStream?, IImageFormat?)> imageToStreamResult = await ImageUrlToBase64(imageUrl: new_image).ConfigureAwait(false);
                if (!imageToStreamResult.IsSuccess)
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync(imageToStreamResult.Error.Message);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(errResponse);
                }

                (newIconStream, newIconFormat) = imageToStreamResult.Entity;
                roleData.ImageUrl = new_image;
                modifyRoleResult = await _restGuildApi.ModifyGuildRoleAsync(executionGuild.ID.Value, role.ID,
                    new_name ?? default(Optional<string?>),
                    color: newRoleColor ?? default(Optional<Color?>),
                    icon: newIconStream ?? default(Optional<Stream?>),
                    reason: $"Member requested to modify role").ConfigureAwait(false);

                roleData.ImageHash = modifyRoleResult.IsSuccess ? modifyRoleResult.Entity.Icon.Value?.Value : null;

            }
            
            Result<IReadOnlyList<IMessage>> replyResult;
            if (!modifyRoleResult.IsSuccess)
            {
                //TODO: Handle image failure on servers without boosts
                _log.LogWarning($"Could not modify {role.Mention()} because {modifyRoleResult.Error}");
                if (modifyRoleResult.Error.Message == "Unknown or unsupported image format.")
                {
                    return await SendErrorReply(
                        $"Format {newIconFormat?.Name} rejected by discord please convert to JPG or PNG").ConfigureAwait(false);
                }
                errResponse = await _feedbackService.SendContextualErrorAsync(
                    $"Could not modify {role.Mention()} check that the bot has the correct permissions").ConfigureAwait(false);
                if (true)
                {
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }
            //If we changed db items and didn't save to the db
            if ((new_name is not null || new_color_string is not null) && await database.SaveChangesAsync().ConfigureAwait(false) < 1)
            {
                //TODO: Change role back or queue a later write to the database
                errResponse = await _feedbackService.SendContextualErrorAsync(
                    $"Could not modify database for {modifyRoleResult.Entity.Mention()}, try again later").ConfigureAwait(false);
                if (true)
                {
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
            }
            replyResult = await _feedbackService.SendContextualSuccessAsync(
                $"Successfully modified role {modifyRoleResult.Entity.Mention()}").ConfigureAwait(false);
            if (true)
            {
                return replyResult.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(replyResult);
            }
        }

        public static bool IsUnicodeEmoji(string newImage)
        {
            return newImage.Length == 1;
        }

        private async Task<IResult> SendSuccessReply(string successMessage)
        {
            Result<IReadOnlyList<IMessage>> replyResponse = await _feedbackService
                .SendContextualSuccessAsync(successMessage).ConfigureAwait(false);
            return replyResponse.IsSuccess
                ? Result.FromSuccess()
                : Result.FromError(result: replyResponse);
        }
        private async Task<IResult> SendErrorReply(string errorMessage)
        {
            Result<IReadOnlyList<IMessage>> errResponse = await _feedbackService
                .SendContextualErrorAsync(errorMessage).ConfigureAwait(false);
            return errResponse.IsSuccess
                ? Result.FromSuccess()
                : Result.FromError(result: errResponse);
        }
        private static IEnumerable<Type> GetAllNestedTypes(Type t)
        {
            foreach (Type nestedType in t.GetNestedTypes())
            {
                yield return nestedType;
                foreach (Type recursiveNested in GetAllNestedTypes(nestedType))
                {
                    yield return recursiveNested;
                }
            }
        }
        public static string SpacingSequence(int indentLevel) => '\u02ea' + string.Concat(Enumerable.Repeat('\u02cd', indentLevel));

        [Command("help")]
        [Description("Print the help message for Boost Role Manager")]
        public async Task<IResult> Help()
        {
            List<Type> slashCommandGroups = new() { GetType(), typeof(AddReactionsToMediaArchiveCommands) };
            slashCommandGroups.AddRange(GetAllNestedTypes(GetType()));
            slashCommandGroups.AddRange(GetAllNestedTypes(typeof(AddReactionsToMediaArchiveCommands)));
            List<string> helpStrings = new(){""};
            foreach (Type slashCommandGroup in slashCommandGroups)
            {
                string prefix = "";
                IEnumerable<CommandAttribute> slashCommandGroupAttributes = slashCommandGroup.GetCustomAttributes().OfType<CommandAttribute>();
                CommandAttribute[] commandGroupAttributes = slashCommandGroupAttributes as CommandAttribute[] ?? slashCommandGroupAttributes.ToArray();
                if (commandGroupAttributes.Any())
                {
                    prefix += commandGroupAttributes.First().Name;
                }

                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    prefix += " ";
                }
                MethodInfo[] methodInfos = slashCommandGroup.GetMethods();
                foreach (MethodInfo methodInfo in methodInfos)
                {
                    string commandHelp = "";
                    CommandAttribute? slashCommandName = methodInfo.GetCustomAttribute<CommandAttribute>();
                    if (slashCommandName is null)
                    {
                        continue;
                    }
                    DescriptionAttribute? slashCommandDescription = methodInfo.GetCustomAttribute<DescriptionAttribute>();
                    commandHelp += $"`/{prefix}{slashCommandName.Name}`\n";
                    if (slashCommandDescription != null)
                    {
                        commandHelp += $"{SpacingSequence(1)}{slashCommandDescription.Description}\n";
                    }

                    ParameterInfo[] paramsInfo = methodInfo.GetParameters();
                    foreach (ParameterInfo paramInfo in paramsInfo)
                    {
                        string? paramName = paramInfo.Name;
                        DescriptionAttribute? paramDesc = paramInfo.GetCustomAttribute<DescriptionAttribute>();
                        commandHelp += $"{SpacingSequence(2)}`{paramName}{(paramDesc != null ? $":` {paramDesc.Description}" : '`')}\n";
                    }
                    
                    if (helpStrings[^1].Length + commandHelp.Length > 4096)
                    {
                        helpStrings.Add(commandHelp);
                    }
                    else
                    {
                        helpStrings[^1] += commandHelp;
                    }
                }
            }

            helpStrings[0] = "**Commands:**\n" + helpStrings[0];
            IEnumerable<EmbedBuilder> embeds = helpStrings.Select(helpString => new EmbedBuilder()
                .WithTitle("Help using Discord Boost Role Manager <:miihinotes:913303041057390644>")
                .WithDescription(helpString)
                .WithFooter($"GitHub: {GithubLink} | Donate: {DonateLinks[0]}"));
            foreach (EmbedBuilder embedBuilder in embeds)
            {
                Result embedHelpResult = embedBuilder.Validate();
                if (!embedHelpResult.IsSuccess)
                {
                    _log.LogError("Failed to validate embed {error}", embedHelpResult.Error.Message);
                    Result<IReadOnlyList<IMessage>> replyEmbedErrorResult = await _feedbackService.SendContextualContentAsync(
                        $"***{embedBuilder.Title}***\n{string.Concat('\n', embedBuilder.Fields.Select(field => field.Name + '\n' + field.Value))}{embedBuilder.Footer}", Color.RebeccaPurple, ct: this.CancellationToken).ConfigureAwait(false);
                }
                Result<Embed> embedHelpBuildResult = embedBuilder.Build();
                if (!embedHelpBuildResult.IsSuccess)
                {
                    _log.LogError("Failed to build embed {error}", embedHelpBuildResult.Error.Message);
                    Result<IReadOnlyList<IMessage>> replyEmbedErrorResult = await _feedbackService.SendContextualSuccessAsync($"***{embedBuilder.Title}***\n{string.Concat('\n', embedBuilder.Fields.Select(field => field.Name + '\n' + field.Value))}{embedBuilder.Footer}", ct: this.CancellationToken).ConfigureAwait(false);
                }

                Result<IMessage> replyResult = await _feedbackService.SendContextualEmbedAsync(embedHelpBuildResult.Entity).ConfigureAwait(false);
                if (replyResult.IsSuccess)
                {
                    continue;
                }

                _log.LogError("Error making help embed {error}", replyResult.Error.Message);
                Result<IReadOnlyList<IMessage>> replyEmbedErrorFinalResult = await _feedbackService.SendContextualInfoAsync($"***{embedBuilder.Title}***\n{string.Concat('\n', embedBuilder.Fields.Select(field => field.Name + '\n' + field.Value))}{embedBuilder.Footer}").ConfigureAwait(false);

            }

            return Result.FromSuccess();
        }

        private const string GithubLink = "https://github.com/b-rad15/DiscordBoostRoleBot";
        [Command("github")]
        [Description("Get the link to the Github Repo for Discord Music Recs")]
        public async Task<IResult> GetGithubLink()
        {
            return await _feedbackService.SendContextualSuccessAsync($"View the source code on [Github]({GithubLink})").ConfigureAwait(false);
        }

        private static readonly string[] DonateLinks = { "https://ko-fi.com/bradocon" };
        [Command("donate")]
        [Description("Prints all links to donate to the bot owner")]
        public async Task<IResult> GetDonateLinks()
        {
            return await _feedbackService.SendContextualSuccessAsync($"Support the dev on ko-fi {DonateLinks[0]}").ConfigureAwait(false);
        }

    }

    public class EmptyCommands : CommandGroup
    {

    }
}
