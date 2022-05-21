using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic;
using Remora.Commands.Attributes;
using Remora.Commands.Groups;
using Remora.Discord.API.Abstractions.Gateway.Events;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Attributes;
using Remora.Discord.Commands.Conditions;
using Remora.Discord.Commands.Contexts;
using Remora.Discord.Commands.Feedback.Messages;
using Remora.Discord.Commands.Feedback.Services;
using Remora.Discord.Extensions.Embeds;
using Remora.Discord.Gateway.Responders;
using Remora.Rest.Core;
using Remora.Rest.Results;
using Remora.Results;
using SixLabors.ImageSharp;
using Z.EntityFramework.Plus;
using Color = System.Drawing.Color;

[assembly: InternalsVisibleTo("DiscordBotTests")]
namespace DiscordBoostRoleBot
{
    internal class AddReactionsToMediaArchiveMessageResponder : IResponder<IMessageCreate>
    {
        private readonly IDiscordRestChannelAPI _channelApi;
        private readonly ILogger<AddReactionsToMediaArchiveMessageResponder> _logger;

        public AddReactionsToMediaArchiveMessageResponder(IDiscordRestChannelAPI channelApi, ILogger<AddReactionsToMediaArchiveMessageResponder> logger)
        {
            _channelApi = channelApi;
            _logger = logger;
        }

        public async Task<Result> RespondAsync(IMessageCreate gatewayEvent, CancellationToken ct = new())
        {
            if (!gatewayEvent.GuildID.HasValue)
            {
                _logger.LogDebug("No server was in event, skipping");
                return Result.FromSuccess();
            }
            ulong serverId = gatewayEvent.GuildID.Value.Value;
            ulong userId = gatewayEvent.Author.ID.Value;
            Snowflake messageId = gatewayEvent.ID;
            Snowflake channelId = gatewayEvent.ChannelID;
#if DEBUG
            if (Program.Config.TestServerId is not null && Program.Config.TestServerId != serverId)
            {
                _logger.LogDebug("{server} is not the debug server, skipping", serverId);
                return Result.FromSuccess();
            }
#endif
            await using Database.DiscordDbContext database = new();
            Database.MessageReactorSettings? dbItem = await database.MessageReactorSettings.Where(
                mrs => mrs.ServerId == serverId && mrs.UserIds == userId).AsNoTracking().FirstOrDefaultAsync(cancellationToken: ct).ConfigureAwait(false);
            if (dbItem is null)
            {
                return Result.FromSuccess();
            }

            foreach (string emote in dbItem.Emotes.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                Match match = EmoteWithoutRequiredIdRegex.Match(emote);
                if(!match.Success && emote.Length != 1)
                {
                    _logger.LogWarning("{emote} does not look like right format", match.Groups["emoteWithId"].Value);
                }

                if (!match.Groups["id"].Success)
                {
                    _logger.LogWarning("{emote} has no id, this should fail", match.Groups["emoteWithId"].Value);
                }

                Result addReactionsResult = await _channelApi.CreateReactionAsync(channelId, messageId, match.Groups["emoteWithId"].Value, ct: ct).ConfigureAwait(false);
                if (!addReactionsResult.IsSuccess)
                {
                    _logger.LogError("Could not react to message {message} with reaction {emote} because {reason}", messageId, emote, addReactionsResult.Error);
                }
                _logger.LogDebug("Reacted with {reaction} to message {message}", emote, messageId);
            }
            return Result.FromSuccess();
        }

        public static string PrepEmoteForReaction(string emote)
        {
            return emote.Trim('<', '>', ':');
        }

        public static readonly Regex EmoteWithRequiredIdRegex = new("^<:(?<animated>a:)?(?<name>[a-zA-Z0-9]+):(?<id>[0-9]+)>$");
        public static readonly Regex EmoteWithoutRequiredIdRegex = new("^<:(?<emoteWithId>(?<animated>a:)?(?<name>[a-zA-Z0-9]+)(:(?<id>[0-9]+))?)>$");

        public static bool CheckEmoteForReaction(string emote)
        {
            return EmoteWithRequiredIdRegex.IsMatch(emote);
        }
    }

    internal class AddReactionsToMediaArchiveCommands : CommandGroup
    {

        private readonly ICommandContext _context;
        private readonly FeedbackService _feedbackService;
        private readonly IDiscordRestChannelAPI _channelApi;
        private readonly IDiscordRestGuildAPI _guildApi;
        private readonly IDiscordRestInteractionAPI _interactionApi;
        private readonly ILogger<AddReactionsToMediaArchiveCommands> _logger;

        public AddReactionsToMediaArchiveCommands(ICommandContext context, FeedbackService feedbackService, IDiscordRestChannelAPI channelApi, ILogger<AddReactionsToMediaArchiveCommands> logger, IDiscordRestGuildAPI guildApi, IDiscordRestInteractionAPI interactionApi)
        {
            _context = context;
            _feedbackService = feedbackService;
            _channelApi = channelApi;
            _logger = logger;
            _guildApi = guildApi;
            _interactionApi = interactionApi;
        }

        [Command("react-settings")]
        [Description("Change the settings for your server")]
        public async Task<Result> SetReactorSettings(
            [Description("The channel to react in")]
            [ChannelTypes(ChannelType.GuildPublicThread, ChannelType.GuildPrivateThread, ChannelType.GuildText)]
            IChannel? channel = null,
            [Description("The User who's messages will be reacted to")]
            IGuildMember? user = null,
            [Description("Emotes to react with, separated with ;")]
            string? emotesString = null)
        {
            IGuildMember executorGuildMember;
            Result<IReadOnlyList<IMessage>> errResponse;
            Snowflake? messageId = null;
            var responseMessage = string.Empty;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executorGuildMember = interactionContext.Member.Value;
                    break;
                case MessageContext messageContext:
                    {
                        Result<IGuildMember> guildMemberResult = await _guildApi.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.User.ID).ConfigureAwait(false);
                        if (!guildMemberResult.IsSuccess)
                        {
                            _logger.LogWarning($"Error responding to message {messageContext.MessageID} because {guildMemberResult.Error}");
                            errResponse = await _feedbackService.SendContextualErrorAsync("Make sure you are in a server", options: new FeedbackMessageOptions
                            {
                                MessageFlags = MessageFlags.Ephemeral
                            }).ConfigureAwait(false);
                            return errResponse.IsSuccess
                                ? Result.FromSuccess()
                                : Result.FromError(result: errResponse);
                        }
                        executorGuildMember = guildMemberResult.Entity;
                        break;
                    }
                default:
                    errResponse = await _feedbackService.SendContextualErrorAsync("I don't know how you invoked this command", options: new FeedbackMessageOptions
                    {
                        MessageFlags = MessageFlags.Ephemeral
                    }).ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
            }

            if (!executorGuildMember.IsChannelModAdminOrOwner())
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("You do not have mod permissions", options: new FeedbackMessageOptions
                {
                    MessageFlags = MessageFlags.Ephemeral
                }).ConfigureAwait(false);
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(result: errResponse);
            }

            if (emotesString is not null)
            {
                responseMessage = "";
                IEnumerable<string> emotes = emotesString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(emote => emote.Length != 0);
                foreach (string emote in emotes)
                {
                    Match match = AddReactionsToMediaArchiveMessageResponder.EmoteWithoutRequiredIdRegex.Match(emote);
                    switch (emote.Length)
                    {
                        case 1:
                            continue;
                        case > 1 when !match.Success:
                            responseMessage +=
                                $"Emote {emote} does not appear to be in the correct format, run a test before relying on this to react automatically\n";
                            break;
                        case > 1 when !match.Groups["id"].Success:
                        {
                                responseMessage += $"Emote {emote} has no id specified, to ensure bot works correctly, choose the emoji from the panel\n";
                                break;
                        }
                        case > 1 when match.Groups["animated"].Success:
                            responseMessage += $"Animated emotes may not work correctly";
                            break;
                    }
                }
            }

            await using Database.DiscordDbContext database = new();
            Database.MessageReactorSettings? serverSettings = await 
                database.MessageReactorSettings.Where(mrs => mrs.ServerId == _context.GuildID.Value.Value).FirstOrDefaultAsync().ConfigureAwait(false);
            var newEntry = false;
            //TODO: Test emotes can be reacted before doing it
            if (serverSettings == null)
            {
                if (channel is null || user is null || string.IsNullOrWhiteSpace(emotesString))
                {
                    responseMessage += "Must specify all 3 of channel, user, and emotes";
                    Result<IReadOnlyList<IMessage>> respondResult =
                        await _feedbackService.SendContextualErrorAsync(responseMessage).ConfigureAwait(false);
                    if (respondResult.IsSuccess) return Result.FromSuccess();
                    if (respondResult.Error is RestResultError<RestError> restError1)
                    {

                        _logger.LogCritical(
                            "Could not respond to this message, this should only happen if discord's api is down but happened because rest error {code}: {error}",
                            restError1.Error.Code.Humanize(LetterCasing.Title), restError1.Error.Message);
                    }
                    else
                    {
                        _logger.LogCritical(
                            "Could not respond to this message, this should only happen if discord's api is down but happened because {error}",
                            respondResult.Error);
                    }
                    return Result.FromError(respondResult);
                }

                newEntry = true;
                serverSettings = new Database.MessageReactorSettings
                {
                    ServerId = _context.GuildID.Value.Value,
                    Emotes = emotesString,
                    UserIds = user.User.Value.ID.Value,
                    ChannelId = channel.ID.Value
                };
                database.Add(serverSettings);
                //TODO: check was added correctly
            }
            else
            {
                if (emotesString is not null)
                {
                    serverSettings.Emotes = emotesString;
                    responseMessage += $"Added Emotes {emotesString}\n";
                }

                if (channel is not null)
                {
                    serverSettings.ChannelId = channel.ID.Value;
                    responseMessage += $"Added channel {channel.Mention()}\n";
                }

                if (user is not null)
                {
                    serverSettings.UserIds = user.User.Value.ID.Value;
                    responseMessage += $"Added user {user}\n";
                }

                // Result<IReadOnlyList<IMessage>> replyResult = await _feedbackService.SendContextualSuccessAsync(msg.TrimEnd());
            }
            await database.SaveChangesAsync().ConfigureAwait(false);
            responseMessage += $"Reacting to {new Snowflake(serverSettings.UserIds).User()} messages in {new Snowflake(serverSettings.ChannelId).Channel()} with {serverSettings.Emotes}";
            Result<IReadOnlyList<IMessage>> replyResult = await _feedbackService.SendContextualSuccessAsync(responseMessage).ConfigureAwait(false);
            if (replyResult.IsSuccess)
            {
                return Result.FromSuccess();
            }
            
            if (replyResult.Error is RestResultError<RestError> restError2)
            {

                _logger.LogCritical(
                    "Could not respond to this message, this should only happen if discord's api is down but happened because rest error {code}: {error}",
                    restError2.Error.Code.Humanize(LetterCasing.Title), restError2.Error.Message);
            }
            else
            {
                _logger.LogCritical(
                    "Could not respond to this message, this should only happen if discord's api is down but happened because {error}",
                    replyResult.Error);
            }

            return Result.FromError(replyResult);
            
        }

        [Command("react-set-user-from-message")]
        [Description("Set the user to track based on a message id (used for tracking webhooks)")]
        public async Task<Result> SetReactUserFromMessage([Description("The message id to base the command on")] ulong message_id, [Description("Channel the message is in")][ChannelTypes(ChannelType.GuildPrivateThread, ChannelType.GuildPublicThread, ChannelType.GuildText)] IChannel channel)
        {
            IGuildMember executorGuildMember;
            Result<IReadOnlyList<IMessage>> errResponse;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executorGuildMember = interactionContext.Member.Value;
                    break;
                case MessageContext messageContext:
                    {
                        Result<IGuildMember> guildMemberResult = await _guildApi.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.User.ID).ConfigureAwait(false);
                        if (!guildMemberResult.IsSuccess)
                        {
                            _logger.LogWarning($"Error responding to message {messageContext.MessageID} because {guildMemberResult.Error}");
                            errResponse = await _feedbackService.SendContextualErrorAsync("Make sure you are in a server", options: new FeedbackMessageOptions
                            {
                                MessageFlags = MessageFlags.Ephemeral
                            }).ConfigureAwait(false);
                            return errResponse.IsSuccess
                                ? Result.FromSuccess()
                                : Result.FromError(result: errResponse);
                        }
                        executorGuildMember = guildMemberResult.Entity;
                        break;
                    }
                default:
                    errResponse = await _feedbackService.SendContextualErrorAsync("I don't know how you invoked this command", options: new FeedbackMessageOptions
                    {
                        MessageFlags = MessageFlags.Ephemeral
                    }).ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
            }

            if (!executorGuildMember.IsChannelModAdminOrOwner())
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("You do not have mod permissions", options: new FeedbackMessageOptions
                {
                    MessageFlags = MessageFlags.Ephemeral
                }).ConfigureAwait(false);
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(result: errResponse);
            }
            Result<IMessage> messageResult = await _channelApi.GetChannelMessageAsync(channel.ID, new Snowflake(message_id)).ConfigureAwait(false);
            if (!messageResult.IsSuccess)
            {
                Result<IReadOnlyList<IMessage>> respondResult =
                    await _feedbackService.SendContextualErrorAsync(
                        $"No message found").ConfigureAwait(false);
                if (respondResult.IsSuccess) return Result.FromSuccess();
                if (respondResult.Error is RestResultError<RestError> restError)
                {

                    _logger.LogCritical(
                        "Could not respond to this message, this should only happen if discord's api is down but happened because rest error {code}: {error}",
                        restError.Error.Code.Humanize(LetterCasing.Title), restError.Error.Message);
                }
                else
                {
                    _logger.LogCritical(
                        "Could not respond to this message, this should only happen if discord's api is down but happened because {error}",
                        respondResult.Error);
                }
                return Result.FromError(respondResult);
            }

            IMessage message = messageResult.Entity;
            return await SetReactUserFromMessage(message).ConfigureAwait(false);
        }

        [CommandType(ApplicationCommandType.Message)]
        [Command("react-set-user")]
        [Ephemeral]
        public async Task<Result> SetReactUserFromMessage()
        {
            var interactionContext = _context as InteractionContext;
            IGuildMember executorGuildMember;
            Result<IReadOnlyList<IMessage>> errResponse;
            executorGuildMember = interactionContext.Member.Value;

            if (!executorGuildMember.IsChannelModAdminOrOwner())
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("You do not have mod permissions", options: new FeedbackMessageOptions
                {
                    MessageFlags = MessageFlags.Ephemeral
                }).ConfigureAwait(false);
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(result: errResponse);
            }
            IPartialMessage? message = interactionContext?.Data.Resolved.Value.Messages.Value.FirstOrDefault().Value;
            if (message is not null) return await SetReactUserFromMessage(message).ConfigureAwait(false);
            Result<IReadOnlyList<IMessage>> respondResult = await _feedbackService.SendContextualSuccessAsync("No Message Found", options:new FeedbackMessageOptions
            {
                MessageFlags = MessageFlags.Ephemeral
            }).ConfigureAwait(false);
            return respondResult.IsSuccess
                ? Result.FromSuccess()
                : Result.FromError(respondResult);
        }
        public async Task<Result> SetReactUserFromMessage([Description("Any message from the user")] IPartialMessage message)
        {
            await using Database.DiscordDbContext database = new();
            Database.MessageReactorSettings? serverSettings = await
                database.MessageReactorSettings.Where(mrs => mrs.ServerId == _context.GuildID.Value.Value).FirstOrDefaultAsync().ConfigureAwait(false);
            Result<IReadOnlyList<IMessage>> respondResult;
            if (serverSettings is null)
            {
                respondResult = await _feedbackService.SendContextualErrorAsync(
                    $"Must first create entry with /{typeof(AddReactionsToMediaArchiveCommands).GetRuntimeMethod(nameof(SetReactorSettings), Array.Empty<Type>())?.GetCustomAttribute<CommandAttribute>()?.Name}", options: new FeedbackMessageOptions
                    {
                        MessageFlags = MessageFlags.Ephemeral
                    }).ConfigureAwait(false);
                if (respondResult.IsSuccess) return Result.FromSuccess();
                if (respondResult.Error is RestResultError<RestError> restError)
                {

                    _logger.LogCritical(
                        "Could not respond to this message, this should only happen if discord's api is down but happened because rest error {code}: {error}",
                        restError.Error.Code.Humanize(LetterCasing.Title), restError.Error.Message);
                }
                else
                {
                    _logger.LogCritical(
                        "Could not respond to this message, this should only happen if discord's api is down but happened because {error}",
                        respondResult.Error);
                }

                return Result.FromError(respondResult);
            }

            serverSettings.UserIds = message.Author.Value.ID.Value;
            int numRows = await database.SaveChangesAsync().ConfigureAwait(false);
            respondResult = await _feedbackService.SendContextualSuccessAsync($"Set user to {new Snowflake(serverSettings.UserIds).User()}", options: new FeedbackMessageOptions
            {
                MessageFlags = MessageFlags.Ephemeral
            }).ConfigureAwait(false);
            return respondResult.IsSuccess
                ? Result.FromSuccess()
                : Result.FromError(respondResult);
        }


        [RequireContext(ChannelContext.Guild)]
        [Command("react-to-message")]
        [Description("Send the configured emotess to the given message id")]
        [Ephemeral]
        public async Task<Result> TestWithMessage([Description("The message Id to test with")] string messageIdNumber, [Description("The channel that message is in")]IChannel channel)
        {
            IGuildMember executorGuildMember;
            Result<IReadOnlyList<IMessage>> errResponse;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executorGuildMember = interactionContext.Member.Value;
                    break;
                case MessageContext messageContext:
                    {
                        Result<IGuildMember> guildMemberResult = await _guildApi.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.User.ID).ConfigureAwait(false);
                        if (!guildMemberResult.IsSuccess)
                        {
                            _logger.LogWarning($"Error responding to message {messageContext.MessageID} because {guildMemberResult.Error}");
                            errResponse = await _feedbackService.SendContextualErrorAsync("Make sure you are in a server", options: new FeedbackMessageOptions
                            {
                                MessageFlags = MessageFlags.Ephemeral
                            }).ConfigureAwait(false);
                            return errResponse.IsSuccess
                                ? Result.FromSuccess()
                                : Result.FromError(result: errResponse);
                        }
                        executorGuildMember = guildMemberResult.Entity;
                        break;
                    }
                default:
                    errResponse = await _feedbackService.SendContextualErrorAsync("I don't know how you invoked this command", options: new FeedbackMessageOptions
                    {
                        MessageFlags = MessageFlags.Ephemeral
                    }).ConfigureAwait(false);
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
            }

            if (!executorGuildMember.IsChannelModAdminOrOwner())
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("You do not have mod permissions", options: new FeedbackMessageOptions
                {
                    MessageFlags = MessageFlags.Ephemeral
                }).ConfigureAwait(false);
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(result: errResponse);
            }
            await using Database.DiscordDbContext database = new();
            Database.MessageReactorSettings? dbItem = await database.MessageReactorSettings.Where(mrs => mrs.ServerId == _context.GuildID.Value.Value)
                .FirstOrDefaultAsync().ConfigureAwait(false);
            if (dbItem is null)
            {
                Result<IReadOnlyList<IMessage>> replyResult = await _feedbackService.SendContextualErrorAsync(
                    $"No Configuration exists, use /react-settings to make one").ConfigureAwait(false);
                return replyResult.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(replyResult);
            }
            Snowflake channelId = channel.ID;
            var messageId = new Snowflake(Convert.ToUInt64(messageIdNumber));
            foreach (string emote in dbItem.Emotes.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string emotePrepped = AddReactionsToMediaArchiveMessageResponder.PrepEmoteForReaction(emote);
                if (!AddReactionsToMediaArchiveMessageResponder.CheckEmoteForReaction(emotePrepped))
                    _logger.LogWarning("{emote} does not look like right format", emotePrepped);
                Result addReactionsResult = await _channelApi.CreateReactionAsync(channelId, messageId, emotePrepped).ConfigureAwait(false);
                if (!addReactionsResult.IsSuccess)
                {
                    _logger.LogError("Could not react to message {message} with reaction {emote} because {reason}", messageId, emote, addReactionsResult.Error);
                    Result<IReadOnlyList<IMessage>> replyResult = await _feedbackService.SendContextualErrorAsync(
                        $"Could not react to message {messageId} with reaction {emote} because {addReactionsResult.Error}").ConfigureAwait(false);
                    return replyResult.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(replyResult);
                }
                _logger.LogDebug("Reacted with {reaction} to message {message}", emote, messageId); 
                Result<IReadOnlyList<IMessage>> replyResult2 = await _feedbackService.SendContextualErrorAsync(
                    $"Reacted with {emote} to message {messageId}").ConfigureAwait(false);
                if (!replyResult2.IsSuccess)
                    Result.FromError(replyResult2);
            }
            return Result.FromSuccess();
        }

        [Command("react")]
        [CommandType(ApplicationCommandType.Message)]
        [Ephemeral]
        // [SuppressInteractionResponse(true)]
        public async Task<Result> ReactToMessage()
        {
            var interactionContext = _context as InteractionContext;
            // var createResponseResult = await _interactionApi.CreateInteractionResponseAsync(interactionContext.ID,
            //     interactionContext.Token,
            //     new InteractionResponse(InteractionCallbackType.DeferredChannelMessageWithSource) {Data = new InteractionCallbackData{Flags = MessageFlags.Ephemeral | MessageFlags.Loading}});
            
            IMessageReference? message = interactionContext?.Data.Resolved.Value.Messages.Value.First().Value.MessageReference.Value;
            Result<IReadOnlyList<IMessage>> errResponse;
            if (message is null)
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("There is no message", options: new FeedbackMessageOptions
                {
                    MessageFlags = MessageFlags.Ephemeral
                }).ConfigureAwait(false);
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(result: errResponse);
            }
            if (interactionContext!.Member.Value.IsChannelModAdminOrOwner())
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("You do not have mod permissions", options: new FeedbackMessageOptions
                {
                    MessageFlags = MessageFlags.Ephemeral
                }).ConfigureAwait(false);
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(result: errResponse);
            }
            await using Database.DiscordDbContext database = new();
            Database.MessageReactorSettings? dbItem = await database.MessageReactorSettings.Where(mrs => mrs.ServerId == _context.GuildID.Value.Value)
                .FirstOrDefaultAsync().ConfigureAwait(false);
            if (dbItem is null)
            {
                Result<IReadOnlyList<IMessage>> replyResult = await _feedbackService.SendContextualErrorAsync(
                    $"No Configuration exists, use /react-settings to make one", options:new FeedbackMessageOptions
                    {
                        MessageFlags = MessageFlags.Ephemeral
                    }).ConfigureAwait(false);
                return replyResult.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(replyResult);
            }

            var msg = "";
            foreach (string emote in dbItem.Emotes.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string emotePrepped = AddReactionsToMediaArchiveMessageResponder.PrepEmoteForReaction(emote);
                if (!AddReactionsToMediaArchiveMessageResponder.CheckEmoteForReaction(emotePrepped))
                    _logger.LogWarning("{emote} does not look like right format", emotePrepped);
                Result addReactionsResult = await _channelApi.CreateReactionAsync(message.ChannelID.Value, message.MessageID.Value, emotePrepped).ConfigureAwait(false);
                if (!addReactionsResult.IsSuccess)
                {
                    _logger.LogError("Could not react to message {message} with reaction {emote} because {reason}", message.MessageID, emote, addReactionsResult.Error);
                    Result<IReadOnlyList<IMessage>> replyResult = await _feedbackService.SendContextualErrorAsync(
                        $"Could not react to message {message.MessageID} with reaction {emote} because {addReactionsResult.Error}", options: new FeedbackMessageOptions
                        {
                            MessageFlags = MessageFlags.Ephemeral
                        }).ConfigureAwait(false);
                    return replyResult.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(replyResult);
                }
                _logger.LogDebug("Reacted with {reaction} to message {message}", emote, message.MessageID);
                msg += $"Reacted with {emote} to message {message.MessageID}";
            }

            Result<IReadOnlyList<IMessage>> responseResult = await _feedbackService.SendContextualSuccessAsync(msg, options: new FeedbackMessageOptions
            {
                MessageFlags = MessageFlags.Ephemeral
            }).ConfigureAwait(false);
            return responseResult.IsSuccess 
                ? Result.FromSuccess() 
                : Result.FromError(responseResult);
        }
    }
}
