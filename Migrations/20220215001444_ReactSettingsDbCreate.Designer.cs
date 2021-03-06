// <auto-generated />
using DiscordBoostRoleBot;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace DiscordBoostRoleBot.Migrations
{
    [DbContext(typeof(Database.DiscordDbContext))]
    [Migration("20220215001444_ReactSettingsDbCreate")]
    partial class ReactSettingsDbCreate
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "6.0.2");

            modelBuilder.Entity("DiscordBoostRoleBot.Database+MessageReactorSettings", b =>
                {
                    b.Property<ulong>("ServerId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Emotes")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<ulong>("UserIds")
                        .HasColumnType("INTEGER");

                    b.HasKey("ServerId");

                    b.ToTable("MessageReactorSettings", (string)null);
                });

            modelBuilder.Entity("DiscordBoostRoleBot.Database+RoleData", b =>
                {
                    b.Property<ulong>("RoleId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Color")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<ulong>("RoleUserId")
                        .HasColumnType("INTEGER");

                    b.Property<ulong>("ServerId")
                        .HasColumnType("INTEGER");

                    b.HasKey("RoleId");

                    b.ToTable("Roles", (string)null);
                });
#pragma warning restore 612, 618
        }
    }
}
