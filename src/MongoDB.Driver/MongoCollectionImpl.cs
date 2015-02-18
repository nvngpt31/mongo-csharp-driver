﻿/* Copyright 2010-2014 MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver.Core.Bindings;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Core.Operations;
using MongoDB.Driver.Core.WireProtocol.Messages.Encoders;

namespace MongoDB.Driver
{
    internal sealed class MongoCollectionImpl<TDocument> : MongoCollectionBase<TDocument>
    {
        // fields
        private readonly ICluster _cluster;
        private readonly CollectionNamespace _collectionNamespace;
        private readonly MessageEncoderSettings _messageEncoderSettings;
        private readonly IOperationExecutor _operationExecutor;
        private readonly IBsonSerializer<TDocument> _documentSerializer;
        private readonly MongoCollectionSettings _settings;

        // constructors
        public MongoCollectionImpl(CollectionNamespace collectionNamespace, MongoCollectionSettings settings, ICluster cluster, IOperationExecutor operationExecutor)
        {
            _collectionNamespace = Ensure.IsNotNull(collectionNamespace, "collectionNamespace");
            _settings = Ensure.IsNotNull(settings, "settings").Freeze();
            _cluster = Ensure.IsNotNull(cluster, "cluster");
            _operationExecutor = Ensure.IsNotNull(operationExecutor, "operationExecutor");

            _documentSerializer = _settings.SerializerRegistry.GetSerializer<TDocument>();
            _messageEncoderSettings = new MessageEncoderSettings
            {
                { MessageEncoderSettingsName.GuidRepresentation, _settings.GuidRepresentation },
                { MessageEncoderSettingsName.ReadEncoding, _settings.ReadEncoding ?? Utf8Encodings.Strict },
                { MessageEncoderSettingsName.WriteEncoding, _settings.WriteEncoding ?? Utf8Encodings.Strict }
            };
        }

        // properties
        public override CollectionNamespace CollectionNamespace
        {
            get { return _collectionNamespace; }
        }

        public override IBsonSerializer<TDocument> DocumentSerializer
        {
            get { return _documentSerializer; }
        }

        public override IMongoIndexManager<TDocument> IndexManager
        {
            get { return new MongoIndexManager(this); }
        }

        public override MongoCollectionSettings Settings
        {
            get { return _settings; }
        }

        // methods
        public override async Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(IEnumerable<object> pipeline, AggregateOptions<TResult> options, CancellationToken cancellationToken)
        {
            Ensure.IsNotNull(pipeline, "pipeline");

            options = options ?? new AggregateOptions<TResult>();
            var pipelineDocuments = pipeline.Select(x => ConvertToBsonDocument(x)).ToList();

            var last = pipelineDocuments.LastOrDefault();
            if (last != null && last.GetElement(0).Name == "$out")
            {
                var operation = new AggregateToCollectionOperation(
                    _collectionNamespace,
                    pipelineDocuments,
                    _messageEncoderSettings)
                {
                    AllowDiskUse = options.AllowDiskUse,
                    MaxTime = options.MaxTime
                };

                await ExecuteWriteOperation(operation, cancellationToken).ConfigureAwait(false);

                var outputCollectionName = last.GetElement(0).Value.AsString;
                var resultSerializer = ResolveResultSerializer(options.ResultSerializer);

                var findOperation = new FindOperation<TResult>(
                    new CollectionNamespace(_collectionNamespace.DatabaseNamespace, outputCollectionName),
                    resultSerializer,
                    _messageEncoderSettings)
                {
                    BatchSize = options.BatchSize,
                    MaxTime = options.MaxTime
                };

                // we want to delay execution of the find because the user may
                // not want to iterate the results at all...
                return await Task.FromResult<IAsyncCursor<TResult>>(new DeferredAsyncCursor<TResult>(ct => ExecuteReadOperation(findOperation, ReadPreference.Primary, ct))).ConfigureAwait(false);
            }
            else
            {
                var operation = CreateAggregateOperation<TResult>(options, pipelineDocuments);
                return await ExecuteReadOperation(operation, cancellationToken).ConfigureAwait(false);
            }
        }

        public override async Task<BulkWriteResult<TDocument>> BulkWriteAsync(IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions options, CancellationToken cancellationToken)
        {
            Ensure.IsNotNull(requests, "requests");
            if (!requests.Any())
            {
                throw new ArgumentException("Must contain at least 1 request.", "requests");
            }

            options = options ?? new BulkWriteOptions();

            var operation = new BulkMixedWriteOperation(
                _collectionNamespace,
                requests.Select(ConvertWriteModelToWriteRequest),
                _messageEncoderSettings)
            {
                IsOrdered = options.IsOrdered,
                WriteConcern = _settings.WriteConcern
            };

            try
            {
                var result = await ExecuteWriteOperation(operation, cancellationToken).ConfigureAwait(false);
                return BulkWriteResult<TDocument>.FromCore(result, requests);
            }
            catch (MongoBulkWriteOperationException ex)
            {
                throw MongoBulkWriteException<TDocument>.FromCore(ex, requests.ToList());
            }
        }

        public override Task<long> CountAsync(Filter<TDocument> filter, CountOptions options, CancellationToken cancellationToken)
        {
            Ensure.IsNotNull(filter, "filter");

            options = options ?? new CountOptions();

            var operation = new CountOperation(_collectionNamespace, _messageEncoderSettings)
            {
                Filter = filter.Render(_documentSerializer, _settings.SerializerRegistry),
                Hint = options.Hint is string ? BsonValue.Create((string)options.Hint) : ConvertToBsonDocument(options.Hint),
                Limit = options.Limit,
                MaxTime = options.MaxTime,
                Skip = options.Skip
            };

            return ExecuteReadOperation(operation, cancellationToken);
        }

        public override Task<IAsyncCursor<TResult>> DistinctAsync<TResult>(string fieldName, Filter<TDocument> filter, DistinctOptions<TResult> options, CancellationToken cancellationToken)
        {
            Ensure.IsNotNull(fieldName, "fieldName");
            Ensure.IsNotNull(filter, "filter");

            options = options ?? new DistinctOptions<TResult>();
            var resultSerializer = ResolveResultSerializer(options.ResultSerializer);

            var operation = new DistinctOperation<TResult>(
                _collectionNamespace,
                resultSerializer,
                fieldName,
                _messageEncoderSettings)
            {
                Filter = filter.Render(_documentSerializer, _settings.SerializerRegistry),
                MaxTime = options.MaxTime
            };

            return ExecuteReadOperation(operation, cancellationToken);
        }

        public override Task<IAsyncCursor<TResult>> FindAsync<TResult>(Filter<TDocument> filter, FindOptions<TResult> options, CancellationToken cancellationToken)
        {
            Ensure.IsNotNull(filter, "filter");

            options = options ?? new FindOptions<TResult>();
            var resultSerializer = ResolveResultSerializer(options.ResultSerializer);

            var operation = new FindOperation<TResult>(
                _collectionNamespace,
                resultSerializer,
                _messageEncoderSettings)
            {
                AllowPartialResults = options.AllowPartialResults,
                BatchSize = options.BatchSize,
                Comment = options.Comment,
                CursorType = options.CursorType.ToCore(),
                Filter = filter.Render(_documentSerializer, _settings.SerializerRegistry),
                Limit = options.Limit,
                MaxTime = options.MaxTime,
                Modifiers = options.Modifiers,
                NoCursorTimeout = options.NoCursorTimeout,
                Projection = ConvertToBsonDocument(options.Projection),
                Skip = options.Skip,
                Sort = ConvertToBsonDocument(options.Sort),
            };

            return ExecuteReadOperation(operation, cancellationToken);
        }

        public override Task<TResult> FindOneAndDeleteAsync<TResult>(Filter<TDocument> filter, FindOneAndDeleteOptions<TResult> options, CancellationToken cancellationToken)
        {
            Ensure.IsNotNull(filter, "filter");

            options = options ?? new FindOneAndDeleteOptions<TResult>();
            var resultSerializer = ResolveResultSerializer(options.ResultSerializer);

            var operation = new FindOneAndDeleteOperation<TResult>(
                _collectionNamespace,
                filter.Render(_documentSerializer, _settings.SerializerRegistry),
                new FindAndModifyValueDeserializer<TResult>(resultSerializer),
                _messageEncoderSettings)
            {
                MaxTime = options.MaxTime,
                Projection = ConvertToBsonDocument(options.Projection),
                Sort = ConvertToBsonDocument(options.Sort)
            };

            return ExecuteWriteOperation(operation, cancellationToken);
        }

        public override Task<TResult> FindOneAndReplaceAsync<TResult>(Filter<TDocument> filter, TDocument replacement, FindOneAndReplaceOptions<TResult> options, CancellationToken cancellationToken)
        {
            var replacementObject = (object)replacement; // only box once if it's a struct
            Ensure.IsNotNull(filter, "filter");
            Ensure.IsNotNull(replacementObject, "replacement");

            options = options ?? new FindOneAndReplaceOptions<TResult>();
            var resultSerializer = ResolveResultSerializer(options.ResultSerializer);

            var operation = new FindOneAndReplaceOperation<TResult>(
                _collectionNamespace,
                filter.Render(_documentSerializer, _settings.SerializerRegistry),
                ConvertToBsonDocument(replacementObject),
                new FindAndModifyValueDeserializer<TResult>(resultSerializer),
                _messageEncoderSettings)
            {
                IsUpsert = options.IsUpsert,
                MaxTime = options.MaxTime,
                Projection = ConvertToBsonDocument(options.Projection),
                ReturnDocument = options.ReturnDocument.ToCore(),
                Sort = ConvertToBsonDocument(options.Sort)
            };

            return ExecuteWriteOperation(operation, cancellationToken);
        }

        public override Task<TResult> FindOneAndUpdateAsync<TResult>(Filter<TDocument> filter, object update, FindOneAndUpdateOptions<TResult> options, CancellationToken cancellationToken)
        {
            Ensure.IsNotNull(filter, "filter");
            Ensure.IsNotNull(update, "update");

            options = options ?? new FindOneAndUpdateOptions<TResult>();
            var resultSerializer = ResolveResultSerializer(options.ResultSerializer);

            var operation = new FindOneAndUpdateOperation<TResult>(
                _collectionNamespace,
                filter.Render(_documentSerializer, _settings.SerializerRegistry),
                ConvertToBsonDocument(update),
                new FindAndModifyValueDeserializer<TResult>(resultSerializer),
                _messageEncoderSettings)
            {
                IsUpsert = options.IsUpsert,
                MaxTime = options.MaxTime,
                Projection = ConvertToBsonDocument(options.Projection),
                ReturnDocument = options.ReturnDocument.ToCore(),
                Sort = ConvertToBsonDocument(options.Sort)
            };

            return ExecuteWriteOperation(operation, cancellationToken);
        }

        public override async Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult> options = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            Ensure.IsNotNull(map, "map");
            Ensure.IsNotNull(reduce, "reduce");

            options = options ?? new MapReduceOptions<TDocument, TResult>();
            var outputOptions = options.OutputOptions ?? MapReduceOutputOptions.Inline;
            var resultSerializer = ResolveResultSerializer<TResult>(options.ResultSerializer);

            if (outputOptions == MapReduceOutputOptions.Inline)
            {
                var operation = new MapReduceOperation<TResult>(
                    _collectionNamespace,
                    map,
                    reduce,
                    resultSerializer,
                    _messageEncoderSettings)
                {
                    Filter = options.Filter == null ? null : options.Filter.Render(_documentSerializer, _settings.SerializerRegistry),
                    FinalizeFunction = options.Finalize,
                    JavaScriptMode = options.JavaScriptMode,
                    Limit = options.Limit,
                    MaxTime = options.MaxTime,
                    Scope = BsonDocumentHelper.ToBsonDocument(_settings.SerializerRegistry, options.Scope),
                    Sort = BsonDocumentHelper.ToBsonDocument(_settings.SerializerRegistry, options.Sort),
                    Verbose = options.Verbose
                };

                return await ExecuteReadOperation(operation, cancellationToken);
            }
            else
            {
                var collectionOutputOptions = (MapReduceOutputOptions.CollectionOutput)outputOptions;
                var databaseNamespace = collectionOutputOptions.DatabaseName == null ?
                    _collectionNamespace.DatabaseNamespace :
                    new DatabaseNamespace(collectionOutputOptions.DatabaseName);
                var outputCollectionNamespace = new CollectionNamespace(databaseNamespace, collectionOutputOptions.CollectionName);

                var operation = new MapReduceOutputToCollectionOperation(
                    _collectionNamespace,
                    outputCollectionNamespace,
                    map,
                    reduce,
                    _messageEncoderSettings)
                {
                    Filter = options.Filter == null ? null : options.Filter.Render(_documentSerializer, _settings.SerializerRegistry),
                    FinalizeFunction = options.Finalize,
                    JavaScriptMode = options.JavaScriptMode,
                    Limit = options.Limit,
                    MaxTime = options.MaxTime,
                    NonAtomicOutput = collectionOutputOptions.NonAtomic,
                    Scope = BsonDocumentHelper.ToBsonDocument(_settings.SerializerRegistry, options.Scope),
                    OutputMode = collectionOutputOptions.OutputMode,
                    ShardedOutput = collectionOutputOptions.Sharded,
                    Sort = BsonDocumentHelper.ToBsonDocument(_settings.SerializerRegistry, options.Sort),
                    Verbose = options.Verbose
                };

                var result = await ExecuteWriteOperation(operation, cancellationToken);

                var findOperation = new FindOperation<TResult>(
                    outputCollectionNamespace,
                    resultSerializer,
                    _messageEncoderSettings)
                {
                    MaxTime = options.MaxTime
                };

                // we want to delay execution of the find because the user may
                // not want to iterate the results at all...
                var deferredCursor = new DeferredAsyncCursor<TResult>(ct => ExecuteReadOperation(findOperation, ReadPreference.Primary, ct));
                return await Task.FromResult(deferredCursor).ConfigureAwait(false);
            }
        }

        public override IMongoCollection<TDocument> WithReadPreference(ReadPreference readPreference)
        {
            var newSettings = _settings.Clone();
            newSettings.ReadPreference = readPreference;
            return new MongoCollectionImpl<TDocument>(_collectionNamespace, newSettings, _cluster, _operationExecutor);
        }

        public override IMongoCollection<TDocument> WithWriteConcern(WriteConcern writeConcern)
        {
            var newSettings = _settings.Clone();
            newSettings.WriteConcern = writeConcern;
            return new MongoCollectionImpl<TDocument>(_collectionNamespace, newSettings, _cluster, _operationExecutor);
        }

        private void AssignId(TDocument document)
        {
            var idProvider = _documentSerializer as IBsonIdProvider;
            if (idProvider != null)
            {
                object id;
                Type idNominalType;
                IIdGenerator idGenerator;
                if (idProvider.GetDocumentId(document, out id, out idNominalType, out idGenerator))
                {
                    if (idGenerator != null && idGenerator.IsEmpty(id))
                    {
                        id = idGenerator.GenerateId(this, document);
                        idProvider.SetDocumentId(document, id);
                    }
                }
            }
        }

        private WriteRequest ConvertWriteModelToWriteRequest(WriteModel<TDocument> model, int index)
        {
            switch (model.ModelType)
            {
                case WriteModelType.InsertOne:
                    var insertOneModel = (InsertOneModel<TDocument>)model;
                    AssignId(insertOneModel.Document);
                    return new InsertRequest(new BsonDocumentWrapper(insertOneModel.Document, _documentSerializer))
                    {
                        CorrelationId = index
                    };
                case WriteModelType.DeleteMany:
                    var deleteManyModel = (DeleteManyModel<TDocument>)model;
                    return new DeleteRequest(deleteManyModel.Filter.Render(_documentSerializer, _settings.SerializerRegistry))
                    {
                        CorrelationId = index,
                        Limit = 0
                    };
                case WriteModelType.DeleteOne:
                    var deleteOneModel = (DeleteOneModel<TDocument>)model;
                    return new DeleteRequest(deleteOneModel.Filter.Render(_documentSerializer, _settings.SerializerRegistry))
                    {
                        CorrelationId = index,
                        Limit = 1
                    };
                case WriteModelType.ReplaceOne:
                    var replaceOneModel = (ReplaceOneModel<TDocument>)model;
                    return new UpdateRequest(
                        UpdateType.Replacement,
                        replaceOneModel.Filter.Render(_documentSerializer, _settings.SerializerRegistry),
                        new BsonDocumentWrapper(replaceOneModel.Replacement, _documentSerializer))
                    {
                        CorrelationId = index,
                        IsMulti = false,
                        IsUpsert = replaceOneModel.IsUpsert
                    };
                case WriteModelType.UpdateMany:
                    var updateManyModel = (UpdateManyModel<TDocument>)model;
                    return new UpdateRequest(
                        UpdateType.Update,
                        updateManyModel.Filter.Render(_documentSerializer, _settings.SerializerRegistry),
                        ConvertToBsonDocument(updateManyModel.Update))
                    {
                        CorrelationId = index,
                        IsMulti = true,
                        IsUpsert = updateManyModel.IsUpsert
                    };
                case WriteModelType.UpdateOne:
                    var updateOneModel = (UpdateOneModel<TDocument>)model;
                    return new UpdateRequest(
                        UpdateType.Update,
                        updateOneModel.Filter.Render(_documentSerializer, _settings.SerializerRegistry),
                        ConvertToBsonDocument(updateOneModel.Update))
                    {
                        CorrelationId = index,
                        IsMulti = false,
                        IsUpsert = updateOneModel.IsUpsert
                    };
                default:
                    throw new InvalidOperationException("Unknown type of WriteModel provided.");
            }
        }

        private BsonDocument ConvertToBsonDocument(object document)
        {
            return BsonDocumentHelper.ToBsonDocument(_settings.SerializerRegistry, document);
        }

        private Task<TResult> ExecuteReadOperation<TResult>(IReadOperation<TResult> operation, CancellationToken cancellationToken)
        {
            return ExecuteReadOperation(operation, _settings.ReadPreference, cancellationToken);
        }

        private async Task<TResult> ExecuteReadOperation<TResult>(IReadOperation<TResult> operation, ReadPreference readPreference, CancellationToken cancellationToken)
        {
            using (var binding = new ReadPreferenceBinding(_cluster, readPreference))
            {
                return await _operationExecutor.ExecuteReadOperationAsync(binding, operation, _settings.OperationTimeout, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<TResult> ExecuteWriteOperation<TResult>(IWriteOperation<TResult> operation, CancellationToken cancellationToken)
        {
            using (var binding = new WritableServerBinding(_cluster))
            {
                return await _operationExecutor.ExecuteWriteOperationAsync(binding, operation, _settings.OperationTimeout, cancellationToken).ConfigureAwait(false);
            }
        }

        private AggregateOperation<TResult> CreateAggregateOperation<TResult>(AggregateOptions<TResult> options, List<BsonDocument> pipeline)
        {
            var resultSerializer = ResolveResultSerializer(options.ResultSerializer);

            return new AggregateOperation<TResult>(
                _collectionNamespace,
                pipeline,
                resultSerializer,
                _messageEncoderSettings)
            {
                AllowDiskUse = options.AllowDiskUse,
                BatchSize = options.BatchSize,
                MaxTime = options.MaxTime,
                UseCursor = options.UseCursor
            };
        }

        private IBsonSerializer<TResult> ResolveResultSerializer<TResult>(IBsonSerializer<TResult> resultSerializer)
        {
            if (resultSerializer != null)
            {
                return resultSerializer;
            }

            if (typeof(TResult) == typeof(TDocument) && _documentSerializer != null)
            {
                return (IBsonSerializer<TResult>)_documentSerializer;
            }

            return _settings.SerializerRegistry.GetSerializer<TResult>();
        }

        private class MongoIndexManager : MongoIndexManagerBase<TDocument>
        {
            private readonly MongoCollectionImpl<TDocument> _collection;

            public MongoIndexManager(MongoCollectionImpl<TDocument> collection)
            {
                _collection = collection;
            }

            public override CollectionNamespace CollectionNamespace
            {
                get { return _collection.CollectionNamespace; }
            }

            public override IBsonSerializer<TDocument> DocumentSerializer
            {
                get { return _collection.DocumentSerializer; }
            }

            public override MongoCollectionSettings Settings
            {
                get { return _collection.Settings; }
            }

            public override Task CreateIndexAsync(object keys, CreateIndexOptions options, CancellationToken cancellationToken)
            {
                Ensure.IsNotNull(keys, "keys");

                var keysDocument = _collection.ConvertToBsonDocument(keys);

                options = options ?? new CreateIndexOptions();
                var request = new CreateIndexRequest(keysDocument)
                {
                    Name = options.Name,
                    Background = options.Background,
                    Bits = options.Bits,
                    BucketSize = options.BucketSize,
                    DefaultLanguage = options.DefaultLanguage,
                    ExpireAfter = options.ExpireAfter,
                    LanguageOverride = options.LanguageOverride,
                    Max = options.Max,
                    Min = options.Min,
                    Sparse = options.Sparse,
                    SphereIndexVersion = options.SphereIndexVersion,
                    StorageEngine = _collection.ConvertToBsonDocument(options.StorageEngine),
                    TextIndexVersion = options.TextIndexVersion,
                    Unique = options.Unique,
                    Version = options.Version,
                    Weights = _collection.ConvertToBsonDocument(options.Weights)
                };

                var operation = new CreateIndexesOperation(_collection._collectionNamespace, new[] { request }, _collection._messageEncoderSettings);
                return _collection.ExecuteWriteOperation(operation, cancellationToken);
            }

            public override Task DropIndexAsync(string name, CancellationToken cancellationToken)
            {
                Ensure.IsNotNullOrEmpty(name, "name");

                var operation = new DropIndexOperation(_collection._collectionNamespace, name, _collection._messageEncoderSettings);

                return _collection.ExecuteWriteOperation(operation, cancellationToken);
            }

            public override Task DropIndexAsync(object keys, CancellationToken cancellationToken)
            {
                Ensure.IsNotNull(keys, "keys");

                var keysDocument = _collection.ConvertToBsonDocument(keys);
                var operation = new DropIndexOperation(_collection._collectionNamespace, keysDocument, _collection._messageEncoderSettings);

                return _collection.ExecuteWriteOperation(operation, cancellationToken);
            }

            public override Task<IAsyncCursor<BsonDocument>> ListIndexesAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                var op = new ListIndexesOperation(_collection._collectionNamespace, _collection._messageEncoderSettings);
                return _collection.ExecuteReadOperation(op, ReadPreference.Primary, cancellationToken);
            }
        }

    }
}
