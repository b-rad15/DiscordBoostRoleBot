using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        private readonly TimeSpan _executeInterval = TimeSpan.FromMinutes(30);
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
                    Result<List<Snowflake>> removeBoosterResult = await Program.RemoveNonBoosterRoles(guildId, stoppingToken).ConfigureAwait(false);
                    if (!removeBoosterResult.IsSuccess)
                    {
                        if (removeBoosterResult.Error.Message.Contains("inner"))
                        {
                            _logger.LogError("Failed {server} because {error}", guildId.Value, removeBoosterResult.Inner!.Error);
                        }
                        _logger.LogError("Could not remove booster role for server {server} because {error}", guildId.Value, removeBoosterResult.Error.Message);
                        continue;
                    }

                    List<Snowflake> usersRemoved = removeBoosterResult.Entity;
                    if (usersRemoved.Count == 0)
                    {
                        _logger.LogDebug("None removed");
                        continue;
                    }
                    foreach (Snowflake userRemoved in usersRemoved)
                    {
                        // Already deleted on removal
                        // await database.RolesCreated.Where(rc => rc.RoleUserId == userRemoved.Value).DeleteAsync(stoppingToken);
                        _logger.LogInformation("\tRemoved user {userMention}", userRemoved.User());
                    }
                }
                await waitTimer;
            }
        }
    }
}
