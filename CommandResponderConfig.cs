using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remora.Commands.Attributes;
using Remora.Commands.Groups;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.Commands.Attributes;
using Remora.Discord.Commands.Conditions;
using Remora.Discord.Commands.Contexts;
using Remora.Discord.Commands.Feedback.Messages;
using Remora.Discord.Commands.Feedback.Services;
using Remora.Discord.Commands.Services;
using Remora.Rest.Core;
using Remora.Results;

namespace DiscordBoostRoleBot
{
    internal class CommandResponderConfigCommands : CommandGroup
    {

        private readonly ICommandContext _context;
        private readonly FeedbackService _feedbackService;
        private readonly IDiscordRestGuildAPI _guildApi;
        private readonly ILogger<CommandResponderConfigCommands> _logger;
        public CommandResponderConfigCommands(ICommandContext context, FeedbackService feedbackService, IDiscordRestGuildAPI guildApi, ILogger<CommandResponderConfigCommands> logger)
        {
            _context = context;
            _feedbackService = feedbackService;
            _guildApi = guildApi;
            _logger = logger;
        }

        [RequireContext(ChannelContext.Guild)]
        [Command("set-prefix")]
        [CommandType(type: ApplicationCommandType.ChatInput)]
        [Description("Set the prefix for the bot")]
        public async Task<Result> SetPrefix([Description("Command Prefix to set, can be any string")] string prefix)
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

            if (!executorGuildMember.Permissions.HasValue)
            {
                Result<IGuildMember> getPermsResult = await Program.AddGuildMemberPermissions(executorGuildMember, _context.GuildID.Value);
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

            if (!_context.GuildID.HasValue)
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("You are not in a server, cannot set prefix");
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(errResponse);
            }

            Result setPrefixResult = await Database.SetPrefix(_context.GuildID.Value, prefix);
            if (!setPrefixResult.IsSuccess)
            {
                errResponse = await _feedbackService.SendContextualErrorAsync("Failed to set prefix");
                return errResponse.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(errResponse);
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
            string prefix = PrefixSetter.DefaultPrefix;
            if (_context.GuildID.HasValue)
            {
                prefix = await Database.GetPrefix(_context.GuildID.Value);
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
        public const string OverridePrefix = "&DiscordBoostRoleBot&";
        public const string DefaultPrefix = "&";
        private readonly ICommandContext _context;

        public PrefixSetter(ICommandContext context)
        {
            _context = context;
        }

        public async ValueTask<Result<(bool Matches, int ContentStartIndex)>> MatchesPrefixAsync(string content, CancellationToken ct = new CancellationToken())
        {
            if (_context.User.IsOwner() && content.StartsWith(OverridePrefix))
            {
                return Result<(bool Matches, int ContentStartIndex)>.FromSuccess((true, OverridePrefix.Length));
            }

            string prefix;
            if(_context.GuildID.HasValue)
            {
                prefix = await Database.GetPrefix(_context.GuildID.Value);
            }
            else
            {
                prefix = DefaultPrefix;
            }

            return Result<(bool Matches, int ContentStartIndex)>.FromSuccess(content.StartsWith(prefix) ? (true, prefix.Length) : (false, -1));
        }
    }
}
