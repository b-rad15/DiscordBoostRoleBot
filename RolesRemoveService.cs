using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Rest.Core;
using Remora.Results;
using Z.EntityFramework.Plus;

namespace DiscordBoostRoleBot
{
    internal class RolesRemoveService : BackgroundService
    {
        private readonly ILogger<RolesRemoveService> _logger;

        public RolesRemoveService(ILogger<RolesRemoveService> logger)
        {
            _logger = logger;
        }

        private readonly TimeSpan _executeInterval = TimeSpan.FromMinutes(Program.Config.RemoveRoleIntervalMinutes.HasValue ? Program.Config.RemoveRoleIntervalMinutes.Value : 5);
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!Program.IsInitialized())
            {
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                ConfiguredTaskAwaitable waitTimer = Task.Delay(_executeInterval, stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("{className} running at: {time}", GetType().Name, DateTimeOffset.Now);
                await using Database.DiscordDbContext database = new();
                List<Snowflake> guildIds = await database.RolesCreated
#if DEBUG
                    .Where(rc=> Program.Config.TestServerId == null || rc.ServerId == Program.Config.TestServerId)
#endif
                    .Select(rc => new Snowflake(rc.ServerId, 0)).Distinct().ToListAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                foreach (Snowflake guildId in guildIds)
                {
                    _logger.LogInformation("{guildId}:", guildId);
                    Result<List<IGuildMember>> removeBoosterResult = await Program.RemoveNonBoosterRoles(guildId, stoppingToken).ConfigureAwait(false);
                    if (!removeBoosterResult.IsSuccess)
                    {
                        if (removeBoosterResult.Error.Message.Contains("inner"))
                        {
                            _logger.LogWarning("Failed {server} because {error}", guildId.Value, removeBoosterResult.Inner?.Error);
                        }
                        _logger.LogWarning("Could not remove booster role for server {server} because {error}", guildId.Value, removeBoosterResult.Error);
                        continue;
                    }

                    List<IGuildMember> usersRemoved = removeBoosterResult.Entity;
                    if (usersRemoved.Count == 0)
                    {
                        _logger.LogInformation("\tNone removed");
                        continue;
                    }
                    foreach (IGuildMember userRemoved in usersRemoved)
                    {
                        // Already deleted on removal
                        // await database.RolesCreated.Where(rc => rc.RoleUserId == userRemoved.Value).DeleteAsync(stoppingToken);
                        _logger.LogInformation("\tRemoved user {userName} - {userMention}", userRemoved.User.Value.NameAndDiscriminator(), userRemoved.User.Value.Mention());
                    }
                }
                await waitTimer;
            }
        }
    }
}
