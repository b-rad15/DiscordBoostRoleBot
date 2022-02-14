using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Rest.Core;

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
                await using Database.RoleDataDbContext database = new();
                var guildIds = await database.RolesCreated.Select(rc => new Snowflake(rc.ServerId, 0)).Distinct().ToListAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                foreach (Snowflake guildId in guildIds)
                {
                    guildLoopStart:
                    _logger.LogDebug("{guildId}:", guildId);
                    var removeBoosterResult = await Program.RemoveNonBoosterRoles(guildId);
                    if (!removeBoosterResult.IsSuccess)
                    {
                        if (removeBoosterResult.Error.Message.Contains("inner"))
                        {
                            _logger.LogError("Failed {server} because {error}", guildId.Value, removeBoosterResult.Inner!.Error);
                        }
                        _logger.LogError("Could not remove booster role for server {server} because {error}", guildId.Value, removeBoosterResult.Error.Message);
                        continue;
                    }

                    var usersRemoved = removeBoosterResult.Entity;
                    if (usersRemoved.Count == 0)
                    {
                        _logger.LogDebug("None removed");
                        continue;
                    }
                    foreach (Snowflake userRemoved in usersRemoved)
                    {
                        await database.RolesCreated.Where(rc => rc.RoleUserId == userRemoved.Value).DeleteFromQueryAsync(cancellationToken: stoppingToken);
                        _logger.LogDebug("\tRemoved user {userMention}", userRemoved.User());
                    }
                }
                await waitTimer;
            }
        }
    }
}
