using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Rest.Core;
using Remora.Results;
using SQLitePCL;
using Remora.Discord;

namespace DiscordBoostRoleBot
{
    internal class Database
    {
        private readonly ILogger<Database> _logger;

        public Database(ILogger<Database> logger)
        {
            _logger = logger;

        }
        public class RoleData
        {
            public int EntryId { get; set; }
            public ulong RoleId { get; set; }
            public ulong ServerId { get; set; }
            public ulong RoleUserId { get; set; }
            public string Color { get; set; }
            public string Name { get; set; }
            public string? ImageUrl { get; set; }
            public string? ImageHash { get; set; }
        }
        public class RoleDataEntityTypeConfiguration : IEntityTypeConfiguration<RoleData>
        {
            public void Configure(EntityTypeBuilder<RoleData> builder)
            {
                //Property Specific stuff
                builder.Property(cl => cl.EntryId).IsRequired().ValueGeneratedOnAdd();
                builder.Property(cl => cl.ServerId).IsRequired();
                builder.Property(cl => cl.RoleId).IsRequired();
                builder.Property(cl => cl.RoleUserId).IsRequired();
                builder.Property(cl => cl.Color).IsRequired();
                builder.Property(cl => cl.ImageUrl);
                builder.Property(cl => cl.ImageHash);
                //Table Stuff
                builder.ToTable("Roles");
                builder.HasKey(cl => cl.EntryId);
            }
        }
        public class MessageReactorSettings
        {
            public ulong ServerId;
            public ulong ChannelId;
            public ulong UserIds;
            public string Emotes;
        }
        public class MessageReactorSettingsEntityTypeConfiguration : IEntityTypeConfiguration<MessageReactorSettings>
        {
            public void Configure(EntityTypeBuilder<MessageReactorSettings> builder)
            {
                //Property Specific stuff
                builder.Property(cl => cl.ServerId).IsRequired();
                builder.Property(cl => cl.UserIds).IsRequired();
                builder.Property(cl => cl.Emotes).IsRequired();
                builder.Property(cl => cl.ChannelId).IsRequired();
                //Table Stuff
                builder.ToTable("MessageReactorSettings");
                builder.HasKey(cl => cl.ServerId);
            }
        }
        public class ServerSettings
        {
            public ulong ServerId;
            public string Prefix = "&";
            public List<Snowflake> AllowedRolesSnowflakes { get; set; } = null!;
        }
        public class ServerSettingsEntityTypeConfiguration : IEntityTypeConfiguration<ServerSettings>
        {
            public void Configure(EntityTypeBuilder<ServerSettings> builder)
            {
                //Property Specific stuff
                builder.Property(cl => cl.ServerId).IsRequired();
                builder.Property(cl => cl.Prefix).IsRequired().HasDefaultValue("&");
                builder.Property(cl => cl.AllowedRolesSnowflakes).IsRequired();
                //Table Stuff
                builder.ToTable("ServerSettings");
                builder.HasKey(cl => cl.ServerId);
            }
        }
        public class DiscordDbContext : DbContext
        {
            public DbSet<RoleData> RolesCreated { get; set; }
            public DbSet<MessageReactorSettings> MessageReactorSettings { get; set; }
            public DbSet<ServerSettings> ServerwideSettings { get; set; }

            private readonly ILoggerFactory _loggerFactory = Program.LogFactory;

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
                optionsBuilder.UseSqlite("Data Source=RolesDatabase.db;")
                    // TODO: Figure out logging
                    // .LogTo(_loggerStatic.LogError)
                    .UseLoggerFactory(_loggerFactory)
                    .ConfigureWarnings(b=>b.Log(
                        (RelationalEventId.ConnectionOpening, LogLevel.Trace),
                        (RelationalEventId.ConnectionOpened, LogLevel.Trace),
                        (RelationalEventId.CommandCreating, LogLevel.Trace),
                        (CoreEventId.ContextInitialized, LogLevel.Trace),
                        (RelationalEventId.CommandExecuted, LogLevel.Debug),
                        (RelationalEventId.ConnectionClosed, LogLevel.Trace)))
                    .EnableSensitiveDataLogging()
                    .EnableDetailedErrors();

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.ApplyConfigurationsFromAssembly(typeof(RoleDataEntityTypeConfiguration).Assembly);
                modelBuilder.ApplyConfigurationsFromAssembly(typeof(MessageReactorSettingsEntityTypeConfiguration).Assembly);
                modelBuilder.ApplyConfigurationsFromAssembly(typeof(ServerSettingsEntityTypeConfiguration).Assembly);
                const char separatorChar = '|';
                var snowflakeListConverter = new ValueConverter<List<Snowflake>, string>(snowflakesList => string.Join(separatorChar, snowflakesList),
                    snowflakesString => snowflakesString.Split(separatorChar, StringSplitOptions.RemoveEmptyEntries)
                        .Select(individualSnowflakeString => new Snowflake(Convert.ToUInt64(individualSnowflakeString), 0)).ToList());
                var snowflakeListValueComparer = new ValueComparer<List<Snowflake>>(
                    (c1, c2) => new HashSet<Snowflake>(c1!).SetEquals(new HashSet<Snowflake>(c2!)),
                    c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                    c => c.ToList());
                modelBuilder.Entity<ServerSettings>()
                    .Property(nameof(ServerSettings.AllowedRolesSnowflakes))
                    .HasConversion(snowflakeListConverter, snowflakeListValueComparer);
            }

        }
        public static async Task<bool> AddRoleToDatabase(ulong serverId, ulong userId, ulong roleId, string color, string name, string? imageUrl = null, string? imageHash = null)
        {
            RoleData roleData = new()
            {
                ServerId = serverId,
                RoleUserId = userId,
                RoleId = roleId,
                Color = color,
                Name = name,
                ImageUrl = imageUrl,
                ImageHash = imageHash
            };
            await using DiscordDbContext database = new();
            database.Add(roleData);
            return await database.SaveChangesAsync().ConfigureAwait(false) > 0;
        }
        public static async Task<int> GetRoleCount(ulong serverId, ulong userId)
        {
            await using DiscordDbContext database = new();
            var rolesCount = await database.RolesCreated.CountAsync(rd => rd.ServerId == serverId && rd.RoleUserId == userId).ConfigureAwait(false);
            return rolesCount;
        }
        public static async Task<(int, ulong)> RemoveRoleFromDatabase(IRole role) => await RemoveRoleFromDatabase(role.ID).ConfigureAwait(false);
        public static async Task<(int, ulong)> RemoveRoleFromDatabase(Snowflake roleSnowflake) => await RemoveRoleFromDatabase(roleSnowflake.Value).ConfigureAwait(false);
        public static async Task<(int, ulong)> RemoveRoleFromDatabase(ulong roleId)
        {
            await using DiscordDbContext database = new();
            RoleData? roleToRemove = await database.RolesCreated.Where(roleData => roleData.RoleId == roleId).FirstOrDefaultAsync().ConfigureAwait(false);
            if (roleToRemove is null)
            {
                return (-1, 0);
            }
            database.RolesCreated.Remove(roleToRemove);
            return (await database.SaveChangesAsync().ConfigureAwait(false), roleToRemove.RoleUserId);
        }

        public static async Task<string> GetPrefix(Snowflake guildId) => await GetPrefix(guildId.Value).ConfigureAwait(false);
        public static async Task<string> GetPrefix(ulong guildId)
        {
            await using DiscordDbContext database = new();
            string? prefix = await database.ServerwideSettings.Where(ss => ss.ServerId == guildId).Select(ss=>ss.Prefix).AsNoTracking().FirstOrDefaultAsync().ConfigureAwait(false);
            return prefix ?? PrefixSetter.DefaultPrefix;
        }
        public static async Task<Result> SetPrefix(Snowflake guildId, string prefix) => await SetPrefix(guildId.Value, prefix).ConfigureAwait(false);
        public static async Task<Result> SetPrefix(ulong guildId, string prefix)
        {
            await using DiscordDbContext database = new();
            ServerSettings? serverSettings = await database.ServerwideSettings.Where(ss => ss.ServerId == guildId).FirstOrDefaultAsync().ConfigureAwait(false);
            if (serverSettings is null)
            {
                serverSettings = new()
                {
                    Prefix = prefix,
                    ServerId = guildId
                };
                database.Add(serverSettings);
            }
            else
            {
                serverSettings.ServerId = guildId;
            }
            int numRows = await database.SaveChangesAsync().ConfigureAwait(false);
            return numRows > 1 
                ? Result.FromSuccess() 
                : Result.FromError<string>($"Failed to save database, {numRows} rows updated");
        }
    }
}
