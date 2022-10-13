using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace MongoSourceConnectorToEventGrid
{
    public class MongoDBChangeStreamService
    {
        protected readonly IMongoClient client;
        protected readonly IMongoDatabase database;
        private readonly IMongoCollection<BsonDocument> collection;
        private readonly IAppLogger<MongoDBChangeStreamService> logger;
        private readonly SemaphoreSlim semaphoreSlim = new(1, 1);
        private BlobServiceClient blobServiceClient;
        private string container;
        private string storageAccountCon;
        private string dLGen2AccountName;
        private string dLGen2AccountKey;
        private string fileSystemName;
        private string dataLakeGen2Uri;

        #region Public Methods

        public MongoDBChangeStreamService(IMongoClient client, IAppLogger<MongoDBChangeStreamService> logger, 
              IConfiguration configuration)
        {
            this.database = client.GetDatabase(configuration["mongodb-database"]);
            this.collection = this.database.GetCollection<BsonDocument>(configuration["mongodb-collection"]);
            this.client = client;
            this.logger = logger;
            this.dLGen2AccountName = configuration["dataLakeGen2-accountName"];
            this.dLGen2AccountKey = configuration["dataLakeGen2-accountKey"];
            this.fileSystemName = configuration["fileSystemName"];
            this.dataLakeGen2Uri = configuration["dataLakeGen2Uri"];
            this.container = configuration["container"];   
        }

        /// <summary>
        /// Intiliaze Thread
        /// </summary>
        public void Init()
        {
            new Thread(async () => await ObserveCollections()).Start();
        }
        #endregion

        #region Private Methods

        /// <summary>
        /// Observe Collection for Update, insert or Delete activities 
        /// </summary>
        /// <returns></returns>
        private async Task ObserveCollections()
        {
            // Filter definition for document updated 
            var pipelineFilterDefinition = new EmptyPipelineDefinition<ChangeStreamDocument<BsonDocument>>()
                .Match(x => x.OperationType == ChangeStreamOperationType.Update
                || x.OperationType == ChangeStreamOperationType.Insert
                || x.OperationType == ChangeStreamOperationType.Delete);

            // choose stream option and set data lookup for full document 
            var changeStreamOptions = new ChangeStreamOptions
            {
                FullDocument = ChangeStreamFullDocumentOption.UpdateLookup
            };

            // Watches changes on the collection , no need to user cancellation token, mongo sdk already using it
            using var cursor = await this.collection.WatchAsync(pipelineFilterDefinition, changeStreamOptions);

            await this.semaphoreSlim.WaitAsync();

            // Run watch updated operations on returned cursor  from watch async
            await this.WatchCollectionUpdates(cursor);

            // release thread
            this.semaphoreSlim.Release();
        }

        /// <summary>
        /// Mongo DB change stream to track changes on collection
        /// </summary>
        /// <param name="cursor"></param>
        /// <returns></returns>

        private async Task WatchCollectionUpdates(IChangeStreamCursor<ChangeStreamDocument<BsonDocument>> cursor)
        {
            await cursor?.ForEachAsync(async change =>
            {
                // If change is null - log information as null and return message
                if (change == null)
                {
                    this.logger.LogInformation("No changes tracked  by change stream  watcher");
                    return;
                }
                try
                {
                    // Deserialize full document with Plain object 
                    var updatedDocument = BsonSerializer.Deserialize<Dictionary<string, object>>(change.FullDocument);
                    
                    // remove _id aka objectId key from Mongodb
                    updatedDocument.Remove("_id");

                    var isBlobUpdate = await UpdateStorage(updatedDocument);

                    // log information
                    if(isBlobUpdate) 
                        this.logger.LogInformation($"Changes tracked successfully by change stream : {updatedDocument.ToJson()}");
                    else
                    {
                        this.logger.LogError($"Unable to push changes to blob for type : {updatedDocument.ToJson()}");
                    }
                }
                catch (MongoException exception)
                {
                    // log mongodb exception - helpful for developers
                    this.logger.LogError("Change Stream watcher. Exception:" + exception.Message);
                }
            })!;
        }

        /// <summary>
        /// Upload delta changes to ADL gen 2
        /// </summary>
        /// <param name="updatedDocument"></param>
        /// <returns></returns>
        private async Task<bool> UpdateStorage(Dictionary<string, object> updatedDocument)
        {
            try
            {

                StorageSharedKeyCredential sharedKeyCredential =  new(dLGen2AccountName, dLGen2AccountKey);
                DataLakeServiceClient dataLakeServiceClient = new DataLakeServiceClient (new Uri(dataLakeGen2Uri), sharedKeyCredential);
              
                DataLakeFileSystemClient fileSystemClient = dataLakeServiceClient.GetFileSystemClient(fileSystemName);

                DataLakeDirectoryClient directoryClient = fileSystemClient.GetDirectoryClient(container);
                
                // json file type 
                var filePath = $"{container}-" + Guid.NewGuid() + ".json";
                DataLakeFileClient fileClient = await directoryClient.CreateFileAsync(filePath);
                byte[] recordcontent=Encoding.UTF8.GetBytes(updatedDocument.ToJson());
                await using var ms = new MemoryStream(recordcontent);
               
                var file = await fileClient.AppendAsync(ms, offset: 0);
                long fileSize = recordcontent.Length;
                await fileClient.FlushAsync(position: fileSize);

                var fileAccessControl = await fileClient.GetAccessControlAsync();

                var accessControlList = PathAccessControlExtensions.ParseAccessControlList(
                    "user::rwx,group::rwx,other::rw-");
                await fileClient.SetAccessControlListAsync((accessControlList));
                
                var uploadedVer = fileSize > 0;
                return uploadedVer;
            }
            catch (Exception e)
            {
                // log exception
                this.logger.LogError("Change Stream watcher. Exception:" + e.Message);
                throw;
            }
        }

        #endregion
    }
}
