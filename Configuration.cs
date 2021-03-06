using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordBoostRoleBot
{
    internal class Configuration
    {
        public string Token { get; set; } = null!;
        public ulong? TestServerId { get; set; }
        public ulong? BotOwnerId { get; set; }
        public double? RemoveRoleIntervalMinutes { get; set; }

        public static Configuration ReadConfig(string configPath = "config.json")
        {
            string jsonString = File.ReadAllText(path: configPath);
            JsonSerializerOptions? options = new()
            {
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                WriteIndented = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };
            Configuration configuration = JsonSerializer.Deserialize<Configuration>(json: jsonString, options: options) ?? throw new InvalidOperationException("Configuration cannot be null");
            return configuration;
        }
    }
}
