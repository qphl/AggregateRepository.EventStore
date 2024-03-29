﻿// <copyright file="EventStoreAggregateRepository.cs" company="Corsham Science">
// Copyright (c) Corsham Science. All rights reserved.
// </copyright>

namespace CorshamScience.AggregateRepository.EventStore
{
    using System;
    using System.Text;
    using CorshamScience.AggregateRepository.Core;
    using CorshamScience.AggregateRepository.Core.Exceptions;
    using global::EventStore.Client;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <inheritdoc />
    /// <summary>
    /// Implementation of <see cref="T:CorshamScience.AggregateRepository.Core.IAggregateRepository" /> which uses Event Store as underlying storage for an aggregate's events.
    /// </summary>
    public class EventStoreAggregateRepository : IAggregateRepository
    {
        private readonly EventStoreClient _eventStoreClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventStoreAggregateRepository"/> class using the provided <see cref="EventStoreClient"/> to store and retrieve events for an <see cref="IAggregate"/>.
        /// </summary>
        /// <param name="eventStoreClient">The GRPC <see cref="EventStoreClient"/> to connect to.</param>
        public EventStoreAggregateRepository(EventStoreClient eventStoreClient) => _eventStoreClient = eventStoreClient;

        /// <inheritdoc />
        /// <exception cref="AggregateNotFoundException">
        /// Thrown when the provided <see cref="IAggregate"/>'s ID matches a deleted stream in the EventStore the <see cref="EventStoreClient"/> is configured to use.
        /// </exception>
        public async Task SaveAsync(IAggregate aggregateToSave)
        {
            var events = aggregateToSave.GetUncommittedEvents().Cast<object>().ToList();
            var streamName = StreamNameForAggregateId(aggregateToSave.Id);

            var originalVersion = aggregateToSave.Version - events.Count;
            ulong expectedVersion = originalVersion == 0 ? expectedVersion = StreamRevision.None : (ulong)(originalVersion - 1);

            var preparedEvents = events
                .Select(ToEventData)
                .ToArray();

            try
            {
                await _eventStoreClient.AppendToStreamAsync(streamName, expectedVersion, preparedEvents)
                    .ConfigureAwait(false);
                aggregateToSave.ClearUncommittedEvents();
            }
            catch (StreamDeletedException ex)
            {
                throw new AggregateNotFoundException("Aggregate not found, stream deleted", ex);
            }
            catch (WrongExpectedVersionException ex)
            {
                throw new AggregateVersionException("Aggregate version incorrect", ex);
            }
        }

        /// <inheritdoc />
        public async Task<T> GetAggregateAsync<T>(object aggregateId, int version = int.MaxValue)
            where T : IAggregate
        {
            if (version <= 0)
            {
                throw new InvalidOperationException("Cannot get version <= 0");
            }

            var streamName = StreamNameForAggregateId(aggregateId);
            var events = ReadFromStream(streamName, version);

            return await CreateAndRehydrateAggregateAsync<T>(events, version).ConfigureAwait(false);
        }

        private static async Task<T> CreateAndRehydrateAggregateAsync<T>(EventStoreClient.ReadStreamResult events, int version)
            where T : IAggregate
        {
            var aggregate = (T)Activator.CreateInstance(typeof(T), true) !;

            var eventCount = 0;

            await foreach (var @event in events.ConfigureAwait(false))
            {
                eventCount++;
                aggregate.ApplyEvent(Deserialize(@event));
            }

            // If version is greater than number of events, throw exception
            if (eventCount < version && version != int.MaxValue)
            {
                throw new AggregateVersionException("version is higher than actual version");
            }

            return aggregate;
        }

        private static EventData ToEventData(object @event)
        {
            var data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(@event));

            var eventHeaders = new
            {
                ClrType = @event.GetType().AssemblyQualifiedName,
            };

            var metadata = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(eventHeaders));
            var typeName = @event.GetType().Name;

            return new (
                Uuid.NewUuid(),
                typeName,
                data,
                metadata);
        }

        private static object Deserialize(ResolvedEvent resolvedEvent)
        {
            const string metaDataPropertyName = "ClrType";

            var jsonData = Encoding.UTF8.GetString(resolvedEvent.Event.Data.Span);
            var metaData = Encoding.UTF8.GetString(resolvedEvent.Event.Metadata.Span);
            var eventClrTypeName = JObject.Parse(metaData).Property(metaDataPropertyName)?.Value?.ToObject<string>();

            if (eventClrTypeName is null)
            {
                throw new InvalidOperationException($"Event Metadata has no property '{metaDataPropertyName}'");
            }

            var type = Type.GetType(eventClrTypeName);
            if (type is null)
            {
                throw new InvalidOperationException($"Could not find type ${eventClrTypeName}");
            }

            var deserialized = JsonConvert.DeserializeObject(jsonData, type);
            if (deserialized is null)
            {
                throw new InvalidOperationException($"Failed to deserialize event of type ${eventClrTypeName}");
            }

            return deserialized;
        }

        private static string StreamNameForAggregateId(object id) => "aggregate-" + id;

        private EventStoreClient.ReadStreamResult ReadFromStream(string streamName, int version)
        {
            var events = _eventStoreClient.ReadStreamAsync(
                Direction.Forwards,
                streamName,
                StreamPosition.Start,
                version);

            if (events.ReadState.Result != ReadState.Ok)
            {
                throw new AggregateNotFoundException(streamName);
            }

            return events;
        }
    }
}
