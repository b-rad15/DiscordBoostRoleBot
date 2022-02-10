﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remora.Commands.Attributes;
using Remora.Commands.Groups;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.Commands.Attributes;
using Remora.Discord.Commands.Contexts;
using Remora.Discord.Commands.Feedback.Messages;
using Remora.Discord.Commands.Feedback.Services;
using Remora.Discord.Extensions.Builder;
using Remora.Discord.Extensions.Embeds;
using Remora.Discord.API.Extensions;
using Remora.Discord.Commands.Conditions;
using Remora.Discord.Hosting.Services;
using Remora.Discord.Rest.API;
using Remora.Rest.Core;
using Remora.Results;
using SQLitePCL;
using System.Drawing.Imaging;
using System.Net.Mime;
using System.Net.Security;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.Extensions.Caching.Memory;
using Remora.Discord.API.Objects;
using Remora.Rest.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using Color = System.Drawing.Color;

namespace DiscordBoostRoleBot
{
    public class RoleCommands : CommandGroup
    {
        private readonly FeedbackService _feedbackService;
        private readonly ICommandContext _context;
        private readonly IDiscordRestGuildAPI _restGuildAPI;
        private readonly IDiscordRestUserAPI _restUserAPI;
        private readonly ILogger<Program> _log; 

        /// <summary>
        /// Initializes a new instance of the <see cref="RoleCommands"/> class.
        /// </summary>
        /// <param name="feedbackService">The feedback service.</param>
        /// <param name="context">The command context.</param>
        /// <param name="restGuildApi">The DiscordRestGuildAPI to allow guild api access.</param>
        public RoleCommands(FeedbackService feedbackService, ICommandContext context, IDiscordRestGuildAPI restGuildApi, ILogger<Program> log, IDiscordRestUserAPI restUserApi)
        {
            _feedbackService = feedbackService;
            _context = context;
            _restGuildAPI = restGuildApi;
            _log = log;
            _restUserAPI = restUserApi;
        }

        public static Color GetColorFromString(string colorString) => ColorTranslator.FromHtml(colorString);

        public static readonly Regex Base64Regex = new(@"^([A-Za-z0-9+/]{4})*([A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{2}==)?$");

        [RequireContext(ChannelContext.Guild)]
        // [RequireDiscordPermission(DiscordPermission.ManageRoles | DiscordPermission.Administrator)]
        [Command("make-role")]
        [CommandType(type: ApplicationCommandType.ChatInput)]
        [Description("Make a new role, attach an image to add it to the role")]
        public async Task<IResult> MakeNewRole([Description("RoleName")] string roleName,
            [Description("Color in #XxXxXx format or common name, leave blank or #000000 to keep current color")] string color,
            [Description("The User to assign the role to")] IGuildMember? assignToMember = null,
            [Description("The image url to use for the icon")] string? imageUrl = null
            )
        {
            IGuildMember executorGuildMember;
            Result<IReadOnlyList<IMessage>> errResponse;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executorGuildMember = interactionContext.Member.Value;
                    if (interactionContext.Message.HasValue)
                    {
                        IReadOnlyList<IAttachment> attachments = interactionContext.Message.Value.Attachments;
                        if (attachments[0].ContentType.HasValue && attachments[0].ContentType.Value is "image/jpeg" or "image/png" or "image/gif")
                        {
                            imageUrl = attachments[0].Url;
                        }
                    }
                    // Result<IReadOnlyList<IMessage>> errResponse = await _feedbackService.SendContextualErrorAsync("This can only be executed via slash command");
                    // return errResponse.IsSuccess
                    //     ? Result.FromSuccess()
                    //     : Result.FromError(errResponse);
                    break;
                case MessageContext messageContext:
                {
                    Result<IGuildMember> guildMemberResult = await _restGuildAPI.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.User.ID).ConfigureAwait(false);
                    if (!guildMemberResult.IsSuccess)
                    {
                        _log.LogWarning($"Error responding to message {messageContext.MessageID} because {guildMemberResult.Error}");
                        errResponse = await _feedbackService.SendContextualErrorAsync("Make sure you are in a server").ConfigureAwait(false);
                        return errResponse.IsSuccess
                            ? Result.FromSuccess()
                            : Result.FromError(result: errResponse);
                    }
                    executorGuildMember = guildMemberResult.Entity;
                    if (messageContext.Message.Attachments.HasValue && messageContext.Message.Attachments.Value.Count > 0)
                    {
                        IReadOnlyList<IAttachment> attachments = messageContext.Message.Attachments.Value;
                        if (attachments[0].ContentType == "image/jpeg" || attachments[0].ContentType == "image/png" ||
                            attachments[0].ContentType == "image/gif")
                        {
                            imageUrl = attachments[0].Url;
                        }
                    }

                    break;
                }
                default:
                    errResponse = await _feedbackService.SendContextualErrorAsync("I don't know how you invoked this command").ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
            }

            assignToMember ??= executorGuildMember;
            //Run input checks
            //If you are (not the user you're trying to assign to or are not premium) and you are not a mod/owner then deny you
            if ((_context.User.ID != assignToMember.User.Value.ID || assignToMember.IsNotBoosting()) && !executorGuildMember.IsModAdminOrOwner())
            {
                if (await Database.GetRoleCount(_context.GuildID.Value.Value, assignToMember.User.Value.ID.Value) > 0)
                {
                    return await SendErrorReply("You are only allowed one booster role on this server");
                }
                errResponse = await _feedbackService.SendContextualErrorAsync(
                    $"You do not have the required permissions to create this role for {assignToMember.Mention()}, not boosting and don't have ManageRoles mod permissions").ConfigureAwait(false);
                return !errResponse.IsSuccess
                    ? Result.FromError(result: errResponse)
                    : Result.FromSuccess();
            }

            //Declare necessary variables
            Result<IReadOnlyList<IMessage>> reply;
            //Check arguments and initialize variables
            //Prepare Color
            Color roleColor;
            try
            {
                roleColor = GetColorFromString(color);
            }
            catch (ArgumentException e)
            {
                _log.LogWarning("Color not found {color} because {e}", color, e);
                reply = await _feedbackService.SendContextualErrorAsync($"Invalid color {color}, must be in the format #XxXxXx or a common color name, check your spelling").ConfigureAwait(false);
                return !reply.IsSuccess
                    ? Result.FromError(result: reply)
                    : Result.FromSuccess();
            }
            if (!_context.GuildID.HasValue)
            {
                reply = await _feedbackService.SendContextualErrorAsync("You are not sending this command in a guild, somehow your permissions are broken", ct: this.CancellationToken).ConfigureAwait(false);
                return !reply.IsSuccess
                    ? Result.FromError(result: reply)
                    : Result.FromSuccess();
            }
            //Prepare server
            Snowflake requestServer = _context.GuildID.Value;
            //Prepare Image
            MemoryStream? iconStream = null;
            IImageFormat? iconFormat = null;
            if (imageUrl is not null)
            {
                IResult? makeNewRole;
                (iconStream, iconFormat, makeNewRole) = await ImageUrlToBase64(imageUrl: imageUrl);
                if (iconStream is null)
                    return makeNewRole;
            }

            Result<IRole> roleResult = await _restGuildAPI.CreateGuildRoleAsync(guildID: requestServer, name: roleName, colour: roleColor, icon: iconStream ?? default(Optional<Stream>),
                isHoisted: false, isMentionable: true, ct: this.CancellationToken).ConfigureAwait(false);
            if (!roleResult.IsSuccess)
            {
                _log.LogError($"Could not assign role to {assignToMember.User.Value.Mention()} because {roleResult.Error}");
                reply = await _feedbackService.SendContextualErrorAsync(
                    $"Could not assign role to {assignToMember.User.Value.Mention()}, make sure the bot's permissions are set correctly. Error = {roleResult.Error.Message}", ct: this.CancellationToken).ConfigureAwait(false);
                return roleResult;
            }
            IRole role = roleResult.Entity;
            bool addedToDb = await Database.AddRoleToDatabase(_context.GuildID.Value.Value, assignToMember.User.Value.ID.Value,
                role.ID.Value, color, role.Name);
            if (!addedToDb)
            {
                _log.LogError($"Could not add role to database");
                reply = await _feedbackService.SendContextualErrorAsync(
                    "Failed to track role, try again later", ct: this.CancellationToken).ConfigureAwait(false);
                return reply.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(reply);
            }
            Result roleApplyResult = await _restGuildAPI.AddGuildMemberRoleAsync(guildID: _context.GuildID.Value,
                userID: assignToMember.User.Value.ID, roleID: role.ID,
                "User is boosting, role request via BoostRoleManager bot", ct: this.CancellationToken).ConfigureAwait(false);
            if (!roleApplyResult.IsSuccess)
            {
                _log.LogError($"Could not make role because {roleApplyResult.Error}");
                reply = await _feedbackService.SendContextualErrorAsync(
                    "Could not make role, make sure the bot's permissions are set correctly", ct: this.CancellationToken).ConfigureAwait(false);
                return roleApplyResult;

            }

            string msg = "";
            var getRolesResult = await _restGuildAPI.GetGuildRolesAsync(_context.GuildID.Value, ct: this.CancellationToken);
            if (getRolesResult.IsSuccess)
            {
                var guildRoles = getRolesResult.Entity;
                var currentBotUserResult =
                    await _restUserAPI.GetCurrentUserAsync(ct: this.CancellationToken);
                if (currentBotUserResult.IsSuccess)
                {
                    var currentBotUser = currentBotUserResult.Entity;
                    var currentBotMemberResult =
                        await _restGuildAPI.GetGuildMemberAsync(_context.GuildID.Value, currentBotUser.ID,
                            this.CancellationToken);
                    if (currentBotMemberResult.IsSuccess)
                    {
                        var currentBotMember = currentBotMemberResult.Entity;
                        var botRoles = guildRoles.Where(gr => currentBotMember.Roles.Contains(gr.ID));
                        var maxPosRole = botRoles.MaxBy(br => br.Position);
                        _log.LogDebug("Bot's highest role is {roleName}: {roleId}", maxPosRole.Name, maxPosRole.ID);
                        int maxPos = maxPosRole.Position;
                        Result<IReadOnlyList<IRole>> roleMovePositionResult = await _restGuildAPI
                            .ModifyGuildRolePositionsAsync(_context.GuildID.Value,
                                new (Snowflake RoleID, Optional<int?> Position)[] { (role.ID, maxPos) }).ConfigureAwait(false);
                        if (!roleMovePositionResult.IsSuccess)
                        {
                            _log.LogWarning($"Could not move the role because {roleMovePositionResult.Error}");
                            reply = await _feedbackService
                                .SendContextualErrorAsync(
                                    "Could not move role in list, check the bot's permissions and try again",
                                    ct: this.CancellationToken).ConfigureAwait(false);
                            return roleMovePositionResult;
                        }

                        _log.LogDebug(roleMovePositionResult.ToString()); 
                    }
                    else
                    {
                        _log.LogWarning($"Could not get bot member because {currentBotMemberResult.Error}");
                        msg += "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
                    }
                }
                else
                {
                    _log.LogWarning($"Could not get bot user because {currentBotUserResult.Error}");
                    msg += "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
                }
            }
            else
            {
                _log.LogWarning($"Could not move the role because {getRolesResult.Error}");
                msg += "Could not move role in list, check the bot's permissions (and role position) and try again or move the role manually\n";
            }

            msg += $"Made Role {role.Mention()} and assigned to {assignToMember.Mention()}\n";
            FeedbackMessage message = new(msg.TrimEnd(), Colour: role.Colour);
            reply = await _feedbackService.SendContextualMessageAsync(message: message, ct: this.CancellationToken).ConfigureAwait(false);
            return !reply.IsSuccess
                ? Result.FromError(result: reply)
                : Result.FromSuccess();
        }

        private async Task<(MemoryStream? iconStream, IImageFormat? imageFormat, IResult? makeNewRole)> ImageUrlToBase64(string imageUrl)
        {
            MemoryStream? iconStream = null;
            //if this isn't the base64, we overwrite it anyway
            string dataUri = imageUrl;
            IImageFormat? imageFormat = null;
            byte[]? imgData;
            if (Base64Regex.IsMatch(input: dataUri))
            {
                return (null, null, await SendErrorReply("This is not a url, give an image URL"));
            }

            Result<IReadOnlyList<IMessage>> errResponse;
            try
            {
                imgData = await Program.httpClient
                    .GetByteArrayAsync(requestUri: imageUrl, cancellationToken: this.CancellationToken)
                    .ConfigureAwait(false);
                imageFormat = Image.DetectFormat(data: imgData);
                if (imageFormat is not JpegFormat && imageFormat is not PngFormat && imageFormat is not GifFormat)
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync(
                            $"format {imageFormat.Name} is not allowed please convert to JPG or PNG")
                        .ConfigureAwait(false);
                    return (iconStream, imageFormat, !errResponse.IsSuccess
                        ? Result.FromError<IReadOnlyList<IMessage>>(result: errResponse)
                        : Result.FromSuccess());
                }

                dataUri = $"data:{imageFormat.DefaultMimeType};base64,{Convert.ToBase64String(inArray: imgData)}";
                _log.LogInformation("Image is {dataUri}", dataUri);
            }
            catch (Exception e)
            {
                _log.LogWarning(e.ToString());
                errResponse = await _feedbackService.SendContextualErrorAsync(
                        $"{imageUrl} is an invalid url, make sure that you can load this in a browser and that it is a link directly to an image (i.e. not an image on a website)")
                    .ConfigureAwait(false);
                return (iconStream, imageFormat, !errResponse.IsSuccess
                    ? Result.FromError<IReadOnlyList<IMessage>>(result: errResponse)
                    : Result.FromSuccess());
            }

            // _log.LogInformation("Data Stream : {stream}", new MemoryStream(Encoding.UTF8.GetBytes(dataUri)).);
            // iconStream = new MemoryStream(Encoding.UTF8.GetBytes(s: dataUri));
            // BitConverter.GetBytes(9894494448401390090).CopyTo(imgData, 0);
            await File.WriteAllBytesAsync("TestFile" + imageFormat.FileExtensions.First(), imgData);
            iconStream = new MemoryStream(imgData);

            return (iconStream, imageFormat, null);
        }

        [Command("untrack-role")]
        [Description("Stops the bot from managing this role")]
        public async Task<IResult> UntrackRole([Description("The role to stop tracking")] IRole role, [Description("Whether the role should be deleted")] bool deleteRole = true)
        {
            IGuildMember member;
            Result<IReadOnlyList<IMessage>> errResponse;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    member = interactionContext.Member.Value;
                    break;
                case MessageContext messageContext:
                {
                    Result<IGuildMember> guildMemberResult = await _restGuildAPI.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.User.ID).ConfigureAwait(false);
                    if (!guildMemberResult.IsSuccess)
                    {
                        _log.LogWarning($"Error responding to message {messageContext.MessageID} because {guildMemberResult.Error}");
                        errResponse = await _feedbackService.SendContextualErrorAsync("Make sure you are in a server").ConfigureAwait(false);
                        return errResponse.IsSuccess
                            ? Result.FromSuccess()
                            : Result.FromError(result: errResponse);
                    }
                    member = guildMemberResult.Entity;
                    break;
                }
                default:
                    errResponse = await _feedbackService.SendContextualErrorAsync("I don't know how you invoked this command").ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
            }

            if (!deleteRole && !member.IsModAdminOrOwner())
            {
                return await SendErrorReply("Only mods are allowed to untrack a role without deleting it");
            }
            //Run input checks
            //If you are (not the user you're trying to assign to or are not premium) and you are not a mod/owner then deny you
            if (_context.User.ID != member.User.Value.ID && !member.IsModAdminOrOwner())
            {
                return await SendErrorReply("You do not have permission to untrack this role, you either you did not create it or do not have it and you don't have the mod permissions to manage roles");
            }
            (int result, ulong ownerId) = await Database.RemoveRoleFromDatabase(role);
            Result<IReadOnlyList<IMessage>> replyResult;
            switch (result)
            {
                case -1:
                {
                    replyResult = await _feedbackService.SendContextualErrorAsync(
                        $"Role {role.Mention()} not found in database, make sure it was created using the bot or added using /track-role",
                        ct: this.CancellationToken);
                    return !replyResult.IsSuccess
                        ? Result.FromError(replyResult)
                        : Result.FromSuccess();
                }
                case 0:
                {
                    _log.LogWarning($"Role {role.Mention()} could not be removed from database");
                    replyResult = await _feedbackService.SendContextualErrorAsync(
                        $"Role {role.Mention()} could not be removed from database, try again later",
                        ct: this.CancellationToken);
                    return !replyResult.IsSuccess
                        ? Result.FromError(replyResult)
                        : Result.FromSuccess();
                }
                case 1:
                {
                    replyResult = await _feedbackService.SendContextualSuccessAsync(
                        $"Role {role.Mention()} untracked successfully",
                        ct: this.CancellationToken);
                    if (!replyResult.IsSuccess)
                        return Result.FromError(replyResult);
                    if (!deleteRole) 
                        return Result.FromSuccess();
                    Result removeRoleResult = await _restGuildAPI.RemoveGuildMemberRoleAsync(_context.GuildID.Value, new Snowflake(ownerId), role.ID, reason: "Removing role to prep for deletion", ct: this.CancellationToken);
                    if (!removeRoleResult.IsSuccess)
                    {
                        _log.LogError("Could not remove role {role} : {roleMention} from member {memberId} because {error}", role.Name,
                            role.Mention(), ownerId, removeRoleResult.Error);
                    }
                    Result deleteResult = await _restGuildAPI.DeleteGuildRoleAsync(_context.GuildID.Value, role.ID, reason: $"User requested deletion", ct: this.CancellationToken);
                    if (!deleteResult.IsSuccess)
                    {
                        _log.LogError("Could not remove role {role} : {roleMention} because {error}", role.Name,
                            role.Mention(), deleteResult.Error);
                        return await SendErrorReply($"Could not remove role {role.Mention()}, remove it manually");
                    }

                    replyResult =
                        await _feedbackService.SendContextualSuccessAsync(
                            $"Deleted role {role.Name} : {role.Mention()} from server");
                    return replyResult.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(replyResult);

                }
                default:
                    _log.LogCritical($"Role {role.Mention()} removed multiple times from the database, somehow, oh no");
                    replyResult = await _feedbackService.SendContextualErrorAsync(
                        $"Role {role.Mention()} removed multiple times from the database, somehow, oh no",
                        ct: this.CancellationToken);
                    return !replyResult.IsSuccess
                        ? Result.FromError(replyResult)
                        : Result.FromSuccess();
            }
        }

        [Command("track-role")]
        [Description("Track an existing role with the bot")]
        public async Task<IResult> TrackRole([Description("The role to track")] IRole role,
            [Description("If this role has no members, assign it to this user")] IUser? newOwner = null)
        {
            Database.RoleDataDbContext database = new();
            if (await database.RolesCreated.Where(rd => rd.RoleId == role.ID.Value).AnyAsync())
            {
                return await SendErrorReply("This role is already being tracked");
            }
            IGuildMember executorGuildMember;
            Result<IReadOnlyList<IMessage>> errResponse;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executorGuildMember = interactionContext.Member.Value;
                    break;
                case MessageContext messageContext:
                    {
                        Result<IGuildMember> guildMemberResult = await _restGuildAPI.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.User.ID).ConfigureAwait(false);
                        if (!guildMemberResult.IsSuccess)
                        {
                            _log.LogWarning($"Error responding to message {messageContext.MessageID} because {guildMemberResult.Error}");
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
            IGuildMember member = executorGuildMember;
            //If you are (not the user you're trying to assign to or are not premium) and you are not a mod/owner then deny you
            if ((_context.User.ID != member.User.Value.ID || member.IsNotBoosting()) && !member.IsModAdminOrOwner())
            {
                if (await Database.GetRoleCount(_context.GuildID.Value.Value, member.User.Value.ID.Value) > 0 && !member.IsModAdminOrOwner())
                {
                    return await SendErrorReply("You are only allowed one booster role on this server");
                }
                errResponse = await _feedbackService.SendContextualErrorAsync(
                    $"You do not have the required permissions to create this role for {member.Mention()}, not boosting and don't have ManageRoles mod permissions").ConfigureAwait(false);
                return !errResponse.IsSuccess
                    ? Result.FromError(result: errResponse)
                    : Result.FromSuccess();
            }

            //Check role meets criteria to be added
            List<IGuildMember> membersList = new();
            Optional<Snowflake> lastGuildMemberSnowflake = default;
            while (true)
            {
                Result<IReadOnlyList<IGuildMember>> getMembersResult =
                    await _restGuildAPI.ListGuildMembersAsync(_context.GuildID.Value, limit: 1000, after: lastGuildMemberSnowflake);
                if (!getMembersResult.IsSuccess)
                {
                    return await SendErrorReply(
                        "Could not find how many have this role, invalid. Try again or recreate this role using the bot if necessary.");
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
                case > 1:
                    return await SendErrorReply("More than 1 member has this role, cannot add to the bot");
                case 1:
                    ownerMemberSnowflake = membersList.First().User.Value.ID;
                    break;
                case 0:
                    if (newOwner is null)
                    {
                        return await SendErrorReply("No user has this role; Add a user or specify one in this command before tracking it");
                    }

                    ownerMemberSnowflake = newOwner.ID;
                    var addRoleResult = await _restGuildAPI.AddGuildMemberRoleAsync(_context.GuildID.Value,
                        ownerMemberSnowflake, role.ID,
                        reason: $"User added role when starting tracking");
                    if (!addRoleResult.IsSuccess)
                    {
                        return await SendErrorReply($"No user has this role and the bot failed to add {newOwner.Mention()}");
                    }
                    break;
            }
            
            Database.RoleData roleData = new()
            {
                Color = ColorTranslator.ToHtml(role.Colour),
                Name = role.Name,
                RoleId = role.ID.Value,
                ServerId = _context.GuildID.Value.Value,
                RoleUserId = ownerMemberSnowflake.Value
            };
            await database.RolesCreated.AddAsync(roleData);
            await database.SaveChangesAsync();
            return await SendSuccessReply($"Successfully started tracking {role.Mention()}, you can now modify it via bot commands");
        }

        [Command("modify-role")]
        [Description("Modify a role's properties")]
        public async Task<IResult> ModifyRole([Description("The role to change")] IRole role,
            [Description("The new name to give it")]string? newName = null,
            [Description("Color in #XxXxXx format, leave blank or #000000 to keep current color")] string? newColorString = null,
            [Description("The image url to use for the icon")] string? newImageUrl = null
            )
        {
            if (newName == null && newColorString == null && newImageUrl == null)
            {
                return await SendErrorReply("You must specify at least one property of the role to change");
            }
            //Check Server Member has permissions to use command
            IGuildMember member;
            Result<IReadOnlyList<IMessage>> errResponse;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    member = interactionContext.Member.Value;
                    break;
                case MessageContext messageContext:
                    Result<IGuildMember> guildMemberResult = await _restGuildAPI.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.User.ID).ConfigureAwait(false);
                    if (!guildMemberResult.IsSuccess)
                    {
                        _log.LogWarning($"Error responding to message {messageContext.MessageID} because {guildMemberResult.Error}");
                        errResponse = await _feedbackService.SendContextualErrorAsync("Make sure you are in a server").ConfigureAwait(false);
                        return errResponse.IsSuccess
                            ? Result.FromSuccess()
                            : Result.FromError(result: errResponse);
                    }

                    member = guildMemberResult.Entity;
                    break;
                default:
                    errResponse = await _feedbackService.SendContextualErrorAsync("I don't know how you invoked this command").ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
            }
            await using Database.RoleDataDbContext database = new();
            Database.RoleData? roleData = await database.RolesCreated
                .Where(rd => _context.GuildID.Value.Value == rd.ServerId && role.ID.Value == rd.RoleId).FirstOrDefaultAsync();
            if (roleData == null)
            {
                return await SendErrorReply("Role not found in database, check that this command is tracking it");
            }
            //If they don't have ManageRoles perm and if they either did not create the role or do not have the role, deny access
            if (_context.User.ID != member.User.Value.ID && !member.IsModAdminOrOwner())
            {
                return await SendErrorReply("You do not have permission to modify this role, you either you did not create it or do not have it and you don't have the mod permissions to manage roles");
            }

            //Handle Name Change
            if (newName is not null)
            {
                roleData.Name = newName;
            }
            //Handle Color Change
            Color? newRoleColor = null;
            if (newColorString is not null)
            {
                try
                {
                    newRoleColor = GetColorFromString(newColorString);
                    roleData.Color = ColorTranslator.ToHtml(newRoleColor.Value);
                }
                catch (ArgumentException e)
                {
                    _log.LogWarning("Color not found {color} because {e}", newColorString, e);
                    Result<IReadOnlyList<IMessage>> reply = await _feedbackService.SendContextualErrorAsync($"Invalid color {newColorString}, must be in the format #XxXxXx or a common color name, check your spelling").ConfigureAwait(false);
                    return !reply.IsSuccess
                        ? Result.FromError(result: reply)
                        : Result.FromSuccess();
                }
            }
            //Handle Image Change
            MemoryStream? newIconStream = null;
            IImageFormat? newIconFormat = null;
            if (newImageUrl is not null)
            {
                //Get guild info to tell premium tier
                Result<IGuild> getGuildResult = await _restGuildAPI.GetGuildAsync(_context.GuildID.Value);
                if (!getGuildResult.IsSuccess)
                {
                    return await SendErrorReply("Could not get info about the guild you are in, try again later");
                }

                if (getGuildResult.Entity.PremiumTier < PremiumTier.Tier2)
                {
                    return await SendErrorReply(
                        $"Server must be boosted at least to tier 2 before adding role icons.\n{7 - getGuildResult.Entity.PremiumSubscriptionCount.Value} more boost needed");
                }
                IResult? makeNewRole;
                (newIconStream, newIconFormat, makeNewRole) = await ImageUrlToBase64(imageUrl: newImageUrl);
                if (newIconStream is null)
                {
                    //If this is null, there was no error so just assume image not valid
                    if (makeNewRole != null)
                    {
                        return makeNewRole;
                    }
                }
            }

            // Stream? imgStream = newImageUrl is not null
            //     ? await Program.httpClient
            //         .GetStreamAsync(requestUri: newImageUrl, cancellationToken: this.CancellationToken)
            //         .ConfigureAwait(false)
            //     : null;
            Result<IRole> modifyRoleResult = await _restGuildAPI.ModifyGuildRoleAsync(_context.GuildID.Value, role.ID,
                newName ?? default(Optional<string?>),
                color: newRoleColor ?? default(Optional<Color?>),
                icon: newIconStream ?? default(Optional<Stream?>),
                reason: $"Member requested to modify role");
            Result<IReadOnlyList<IMessage>> replyResult;
            if (!modifyRoleResult.IsSuccess)
            {
                //TODO: Handle image failure on servers without boosts
                _log.LogWarning($"Could not modify {role.Mention()} because {modifyRoleResult.Error}");
                if (modifyRoleResult.Error.Message == "Unknown or unsupported image format.")
                {
                    return await SendErrorReply(
                        $"format {newIconFormat.Name} rejected by discord please convert to JPG or PNG");
                }
                replyResult = await _feedbackService.SendContextualSuccessAsync(
                    $"Could not modify {role.Mention()} check that the bot has the correct permissions");
                return replyResult.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(replyResult);
            }
            //If we changed db items and didn't save to the db
            if ((newName is not null || newColorString is not null) && await database.SaveChangesAsync() < 1)
            {
                //TODO: Change role back or queue a later write to the database
                replyResult = await _feedbackService.SendContextualSuccessAsync(
                    $"Could not modify database for {modifyRoleResult.Entity.Mention()}, try again later");
                return replyResult.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(replyResult);
            }

            replyResult = await _feedbackService.SendContextualSuccessAsync(
                $"Successfully modified role {modifyRoleResult.Entity.Mention()}");
            return replyResult.IsSuccess
                ? Result.FromSuccess()
                : Result.FromError(replyResult);
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

        [Command("check-boosting")]
        [Description("Prints who is and is not boosting")]
        public async Task<IResult> CheckBoosting()
        {
            string msg = await Program.CheckBoosting(server: _context.GuildID.Value, _restGuildAPI: _restGuildAPI).ConfigureAwait(false);

            Result<IReadOnlyList<IMessage>> reply = await _feedbackService.SendContextualSuccessAsync(contents: msg, ct: this.CancellationToken).ConfigureAwait(false);
            return !reply.IsSuccess
                ? Result.FromError(result: reply)
                : Result.FromSuccess();
        }
    }

    public class EmptyCommands : CommandGroup
    {

    }
}