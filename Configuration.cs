using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DiscordBoostRoleBot
{
    internal class Configuration
    {
        public string Token { get; set; } = null!;

        public static Configuration ReadConfig(string configPath = "config.json")
        {
            string jsonString = File.ReadAllText(configPath);
            JsonSerializerOptions? options = new()
            {
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                WriteIndented = true
            };
            Configuration configuration = JsonSerializer.Deserialize<Configuration>(jsonString, options) ?? throw new InvalidOperationException("Configuration cannot be null");
            return configuration;
        }
    }
}
