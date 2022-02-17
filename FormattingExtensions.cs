//
//  MentionFormatter.cs
//
//  Author:
//       Jarl Gullberg <jarl.gullberg@gmail.com>
//
//  Copyright (c) 2017 Jarl Gullberg
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System.Drawing;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Rest.Core;

namespace DiscordBoostRoleBot {

    /// <summary>
    /// Provides helper methods to mention various Discord objects.
    /// </summary>
    public static class MentionFormatter
    {
        /// <summary>
        /// Creates a mention string for a user, displaying their username.
        /// </summary>
        /// <param name="snowflake">The user Snowflake ID.</param>
        /// <returns>
        /// A user mention string.
        /// </returns>
        public static string User(this Snowflake snowflake) => $"<@{snowflake.Value}>";
        /// <summary>
        /// Creates a mention string for a user, displaying their username.
        /// </summary>
        /// <param name="user">The user</param>
        /// <returns>
        /// A user mention string.
        /// </returns>
        public static string Mention(this IUser user) => user.ID.User();
        /// <summary>
        /// Gets the User's name and Discriminator, like Discord would show it
        /// </summary>
        /// <param name="user">The user</param>
        /// <returns>
        /// A user mention string.
        /// </returns>
        public static string NameAndDiscriminator(this IUser user) => $"{user.Username}#{user.Discriminator}";
        /// <summary>
        /// Gets the User's name and Discriminator, like Discord would show it
        /// </summary>
        /// <param name="user">The user</param>
        /// <returns>
        /// A user mention string.
        /// </returns>
        public static string NameAndDiscriminator(this IGuildMember member) => member.User.Value.NameAndDiscriminator();
        /// <summary>
        /// Creates a mention string for a user, displaying their username.
        /// </summary>
        /// <param name="member">The member</param>
        /// <returns>
        /// A user mention string.
        /// </returns>
        public static string Mention(this IGuildMember member) => member.User.Value.Mention();

        /// <summary>
        /// Creates a mention string for a channel.
        /// </summary>
        /// <param name="snowflake">The channel Snowflake ID.</param>
        /// <returns>
        /// A channel mention string.
        /// </returns>
        public static string Channel(this Snowflake snowflake) => $"<#{snowflake.Value}>";
        /// <summary>
        /// Creates a mention string for a channel.
        /// </summary>
        /// <param name="channel">The channel Snowflake ID.</param>
        /// <returns>
        /// A channel mention string.
        /// </returns>
        public static string Mention(this IChannel channel) => channel.ID.Channel();

        /// <summary>
        /// Creates a mention string for a role.
        /// </summary>
        /// <param name="snowflake">The role Snowflake ID.</param>
        /// <returns>
        /// A role mention string.
        /// </returns>
        public static string Role(this Snowflake snowflake) => $"<@&{snowflake.Value}>";
        /// <summary>
        /// Creates a mention string for a role.
        /// </summary>
        /// <param name="role">The role Snowflake ID.</param>
        /// <returns>
        /// A role mention string.
        /// </returns>
        public static string Mention(this IRole role) => role.ID.Role();
    }

    public static class ChecksExtensions
    {
        public static bool IsBoosting(this IGuildMember member)
        {
            //PremiumSince parameter was returned and that value is something
            return (member.PremiumSince.HasValue && member.PremiumSince.Value is not null);
        }
        public static bool IsNotBoosting(this IGuildMember member)
        {
            //Premium since value was not returned or it is null
            return (!member.PremiumSince.HasValue || member.PremiumSince.Value is null);
        }

        public static bool IsRoleModAdminOrOwner(this IGuildMember member)
        {
            //Permissions are include in object
            return member.Permissions.HasValue
                   //and those permissions include manage roles or admin (since having admin != manage roles)
                     && (member.Permissions.Value.HasPermission(DiscordPermission.ManageRoles) || member.Permissions.Value.HasPermission(DiscordPermission.Administrator))
                   //or it's a me
                     || member.User.Value.ID.Value == Program.Config.BotOwnerId;
        }
        public static bool IsChannelModAdminOrOwner(this IGuildMember member)
        {
            //Permissions are include in object
            return member.Permissions.HasValue
                   //and those permissions include manage roles or admin (since having admin != manage roles)
                     && (member.Permissions.Value.HasPermission(DiscordPermission.ManageChannels) || member.Permissions.Value.HasPermission(DiscordPermission.Administrator))
                   //or it's a me
                     || member.User.Value.ID.Value == Program.Config.BotOwnerId;
        }
        public static bool HasPermAdminOrOwner(this IGuildMember member, params DiscordPermission[] permissions)
        {
            //Permissions are include in object
            return member.Permissions.HasValue
                   //and those permissions include manage roles or admin (since having admin != manage roles)
                   && (member.Permissions.Value.HasPermission(DiscordPermission.Administrator) || permissions.All(perm=>member.Permissions.Value.HasPermission(perm)))
                   //or it's a me
                   || member.User.Value.ID.Value == Program.Config.BotOwnerId;
        }
    }
    public static class OtherExtensions
    {
        public static string ToHex(this byte[] bytes)
        {
            char[] c = new char[bytes.Length * 2];

            byte b;

            for (int bx = 0, cx = 0; bx < bytes.Length; ++bx, ++cx)
            {
                b = ((byte)(bytes[bx] >> 4));
                c[cx] = (char)(b > 9 ? b + 0x37 + 0x20 : b + 0x30);

                b = ((byte)(bytes[bx] & 0x0F));
                c[++cx] = (char)(b > 9 ? b + 0x37 + 0x20 : b + 0x30);
            }

            return new string(value: c);
        }
        public static string ToHashRGB(this Color color)
        {
            byte[] bytes = { color.R, color.G, color.B };
            return $"#{bytes.ToHex()}";
        }
    }
}