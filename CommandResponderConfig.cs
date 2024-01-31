using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Remora.Commands.Attributes;
using Remora.Commands.Groups;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Attributes;
using Remora.Discord.Commands.Conditions;
using Remora.Discord.Commands.Contexts;
using Remora.Discord.Commands.Extensions;
using Remora.Discord.Commands.Feedback.Messages;
using Remora.Discord.Commands.Feedback.Services;
using Remora.Discord.Commands.Services;
using Remora.Rest.Core;
using Remora.Results;

namespace DiscordBoostRoleBot
{
    internal class CommandResponderConfigCommands : CommandGroup
    {

        private readonly ICommandContext                         _context;
        private readonly FeedbackService                         _feedbackService;
        private readonly IDiscordRestGuildAPI                    _guildApi;
        private readonly ILogger<CommandResponderConfigCommands> _logger;
        private readonly Database                                _databaseClass;
        public CommandResponderConfigCommands(ICommandContext context, FeedbackService feedbackService, IDiscordRestGuildAPI guildApi, ILogger<CommandResponderConfigCommands> logger, Database databaseClass)
        {
            _context            = context;
            _feedbackService    = feedbackService;
            _guildApi           = guildApi;
            _logger             = logger;
            _databaseClass = databaseClass;
        }

        [RequireContext(ChannelContext.Guild)]
        [Command("set-prefix")]
        [CommandType(type: ApplicationCommandType.ChatInput)]
        [Description("Set the prefix for the bot")]
        public async Task<Result> SetPrefix([Description("Command Prefix to set, can be any string")] string prefix)
        {
            IGuildMember executorGuildMember;
            PartialGuild executorGuild;
            Result<IReadOnlyList<IMessage>> errResponse;
            Snowflake? messageId = null;
            var responseMessage = string.Empty;
            switch (_context)
            {
                case InteractionContext interactionContext:
                    executorGuild = new(interactionContext.Interaction.GuildID);
                    executorGuildMember = interactionContext.Interaction.Member.Value;
                    break;
                case MessageContext messageContext:
                    {
                        executorGuild = new(messageContext.GuildID);
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

            if (!executorGuildMember.Permissions.HasValue)
            {
                Result<IGuildMember> getPermsResult = await Program.AddGuildMemberPermissions(executorGuildMember, executorGuild.ID.Value, this.CancellationToken);
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

            if (!executorGuild.ID.HasValue)
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("You are not in a server, cannot set prefix");
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(errResponse);
            }

            Result setPrefixResult = await _databaseClass.SetPrefix(executorGuild.ID.Value, prefix);
            if (!setPrefixResult.IsSuccess)
            {
                switch (setPrefixResult.Error)
                {
                    case ArgumentOutOfRangeError:
                        _logger.LogCritical("Could not set prefix because {error}", setPrefixResult.Error);
                        errResponse = await _feedbackService.SendContextualErrorAsync("Multiple server entries found, prefix likely will fail. Contact developer.");
                        return errResponse.IsSuccess
                            ? Result.FromSuccess()
                            : Result.FromError(errResponse);
                    default:
                        _logger.LogError("Could not set prefix because {error}", setPrefixResult.Error);
                        errResponse = await _feedbackService.SendContextualErrorAsync("Could not set prefix, try again or contact developer");
                        return errResponse.IsSuccess
                            ? Result.FromSuccess()
                            : Result.FromError(errResponse);
                }
            }
            errResponse = await _feedbackService.SendContextualSuccessAsync($"Prefix set to {prefix}");
            return errResponse.IsSuccess
                ? Result.FromSuccess()
                : Result.FromError(errResponse);
        }

        [Command("get-prefix")]
        [CommandType(type: ApplicationCommandType.ChatInput)]
        [Description("Get the prefix for the bot")]
        public async Task<Result> GetPrefix()
        {
            var guildId = _context switch
            {
                InteractionContext interactionContext => interactionContext.Interaction.GuildID,
                MessageContext messageContext => messageContext.GuildID,
                _ => throw new ArgumentOutOfRangeException(nameof(_context), _context, null)
            };
            string prefix = PrefixSetter.DefaultPrefix;
            if (guildId.HasValue)
            {
                prefix = await _databaseClass.GetPrefix(guildId.Value);
            }

            string replyString = prefix.Contains(' ') ? $"Prefix is \"{prefix}\"" : $"Prefix is {prefix}";
            Result<IReadOnlyList<IMessage>> responseResult = await _feedbackService.SendContextualSuccessAsync(replyString);
            return responseResult.IsSuccess
                ? Result.FromSuccess()
                : Result.FromError(responseResult);
        }
    }

    internal class PrefixSetter : ICommandPrefixMatcher
    {
        public const     string                OverridePrefix = "&DiscordBoostRoleBot&";
        public const     string                DefaultPrefix  = "&";
        private readonly IMessageContext       _context;
        private readonly ILogger<PrefixSetter> _log;
        private readonly Database              _databaseClass;


        public PrefixSetter(IMessageContext context, ILogger<PrefixSetter> log, Database databaseClass)
        {
            _context            = context;
            _log                = log;
            _databaseClass = databaseClass;
        }

        public async ValueTask<Result<(bool Matches, int ContentStartIndex)>> MatchesPrefixAsync(string content, CancellationToken ct = new CancellationToken())
        {
            if (!_context.TryGetUserID(out Snowflake userId))
            {
                _log.LogWarning("Could not get user id from context {contextType}", _context.GetType());
                return new InvalidOperationException("Could not get user id from context");
            }
            if (userId.IsOwner() && content.StartsWith(OverridePrefix))
            {
                return Result<(bool Matches, int ContentStartIndex)>.FromSuccess((true, OverridePrefix.Length));
            }
            string prefix;
            if (!_context.TryGetGuildID(out Snowflake guildId)){
                _log.LogDebug("Could not get guild id from context {contextType}", _context.GetType());
                prefix = DefaultPrefix;
            } else
            {
                prefix = await _databaseClass.GetPrefix(guildId);
            }
            return Result<(bool Matches, int ContentStartIndex)>.FromSuccess(content.StartsWith(prefix) ? (true, prefix.Length) : (false, -1));
        }
    }
}
