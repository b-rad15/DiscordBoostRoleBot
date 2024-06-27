using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Rest.Core;
using Remora.Results;

namespace DiscordBoostRoleBot
{
    internal class RolesRemoveService : BackgroundService
    {
        private readonly ILogger<RolesRemoveService> _logger;
        private readonly IDiscordRestGuildAPI        _guildApi;
        private readonly IConfiguration              _config;
        private readonly Database   database;

        private readonly PeriodicTimer _timer;

        private SemaphoreSlim _oneWorkerAtATime = new(1, 1);


        public RolesRemoveService(ILogger<RolesRemoveService> logger, IDiscordRestGuildAPI guildApi, IConfiguration config, Database database)
        {
            _logger          = logger;
            _guildApi        = guildApi;
            _config          = config;
            this.database    = database;
            _executeInterval = TimeSpan.FromMinutes(_config.GetValue("RemoveRoleIntervalMinutes", 5));
            _timer           = new PeriodicTimer(_executeInterval);
        }

        private readonly TimeSpan _executeInterval;
        
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!Program.IsInitialized())
            {
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
            _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            DoWork(stoppingToken);
            try
            {
                while (await _timer.WaitForNextTickAsync(stoppingToken))
                {
                    DoWork(stoppingToken);
                }
            }
            catch (OperationCanceledException e)
            {
                _logger.LogInformation(e, "Operation cancelled");
            }
        }
        private async void DoWork(CancellationToken stoppingToken)
        {
            //Continue if another worker is already running
            if (!await _oneWorkerAtATime.WaitAsync(0, stoppingToken))
                return;
            _logger.LogInformation("{className} running at: {time}", GetType().Name, DateTimeOffset.Now);
            var roleDatas = await database.GetRoles();
#if DEBUG
            var testServerValue = _config.GetValue<ulong?>("TestServerId");
            List<Snowflake> guildIds = testServerValue == null
                ? roleDatas.Select(rc => rc.ServerId).Distinct().ToList()
                : [new(testServerValue.Value, 0)];
#else
            List<Snowflake> guildIds = roleDatas.Select(rd => rd.ServerId).Distinct().ToList();
#endif
            foreach (Snowflake guildId in guildIds)
            {
                var guildInfoResult = await _guildApi.GetGuildPreviewAsync(guildId, ct: stoppingToken).ConfigureAwait(false);
                if (!guildInfoResult.IsSuccess)
                {
                    _logger.LogWarning("Could not get guild info for {guildId} because {error}", guildId.Value, guildInfoResult.Error);
                    _logger.LogInformation("Removing roles for {guildId}:", guildId.Value);
                } else
                {
                    string guildName = guildInfoResult.Entity.Name;
                    _logger.LogInformation("Removing roles for {guildId} - {guildName}:", guildId.Value, guildName);
                }
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
                    // Already deleted from db on removal
                    _logger.LogInformation("\tRemoved user {userName} - {userMention}", userRemoved.User.Value.NameAndDiscriminator(), userRemoved.User.Value.Mention());
                }
            }
            _oneWorkerAtATime.Release();
        }
    }
}
