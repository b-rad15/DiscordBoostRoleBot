using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Rest.Core;
using Remora.Results;

namespace DiscordBoostRoleBot
{
    internal class UserChecking
    {
        private static readonly IDiscordRestGuildAPI _restGuildAPI;
        internal static async Task<bool> IsBoosting(IUser user, IGuild guild) => await IsBoosting(userSnowflake: user.ID, serverSnowflake: guild.ID).ConfigureAwait(false);

        internal static async Task<bool> IsBoosting(Snowflake userSnowflake, Snowflake serverSnowflake)
        {
            Result<IGuildMember> result = await _restGuildAPI.GetGuildMemberAsync(guildID: serverSnowflake, userID: userSnowflake).ConfigureAwait(false);
            return result.IsSuccess;
        }
    }
}
