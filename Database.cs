﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Rest.Core;

namespace DiscordBoostRoleBot
{
    internal class Database
    {
        private readonly ILogger<Database> _logger;
        private static ILogger<Database> _loggerStatic;
        public Database(ILogger<Database> logger)
        {
            _logger = _loggerStatic = logger;

        }
        public class ServersSettings
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
        public class RoleDataDbContext : DbContext
        {
            public DbSet<RoleData> RolesCreated { get; set; }

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
                optionsBuilder.UseSqlite("Data Source=RolesDatabase.db;")
                    // TODO: Figure out logging
                    // .LogTo(_loggerStatic.LogError)
                    .EnableSensitiveDataLogging()
                    .EnableDetailedErrors();

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.ApplyConfigurationsFromAssembly(typeof(RoleDataEntityTypeConfiguration).Assembly);
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
            await database.AddAsync(roleData);
            return await database.SaveChangesAsync() > 0;
        }

        public static async Task<int> GetRoleCount(ulong serverId, ulong userId)
        {
            await using RoleDataDbContext database = new();
            return await database.RolesCreated.CountAsync(rd => rd.RoleUserId == userId);
        }

        public static async Task<(int, ulong)> RemoveRoleFromDatabase(IRole role) => await RemoveRoleFromDatabase(role.ID);
        public static async Task<(int, ulong)> RemoveRoleFromDatabase(Snowflake roleSnowflake) => await RemoveRoleFromDatabase(roleSnowflake.Value);
        public static async Task<(int, ulong)> RemoveRoleFromDatabase(ulong roleId)
        {
            await using RoleDataDbContext database = new();
            RoleData? roleToRemove = await database.RolesCreated.Where(roleData => roleData.RoleId == roleId).FirstOrDefaultAsync();
            if (roleToRemove is null)
            {
                return (-1, 0);
            }
            database.RolesCreated.Remove(roleToRemove);
            return (await database.SaveChangesAsync(), roleToRemove.RoleUserId);
        }
    }
}