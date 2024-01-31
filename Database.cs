using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Rest.Core;
using Remora.Results;

namespace DiscordBoostRoleBot
{
    public class MongoDbSettings
    {
        public string ConnectionString { get; set; } = null!;
        public string DatabaseName { get; set; } = null!;
        public string RolesCollectionName { get; set; } = null!;
        public string MessageReactorSettingsCollectionName { get; set; } = null!;
        public string ServerSettingsCollectionName { get; set; } = null!;
    }
    public class Database
    {
        private readonly ILogger<Database>          _logger;
        private readonly IMongoCollection<RoleData> _rolesCollection;
        private readonly IMongoCollection<MessageReactorSettings> _messageReactorSettingsCollection;
        private readonly IMongoCollection<ServerSettings> _serverSettingsCollection;

        public Database(ILogger<Database> logger, IOptions<MongoDbSettings> mongoDbSettings)
        {
            _logger       = logger;
            var             client   = new MongoClient(mongoDbSettings.Value.ConnectionString);
            IMongoDatabase? database = client.GetDatabase(mongoDbSettings.Value.DatabaseName);
            // Check that roles collection exists, if not, create it
            if (!database.ListCollectionNames().ToList().Contains(mongoDbSettings.Value.RolesCollectionName))
            {
                database.CreateCollection(mongoDbSettings.Value.RolesCollectionName);

            }
            _rolesCollection = database.GetCollection<RoleData>(mongoDbSettings.Value.RolesCollectionName);
            // Check that RoleId is an index
            var rolesCollectionIndexes = _rolesCollection.Indexes.List().ToList();
            if (rolesCollectionIndexes.All(index => index["name"] != "role_id"))
            {
                _rolesCollection.Indexes.CreateOne(new CreateIndexModel<RoleData>(Builders<RoleData>.IndexKeys.Ascending(roleData => roleData.RoleId)));
            }
            // Check that message reactor settings collection exists, if not, create it
            if (!database.ListCollectionNames().ToList()
                         .Contains(mongoDbSettings.Value.MessageReactorSettingsCollectionName))
            {
                database.CreateCollection(mongoDbSettings.Value.MessageReactorSettingsCollectionName);
            }
            _messageReactorSettingsCollection = database.GetCollection<MessageReactorSettings>(mongoDbSettings.Value.MessageReactorSettingsCollectionName);
            // Check that ServerId is an index
            var messageReactorSettingsCollectionIndexes = _messageReactorSettingsCollection.Indexes.List().ToList();
            if (messageReactorSettingsCollectionIndexes.All(index => index["name"] != "server_id"))
            {
                _messageReactorSettingsCollection.Indexes.CreateOne(new CreateIndexModel<MessageReactorSettings>(Builders<MessageReactorSettings>.IndexKeys.Ascending(messageReactorSettings => messageReactorSettings.ServerId)));
            }
            // Check that server settings collection exists, if not, create it
            if (!database.ListCollectionNames().ToList()
                         .Contains(mongoDbSettings.Value.ServerSettingsCollectionName))
            {
                database.CreateCollection(mongoDbSettings.Value.ServerSettingsCollectionName);
            }
            _serverSettingsCollection = database.GetCollection<ServerSettings>(mongoDbSettings.Value.ServerSettingsCollectionName);
            // Check that ServerId is an index
            var serverSettingsCollectionIndexes = _serverSettingsCollection.Indexes.List().ToList();
            if (serverSettingsCollectionIndexes.All(index => index["name"] != "server_id"))
            {
                _serverSettingsCollection.Indexes.CreateOne(new CreateIndexModel<ServerSettings>(Builders<ServerSettings>.IndexKeys.Ascending(serverSettings => serverSettings.ServerId)));
            }
        }
        public class RoleData
        {
            public Snowflake RoleId     { get; set; }
            public Snowflake ServerId   { get; set; }
            public Snowflake RoleUserId { get; set; }
            public string    Color      { get; set; }
            public string    Name       { get; set; }
            public string?   ImageUrl   { get; set; }
            public string?   ImageHash  { get; set; }
        }
        public class MessageReactorSettings
        {
            public Snowflake ServerId;
            public Snowflake ChannelId;
            public Snowflake UserIds;
            public string Emotes;
        }
        public class ServerSettings
        {
            public Snowflake ServerId;
            public string Prefix = "&";
            public List<Snowflake> AllowedRolesSnowflakes { get; set; } = null!;
        }
        #region RolesDatabase
        public async Task<bool> AddRoleToDatabase(Snowflake serverId, Snowflake userId, Snowflake roleId, string color, string name, string? imageUrl = null, string? imageHash = null)
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
            return await AddRoleToDatabase(roleData);
        }

        public async Task<bool> AddRoleToDatabase(RoleData roleData)
        {
            await _rolesCollection.InsertOneAsync(roleData);
            return true;
        }
        public async Task<long> UpdateRoleInDatabase(RoleData roleData)
        {
            var res = await _rolesCollection.ReplaceOneAsync(r => r.RoleId == roleData.RoleId, roleData);
            return res.ModifiedCount;
        }
        public async Task<long> GetRoleCount(Snowflake serverId, Snowflake userId)
        {
            FilterDefinitionBuilder<RoleData>? documentFilter = Builders<RoleData>.Filter;
            FilterDefinition<RoleData>?        serverIdFilter = documentFilter.Eq(roleData => roleData.ServerId,   serverId);
            FilterDefinition<RoleData>?        userIdFilter   = documentFilter.Eq(roleData => roleData.RoleUserId, userId);
            FilterDefinition<RoleData>?        filter         = documentFilter.And(serverIdFilter, userIdFilter);
            long                                count          = await _rolesCollection.CountDocumentsAsync(filter);
            return count;
        }

        public async Task<List<RoleData>> GetRoles(Snowflake? guildId = null, Snowflake? userId = null, Snowflake? roleId = null)
        {
            FilterDefinitionBuilder<RoleData>? documentFilter = Builders<RoleData>.Filter;
            FilterDefinition<RoleData>?        serverIdFilter = guildId.HasValue ? documentFilter.Eq(roleData => roleData.ServerId,  guildId.Value) : documentFilter.Empty;
            FilterDefinition<RoleData>?        userIdFilter   = userId.HasValue ? documentFilter.Eq(roleData => roleData.RoleUserId, userId.Value) : documentFilter.Empty;
            FilterDefinition<RoleData>?        roleIdFilter   = roleId.HasValue ? documentFilter.Eq(roleData => roleData.RoleId,     roleId.Value) : documentFilter.Empty;
            FilterDefinition<RoleData>?        filter         = documentFilter.And(serverIdFilter, userIdFilter, roleIdFilter);
            IFindFluent<RoleData, RoleData>    findFluent     = _rolesCollection.Find(filter);
            // Render filter in a way to input into mongo shell
            string filterString = filter.Render(BsonSerializer.SerializerRegistry.GetSerializer<RoleData>(), BsonSerializer.SerializerRegistry).ToJson();
            _logger.LogDebug("Filter: {filter:l}", filterString.Replace(@"\""", @""""));
            List<RoleData>?                    roles          = await findFluent.ToListAsync();
            return roles;
        }
        public async Task<long> RemoveRoleFromDatabase(Snowflake guildId, IRole role) => await RemoveRoleFromDatabase(guildId, role.ID).ConfigureAwait(false);
        public async Task<long> RemoveRoleFromDatabase(Snowflake guildId, Snowflake roleId)
        {
            FilterDefinitionBuilder<RoleData>? documentFilter = Builders<RoleData>.Filter;
            FilterDefinition<RoleData>?        roleIdFilter   = documentFilter.Eq(roleData => roleData.RoleId, roleId);
            FilterDefinition<RoleData>?        serverIdFilter = documentFilter.Eq(roleData => roleData.ServerId, guildId);
            FilterDefinition<RoleData>?        filter         = documentFilter.And(serverIdFilter, roleIdFilter);
            DeleteResult?                      deleteResult   = await _rolesCollection.DeleteOneAsync(filter);
            if (deleteResult.DeletedCount == 0)
            {
                return -1;
            }
            return deleteResult.DeletedCount;
        }
        #endregion

        #region ServerSettingsDatabase
        public async Task<string> GetPrefix(Snowflake guildId, string? defaultPrefix = null)
        {
            FilterDefinitionBuilder<ServerSettings>? documentFilter = Builders<ServerSettings>.Filter;
            FilterDefinition<ServerSettings>?        serverIdFilter = documentFilter.Eq(serverSettings => serverSettings.ServerId, guildId);
            FilterDefinition<ServerSettings>?        filter         = serverIdFilter;
            // Grab only the prefix element
            ProjectionDefinitionBuilder<ServerSettings>?  documentProjection = Builders<ServerSettings>.Projection;
            ProjectionDefinition<ServerSettings, string>? projection         = documentProjection.Expression(serverSettings => serverSettings.Prefix);
            string?                                           prefix             = await _serverSettingsCollection.Find(filter).Project(projection).FirstOrDefaultAsync();
            // If prefix is null, set it to defaultPrefix, if defaultPrefix is null, set it to "&"
            prefix ??= defaultPrefix ?? "&";
            return prefix;

        }
        public async Task<Result> SetPrefix(Snowflake guildId, string prefix)
        {
            FilterDefinitionBuilder<ServerSettings>? documentFilter = Builders<ServerSettings>.Filter;
            FilterDefinition<ServerSettings>?        serverIdFilter = documentFilter.Eq(serverSettings => serverSettings.ServerId, guildId);
            FilterDefinition<ServerSettings>?        filter         = serverIdFilter;
            UpdateDefinition<ServerSettings>?        update         = Builders<ServerSettings>.Update.Set(serverSettings => serverSettings.Prefix, prefix);
            UpdateResult?                                      updateResult   = await _serverSettingsCollection.UpdateOneAsync(filter, update);
            switch (updateResult.ModifiedCount)
            {
                case 0:
                    ServerSettings serverSettings = new()
                    {
                        Prefix = prefix,
                        ServerId = guildId
                    };
                    await _serverSettingsCollection.InsertOneAsync(serverSettings);
                    return Result.FromSuccess();
                case 1:
                    return Result.FromSuccess();
                default:
                    return new ArgumentOutOfRangeError(nameof(updateResult.ModifiedCount),$"Updated {updateResult.ModifiedCount} rows for server {guildId}. There should not be multiple rows with the same guildId");
            }
        }
        public async Task<List<Snowflake>> GetAllowedRoles(Snowflake guildId)
        {
            FilterDefinitionBuilder<ServerSettings>? documentFilter = Builders<ServerSettings>.Filter;
            FilterDefinition<ServerSettings>?        serverIdFilter = documentFilter.Eq(serverSettings => serverSettings.ServerId, guildId);
            FilterDefinition<ServerSettings>?        filter         = serverIdFilter;
            // Grab only the allowed roles list
            ProjectionDefinitionBuilder<ServerSettings>?           documentProjection = Builders<ServerSettings>.Projection;
            ProjectionDefinition<ServerSettings, List<Snowflake>>? projection         = documentProjection.Expression(serverSettings => serverSettings.AllowedRolesSnowflakes);
            List<Snowflake>?                                       allowedRoles       = await _serverSettingsCollection.Find(filter).Project(projection).FirstOrDefaultAsync();
            // If prefix is null, set it to defaultPrefix, if defaultPrefix is null, set it to "&"
            allowedRoles ??= [];
            return allowedRoles;
        }
        public async Task<ServerSettings?> GetServerSettings(Snowflake guildId)
        {
            FilterDefinitionBuilder<ServerSettings>? documentFilter = Builders<ServerSettings>.Filter;
            FilterDefinition<ServerSettings>?        serverIdFilter = documentFilter.Eq(serverSettings => serverSettings.ServerId, guildId);
            FilterDefinition<ServerSettings>?        filter         = serverIdFilter;
            ServerSettings?                          serverSettings = await _serverSettingsCollection.Find(filter).FirstOrDefaultAsync();
            return serverSettings;
        }

        //TODO: AddAllowedRole should specify if anything was added or if it was already there, would require a Result<bool> or int or something
        public async Task<Result> AddAllowedRole(Snowflake guildId, Snowflake roleId)
        {
            // Add a role to the server's list of allowed roles if it isn't already there
            // Upsert the server settings if they don't exist
            FilterDefinitionBuilder<ServerSettings>? documentFilter = Builders<ServerSettings>.Filter;  
            FilterDefinition<ServerSettings>?        serverIdFilter = documentFilter.Eq(serverSettings => serverSettings.ServerId, guildId);
            FilterDefinition<ServerSettings>?        filter         = serverIdFilter;
            UpdateDefinition<ServerSettings>?        update         = Builders<ServerSettings>.Update.AddToSet(serverSettings => serverSettings.AllowedRolesSnowflakes, roleId);
            UpdateResult?                            updateResult   = await _serverSettingsCollection.UpdateOneAsync(filter, update, new(){ IsUpsert = true });
            switch (updateResult.ModifiedCount)
            {
                case 0: // Server already had that role as allowed
                    return Result.FromSuccess();
                case 1: // Server didn't have that role as allowed, added
                    return Result.FromSuccess();
                default: // Shouldn't happen
                    _logger.LogCritical("Updated {updateResult.ModifiedCount} rows for server {guildId}. There should not be multiple rows with the same guildId", updateResult.ModifiedCount, guildId);
                    return new ArgumentOutOfRangeError(nameof(updateResult.ModifiedCount),$"Updated {updateResult.ModifiedCount} rows for server {guildId}. There should not be multiple rows with the same guildId");
            }
        }
        public async Task<Result<short>> RemoveAllowedRole(Snowflake guildId, Snowflake roleId)
        {
            // Remove a role from the server's list of allowed roles if it is there
            // Upsert the server settings if they don't exist
            FilterDefinitionBuilder<ServerSettings>? documentFilter = Builders<ServerSettings>.Filter;  
            FilterDefinition<ServerSettings>?        serverIdFilter = documentFilter.Eq(serverSettings => serverSettings.ServerId, guildId);
            FilterDefinition<ServerSettings>?        filter         = serverIdFilter;
            UpdateDefinition<ServerSettings>?        update         = Builders<ServerSettings>.Update.Pull(serverSettings => serverSettings.AllowedRolesSnowflakes, roleId);
            UpdateResult?                            updateResult   = await _serverSettingsCollection.UpdateOneAsync(filter, update);
            switch (updateResult.ModifiedCount)
            {
                case 0: // Server didn't have that role as allowed
                    return 0;
                case 1: // Server had that role as allowed, removed
                    return 1;
                default: // Shouldn't happen
                    _logger.LogCritical("Updated {updateResult.ModifiedCount} rows for server {guildId}. There should not be multiple rows with the same guildId", updateResult.ModifiedCount, guildId);
                    return new ArgumentOutOfRangeException(nameof(updateResult.ModifiedCount), updateResult.ModifiedCount, $"Updated {updateResult.ModifiedCount} rows for server {guildId}. There should not be multiple rows with the same guildId");
            }
        }

        public async Task<Result<List<Snowflake>>> GetAllowedRoles(Snowflake guildId, List<Snowflake> roleIds)
        {
            FilterDefinitionBuilder<ServerSettings>? documentFilter = Builders<ServerSettings>.Filter;
            FilterDefinition<ServerSettings>?        serverIdFilter = documentFilter.Eq(serverSettings => serverSettings.ServerId, guildId);
            FilterDefinition<ServerSettings>?        filter         = serverIdFilter;
            // Grab only the allowed roles list
            ProjectionDefinitionBuilder<ServerSettings>?           documentProjection = Builders<ServerSettings>.Projection;
            ProjectionDefinition<ServerSettings, List<Snowflake>>? projection         = documentProjection.Expression(serverSettings => serverSettings.AllowedRolesSnowflakes);
            List<Snowflake>?                                       allowedRoles       = await _serverSettingsCollection.Find(filter).Project(projection).FirstOrDefaultAsync();
            allowedRoles ??= [];
            return allowedRoles;
        }

        #endregion

        #region MessageRectorSettingsDatabase

        public async Task<MessageReactorSettings?> GetMessageReactorSettings(Snowflake guildId, Snowflake? userId = null)
        {
            FilterDefinitionBuilder<MessageReactorSettings>? documentFilter = Builders<MessageReactorSettings>.Filter;
            FilterDefinition<MessageReactorSettings>?        serverIdFilter = documentFilter.Eq(messageReactorSettings => messageReactorSettings.ServerId, guildId);
            FilterDefinition<MessageReactorSettings>?        userIdFilter   = userId.HasValue ? documentFilter.Eq(messageReactorSettings => messageReactorSettings.UserIds, userId.Value) : documentFilter.Empty;
            FilterDefinition<MessageReactorSettings>?        filter         = documentFilter.And(serverIdFilter, userIdFilter);
            MessageReactorSettings?                          messageReactorSettings = await _messageReactorSettingsCollection.Find(filter).FirstOrDefaultAsync();
            return messageReactorSettings;
        }

        public async Task<Result> UpdateMessageReactorSettings(
            MessageReactorSettings messageReactorSettings)
        {
            //Replace with passed arg
            ReplaceOneResult replaceResult = await _messageReactorSettingsCollection.ReplaceOneAsync(mrs => mrs.ServerId == messageReactorSettings.ServerId, messageReactorSettings, new ReplaceOptions{ IsUpsert = true });
            return Result.FromSuccess();
        }

        public async Task<string?> GetEmoteString(Snowflake guildId)
        {
            var documentFilter = Builders<MessageReactorSettings>.Filter;
            var serverIdFilter = documentFilter.Eq(messageReactorSettings => messageReactorSettings.ServerId, guildId);
            var filter = serverIdFilter;
            // Grab only the emote string
            var documentProjection = Builders<MessageReactorSettings>.Projection;
            var projection = documentProjection.Expression(messageReactorSettings => messageReactorSettings.Emotes);
            string? emoteString = await _messageReactorSettingsCollection.Find(filter).Project(projection).FirstOrDefaultAsync();
            return emoteString;
        }
        #endregion
    }
}
