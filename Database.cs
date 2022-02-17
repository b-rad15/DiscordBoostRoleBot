using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Rest.Core;
using SQLitePCL;

namespace DiscordBoostRoleBot
{
    internal class Database
    {
        private readonly ILogger<Database> _logger;

        public Database(ILogger<Database> logger)
        {
            _logger = logger;

        }
        public class RolesServersSettings
        {
            public ulong ServerId { get; set; }
            //Whether to remove roles after boosts run out
            public bool ShouldRemoveRoles { get; set; }
        }


        public class RoleData
        {
            public ulong RoleId { get; set; }
            public ulong ServerId { get; set; }
            public ulong RoleUserId { get; set; }
            public string Color { get; set; }
            public string Name { get; set; }
        }
        public class RoleDataEntityTypeConfiguration : IEntityTypeConfiguration<RoleData>
        {
            public void Configure(EntityTypeBuilder<RoleData> builder)
            {
                //Property Specific stuff
                builder.Property(cl => cl.ServerId).IsRequired();
                builder.Property(cl => cl.RoleId).IsRequired();
                builder.Property(cl => cl.RoleUserId).IsRequired();
                builder.Property(cl => cl.Color).IsRequired();
                //Table Stuff
                builder.ToTable("Roles");
                builder.HasKey(cl => cl.RoleId );
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
        public class RoleDataDbContext : DbContext
        {
            public DbSet<RoleData> RolesCreated { get; set; }
            public DbSet<MessageReactorSettings> MessageReactorSettings { get; set; }

            private readonly ILoggerFactory _loggerFactory = Program.LogFactory;

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
                optionsBuilder.UseSqlite("Data Source=RolesDatabase.db;")
                    // TODO: Figure out logging
                    // .LogTo(_loggerStatic.LogError)
                    // .ConfigureWarnings(b=>b.Log(
                    //     (RelationalEventId.ConnectionOpened, LogLevel.Information),
                    //     (RelationalEventId.ConnectionClosed, LogLevel.Information)))
                    .UseLoggerFactory(_loggerFactory)
                    .EnableSensitiveDataLogging()
                    .EnableDetailedErrors();

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.ApplyConfigurationsFromAssembly(typeof(RoleDataEntityTypeConfiguration).Assembly);
                modelBuilder.ApplyConfigurationsFromAssembly(typeof(MessageReactorSettingsEntityTypeConfiguration).Assembly);
            }

        }

        public static async Task<bool> AddRoleToDatabase(ulong serverId, ulong userId, ulong roleId, string color, string name)
        {
            RoleData roleData = new()
            {
                ServerId = serverId,
                RoleUserId = userId,
                RoleId = roleId,
                Color = color,
                Name = name
            };
            await using RoleDataDbContext database = new();
            database.Add(roleData);
            return await database.SaveChangesAsync().ConfigureAwait(false) > 0;
        }

        public static async Task<int> GetRoleCount(ulong serverId, ulong userId)
        {
            await using RoleDataDbContext database = new();
            return await database.RolesCreated.CountAsync(rd => rd.RoleUserId == userId).ConfigureAwait(false);
        }

        public static async Task<(int, ulong)> RemoveRoleFromDatabase(IRole role) => await RemoveRoleFromDatabase(role.ID).ConfigureAwait(false);
        public static async Task<(int, ulong)> RemoveRoleFromDatabase(Snowflake roleSnowflake) => await RemoveRoleFromDatabase(roleSnowflake.Value).ConfigureAwait(false);
        public static async Task<(int, ulong)> RemoveRoleFromDatabase(ulong roleId)
        {
            await using RoleDataDbContext database = new();
            RoleData? roleToRemove = await database.RolesCreated.Where(roleData => roleData.RoleId == roleId).FirstOrDefaultAsync().ConfigureAwait(false);
            if (roleToRemove is null)
            {
                return (-1, 0);
            }
            database.RolesCreated.Remove(roleToRemove);
            return (await database.SaveChangesAsync().ConfigureAwait(false), roleToRemove.RoleUserId);
        }
    }
}
