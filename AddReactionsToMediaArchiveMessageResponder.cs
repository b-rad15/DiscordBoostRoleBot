using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Remora.Commands.Attributes;
using Remora.Commands.Groups;
using Remora.Discord.API.Abstractions.Gateway.Events;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Attributes;
using Remora.Discord.Commands.Contexts;
using Remora.Discord.Commands.Feedback.Messages;
using Remora.Discord.Commands.Feedback.Services;
using Remora.Discord.Gateway.Responders;
using Remora.Rest.Core;
using Remora.Rest.Results;
using Remora.Results;

[assembly: InternalsVisibleTo("DiscordBotTests")]
namespace DiscordBoostRoleBot
{
    internal class AddReactionsToMediaArchiveMessageResponder : IResponder<IMessageCreate>
    {
        private readonly IDiscordRestChannelAPI                              _channelApi;
        private readonly ILogger<AddReactionsToMediaArchiveMessageResponder> _logger;
        private readonly IConfiguration                                      _config;
        private readonly Database                                            _database;


        public AddReactionsToMediaArchiveMessageResponder(IDiscordRestChannelAPI channelApi, ILogger<AddReactionsToMediaArchiveMessageResponder> logger, IConfiguration config, Database database)
        {
            _channelApi  = channelApi;
            _logger      = logger;
            _config      = config;
            _database = database;
        }

        public async Task<Result> RespondAsync(IMessageCreate gatewayEvent, CancellationToken ct = new())
        {
            if (!gatewayEvent.GuildID.HasValue)
            {
                _logger.LogDebug("No server was in event, skipping");
                return Result.FromSuccess();
            }
            Snowflake serverId  = gatewayEvent.GuildID.Value;
            Snowflake userId    = gatewayEvent.Author.ID;
            Snowflake messageId = gatewayEvent.ID;
            Snowflake channelId = gatewayEvent.ChannelID;
#if DEBUG
            Snowflake? testServer = _config.GetValue<Snowflake?>("TestServerId");
            if (testServer is not null && testServer != serverId)
            {
                _logger.LogDebug("{server} is not the debug server, skipping", serverId);
                return Result.FromSuccess();
            }
#endif
            Database.MessageReactorSettings? dbItem = await _database.GetMessageReactorSettings(serverId,userId: userId).ConfigureAwait(false);
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

                if (!match.Groups["id"].Success && emote.Length != 1)
                {
                    _logger.LogWarning("{emote} has no id, this should fail", match.Groups["emoteWithId"].Value);
                }
                string emotePrepped = AddReactionsToMediaArchiveMessageResponder.PrepEmoteForReaction(emote);
                Result addReactionsResult = await _channelApi.CreateReactionAsync(channelId, messageId, emotePrepped, ct: ct).ConfigureAwait(false);
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

        public static readonly Regex EmoteWithRequiredIdRegex = new("^<(?<animated>a)?:(?<name>[a-zA-Z0-9]+):(?<id>[0-9]+)>$");
        public static readonly Regex EmoteWithoutRequiredIdRegex = new("^<(?<emoteWithId>(?<animated>a)?:(?<name>[a-zA-Z0-9]+)(:(?<id>[0-9]+))?)>$");

        public static bool CheckEmoteForReaction(string emote)
        {
            return EmoteWithRequiredIdRegex.IsMatch(emote);
        }
    }

    internal class AddReactionsToMediaArchiveCommands : CommandGroup
    {
        private readonly ICommandContext                             _context;
        private readonly FeedbackService                             _feedbackService;
        private readonly IDiscordRestChannelAPI                      _channelApi;
        private readonly IDiscordRestGuildAPI                        _guildApi;
        private readonly IDiscordRestInteractionAPI                  _interactionApi;
        private readonly ILogger<AddReactionsToMediaArchiveCommands> _logger;
        private readonly Database                   database;

        public AddReactionsToMediaArchiveCommands(ICommandContext context, FeedbackService feedbackService, IDiscordRestChannelAPI channelApi, ILogger<AddReactionsToMediaArchiveCommands> logger, IDiscordRestGuildAPI guildApi, IDiscordRestInteractionAPI interactionApi, Database database)
        {
            _context         = context;
            _feedbackService = feedbackService;
            _channelApi      = channelApi;
            _logger          = logger;
            _guildApi        = guildApi;
            _interactionApi  = interactionApi;
            this.database    = database;
        }

        [Command("react-settings")]
        [Description("Change the settings for your server")]
        public async Task<Result> SetReactorSettings(
            [Description("The channel to react in")]
            [ChannelTypes(ChannelType.PublicThread, ChannelType.PrivateThread, ChannelType.GuildText)]
            IChannel? channel = null,
            [Description("The User who's messages will be reacted to")]
            IGuildMember? user = null,
            [Description("Emotes to react with, separated with ;")]
            string? emotesString = null)
        {
            IGuildMember                    executorGuildMember;
            PartialGuild                    executionGuild;
            Result<IReadOnlyList<IMessage>> errResponse;
            Snowflake?                      messageId       = null;
            var                             responseMessage = string.Empty;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executionGuild          = new(interactionContext.Interaction.GuildID);
                    executorGuildMember = interactionContext.Interaction.Member.Value;
                    break;
                case MessageContext messageContext:
                    {
                        executionGuild = new(messageContext.GuildID);
                        Result<IGuildMember> guildMemberResult = await _guildApi.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.Message.Author.Value.ID).ConfigureAwait(false);
                        if (!guildMemberResult.IsSuccess)
                        {
                            _logger.LogWarning($"Error responding to message {messageContext.Message.ID} because {guildMemberResult.Error}");
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

            // await using Database.DiscordDbContext database = new();
            Database.MessageReactorSettings? serverSettings = await 
                database.GetMessageReactorSettings(executionGuild.ID.Value).ConfigureAwait(false);
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
                            restError1.Error.Code, restError1.Error.Message);
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
                    ServerId = executionGuild.ID.Value,
                    Emotes = emotesString,
                    UserIds = user.User.Value.ID,
                    ChannelId = channel.ID
                };
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
                    serverSettings.ChannelId = channel.ID;
                    responseMessage += $"Added channel {channel.Mention()}\n";
                }

                if (user is not null)
                {
                    serverSettings.UserIds = user.User.Value.ID;
                    responseMessage += $"Added user {user}\n";
                }

            }
            await database.UpdateMessageReactorSettings(serverSettings).ConfigureAwait(false);
            responseMessage += $"Reacting to {serverSettings.UserIds.User()} messages in {serverSettings.ChannelId.Channel()} with {serverSettings.Emotes}";
            Result<IReadOnlyList<IMessage>> replyResult = await _feedbackService.SendContextualSuccessAsync(responseMessage).ConfigureAwait(false);
            if (replyResult.IsSuccess)
            {
                return Result.FromSuccess();
            }
            
            if (replyResult.Error is RestResultError<RestError> restError2)
            {

                _logger.LogCritical(
                    "Could not respond to this message, this should only happen if discord's api is down but happened because rest error {code}: {error}",
                    restError2.Error.Code, restError2.Error.Message);
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
        public async Task<Result> SetReactUserFromMessage([Description("The message id to base the command on")] ulong message_id, [Description("Channel the message is in")][ChannelTypes(ChannelType.PrivateThread, ChannelType.PublicThread, ChannelType.GuildText)] IChannel channel)
        {
            IGuildMember executorGuildMember;
            Result<IReadOnlyList<IMessage>> errResponse;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executorGuildMember = interactionContext.Interaction.Member.Value;
                    break;
                case MessageContext messageContext:
                    {
                        Result<IGuildMember> guildMemberResult = await _guildApi.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.Message.Author.Value.ID).ConfigureAwait(false);
                        if (!guildMemberResult.IsSuccess)
                        {
                            _logger.LogWarning($"Error responding to message {messageContext.Message.ID} because {guildMemberResult.Error}");
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
                        restError.Error.Code, restError.Error.Message);
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
            return await SetReactUserFromMessageInternal(message).ConfigureAwait(false);
        }

        [CommandType(ApplicationCommandType.Message)]
        [Command("react-set-user")]
        [Ephemeral]
        public async Task<Result> SetReactUserFromMessage(IPartialMessage message = null)
        {
            var interactionContext = _context as InteractionContext;
            IGuildMember executorGuildMember;
            Result<IReadOnlyList<IMessage>> errResponse;
            executorGuildMember = interactionContext.Interaction.Member.Value;

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
            IPartialMessage? msg = (interactionContext?.Interaction.Data.Value.AsT0)?.Resolved.Value.Messages.Value.FirstOrDefault().Value;
            if (msg is not null) return await SetReactUserFromMessageInternal(msg).ConfigureAwait(false);
            Result<IReadOnlyList<IMessage>> respondResult = await _feedbackService.SendContextualSuccessAsync("No Message Found", options:new FeedbackMessageOptions
            {
                MessageFlags = MessageFlags.Ephemeral
            }).ConfigureAwait(false);
            return respondResult.IsSuccess
                ? Result.FromSuccess()
                : Result.FromError(respondResult);
        }
        public async Task<Result> SetReactUserFromMessageInternal([Description("Any message from the user")] IPartialMessage message)
        {
            PartialGuild executionGuild = new(_context switch
            {
                InteractionContext interactionContext => interactionContext.Interaction.GuildID,
                TextCommandContext messageContext => messageContext.GuildID,
                _ => throw new ArgumentOutOfRangeException(nameof(_context), _context, null),
            });
            // await using Database.DiscordDbContext database = new();
            Database.MessageReactorSettings? serverSettings = await
                database.GetMessageReactorSettings(executionGuild.ID.Value).ConfigureAwait(false);
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
                        restError.Error.Code, restError.Error.Message);
                }
                else
                {
                    _logger.LogCritical(
                        "Could not respond to this message, this should only happen if discord's api is down but happened because {error}",
                        respondResult.Error);
                }

                return Result.FromError(respondResult);
            }

            serverSettings.UserIds = message.Author.Value.ID;
            await database.UpdateMessageReactorSettings(serverSettings).ConfigureAwait(false);
            respondResult = await _feedbackService.SendContextualSuccessAsync($"Set user to {serverSettings.UserIds.User()}", options: new FeedbackMessageOptions
            {
                MessageFlags = MessageFlags.Ephemeral
            }).ConfigureAwait(false);
            return respondResult.IsSuccess
                ? Result.FromSuccess()
                : Result.FromError(respondResult);
        }


        public static readonly Regex MessageLinkRegex = new(@"^(http(s)?://)?(www\.)?discord.com/channels/(?<serverId>[0-9]+)/(?<channelId>[0-9]+)/(?<messageId>[0-9]+)$");

        [DiscordDefaultDMPermission(false)]
        [Command("react-to-message")]
        [Description("Send the configured emotess to the given message id")]
        [Ephemeral]
        public async Task<Result> TestWithMessage([Description("The message Id number or link to test with")] string messageIdString, [Description("The channel that message is in")]IChannel? channel = null, [Description("Emotes to react with, separated with ;")] string? emotesString = null)
        {
            Result<IReadOnlyList<IMessage>> errResponse;
            //Verify Message Link/ID inputs
            Match linkMatch = MessageLinkRegex.Match(messageIdString);
            Snowflake channelId;
            if (linkMatch.Success)
            {
                messageIdString = linkMatch.Groups["messageId"].Value;
                channelId = new Snowflake(Convert.ToUInt64(linkMatch.Groups["channelId"].Value));
            } else
            {
                if (channel is null)
                {
                    errResponse = await _feedbackService.SendContextualErrorAsync(
                        "If message link is not specified, channel must be specified");
                    return errResponse.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(result: errResponse);
                }
                channelId = channel.ID;
            }

            IGuildMember executorGuildMember;
            PartialGuild executionGuild;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executionGuild          = new(interactionContext.Interaction.GuildID);
                        executorGuildMember = interactionContext.Interaction.Member.Value;
                    break;
                case MessageContext messageContext:
                    {
                        executionGuild = new(messageContext.GuildID);
                        Result<IGuildMember> guildMemberResult = await _guildApi.GetGuildMemberAsync(guildID: messageContext.GuildID.Value, userID: messageContext.Message.Author.Value.ID).ConfigureAwait(false);
                        if (!guildMemberResult.IsSuccess)
                        {
                            _logger.LogWarning($"Error responding to message {messageContext.Message.ID} because {guildMemberResult.Error}");
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
            if (emotesString is null)
            {
                emotesString = await database.GetEmoteString(executionGuild.ID.Value).ConfigureAwait(false);
                if (emotesString is null)
                {
                    Result<IReadOnlyList<IMessage>> replyResult = await _feedbackService.SendContextualErrorAsync(
                        $"No Configuration exists, and no emotes specified, use /react-settings to make one").ConfigureAwait(false);
                    return replyResult.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(replyResult);
                }
            }
            var messageId = new Snowflake(Convert.ToUInt64(messageIdString));
            foreach (string emote in emotesString.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
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
        public async Task<Result> ReactToMessage(IPartialMessage message = null)
        {
            var interactionContext = _context as InteractionContext;
            // var createResponseResult = await _interactionApi.CreateInteractionResponseAsync(interactionContext.ID,
            //     interactionContext.Token,
            //     new InteractionResponse(InteractionCallbackType.DeferredChannelMessageWithSource) {Data = new InteractionCallbackData{Flags = MessageFlags.Ephemeral | MessageFlags.Loading}});
            
            IPartialMessage? messageResp = interactionContext?.Interaction.Data.Value.AsT0.Resolved.Value.Messages.Value.First().Value;
            Result<IReadOnlyList<IMessage>> errResponse;
            if (messageResp is null)
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("There is no message", options: new FeedbackMessageOptions
                {
                    MessageFlags = MessageFlags.Ephemeral
                }).ConfigureAwait(false);
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(result: errResponse);
            }
            if (!interactionContext!.Interaction.Member.Value.IsChannelModAdminOrOwner())
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("You do not have mod permissions", options: new FeedbackMessageOptions
                {
                    MessageFlags = MessageFlags.Ephemeral
                }).ConfigureAwait(false);
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(result: errResponse);
            }
            // await using Database.DiscordDbContext database = new();
            PartialGuild                                executionGuild = new(interactionContext.Interaction.GuildID);
            Database.MessageReactorSettings? dbItem = await database.GetMessageReactorSettings(executionGuild.ID.Value).ConfigureAwait(false);
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
            Optional<Snowflake> messageId = messageResp.ID;
            foreach (string emote in dbItem.Emotes.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string emotePrepped = AddReactionsToMediaArchiveMessageResponder.PrepEmoteForReaction(emote);
                if (!AddReactionsToMediaArchiveMessageResponder.CheckEmoteForReaction(emotePrepped))
                    _logger.LogWarning("{emote} does not look like right format", emotePrepped);
                Result addReactionsResult = await _channelApi.CreateReactionAsync(messageResp.ChannelID.Value, messageId.Value, emotePrepped).ConfigureAwait(false);
                if (!addReactionsResult.IsSuccess)
                {
                    _logger.LogError("Could not react to message {message} with reaction {emote} because {reason}", messageId, emote, addReactionsResult.Error);
                    Result<IReadOnlyList<IMessage>> replyResult = await _feedbackService.SendContextualErrorAsync(
                        $"Could not react to message {messageId} with reaction {emote} because {addReactionsResult.Error}", options: new FeedbackMessageOptions
                        {
                            MessageFlags = MessageFlags.Ephemeral
                        }).ConfigureAwait(false);
                    return replyResult.IsSuccess
                        ? Result.FromSuccess()
                        : Result.FromError(replyResult);
                }
                _logger.LogDebug("Reacted with {reaction} to message {message}", emote, messageId);
                msg += $"Reacted with {emote} to message {messageId}";
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
