using Microsoft.Extensions.Logging;
using Remora.Discord.API.Abstractions.Gateway.Events;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.Commands.Contexts;
using Remora.Discord.Commands.Services;
using Remora.Discord.Gateway.Responders;
using Remora.Results;

namespace DiscordBoostRoleBot
{
    internal class BasicInteractionResponder(ILogger<BasicInteractionResponder> log) : IResponder<IInteractionCreate>
    {
        public Task<Result> RespondAsync(IInteractionCreate gatewayEvent, CancellationToken ct = new CancellationToken())
        {
            log.LogDebug("Received interaction {InteractionType}", gatewayEvent.Type);
            if (!gatewayEvent.Data.HasValue)
            {
                log.LogDebug("Received interaction with no data");
            }
            if (gatewayEvent.Data.Value.IsT0)
            {
                IApplicationCommandData slashCommandInteractionData = gatewayEvent.Data.Value.AsT0;
                log.LogDebug("Received slash command {CommandName}", slashCommandInteractionData.Name);
                return Task.FromResult(Result.FromSuccess());
            } else if (gatewayEvent.Data.Value.IsT1)
            {
                IMessageComponentData componentInteractionData = gatewayEvent.Data.Value.AsT1;
                log.LogDebug("Received component interaction {CustomID}", componentInteractionData.CustomID);
                return Task.FromResult(Result.FromSuccess());
            } else if (gatewayEvent.Data.Value.IsT2)
            {
                IModalSubmitData modalSubmitInteractionData = gatewayEvent.Data.Value.AsT2;
                log.LogDebug("Received modal submit {CustomID}", modalSubmitInteractionData.CustomID);
                return Task.FromResult(Result.FromSuccess());
            } else
            {
                log.LogError("Received unknown interaction type {InteractionType}", gatewayEvent.Type);
                return Task.FromResult(Result.FromSuccess());
            }   
        }
    }

    internal class PreparationErrorEventResponder(ILogger<PreparationErrorEventResponder> log) : IPreparationErrorEvent
    {
        public Task<Result> PreparationFailed(IOperationContext context, IResult preparationResult,
            CancellationToken                                   ct = new CancellationToken())
        {
            log.LogCritical("Preparation failed for {Context} with {PreparationResult}", context, preparationResult);
            return Task.FromResult(Result.FromSuccess());
        }
    }

    internal class PostExecutionErrorResponder(ILogger<PostExecutionErrorResponder> log)
        : IPostExecutionEvent
    {
        public Task<Result> AfterExecutionAsync(ICommandContext context, IResult commandResult,
            CancellationToken                                   ct = new CancellationToken())
        {
            if (commandResult.IsSuccess)
            {
                return Task.FromResult(Result.FromSuccess());
            }

            log.LogCritical("Command failed for {Context} with {CommandResult}", context, commandResult);
            return Task.FromResult(Result.FromSuccess());
        }
    }
}
