﻿// <copyright file="EventStoreAggregateRepositoryTests.cs" company="Corsham Science">
// Copyright (c) Corsham Science. All rights reserved.
// </copyright>

namespace CorshamScience.AggregateRepository.EventStore.Tests
{
    using CorshamScience.AggregateRepository.EventStore;
    using DotNet.Testcontainers.Builders;
    using DotNet.Testcontainers.Containers;
    using DotNet.Testcontainers.Images;
    using global::EventStore.Client;

    internal class EventStoreAggregateRepositoryTests : AggregateRepositoryTestFixture
    {
        private ITestcontainersContainer? _container;
        private EventStoreClient? _client;

        protected override async Task InitRepositoryAsync()
        {
            const int hostPort = 2113;
            _container = new TestcontainersBuilder<TestcontainersContainer>()
              .WithImage(new DockerImage("eventstore/eventstore:20.10.2-buster-slim"))
              .WithName("aggregate-repository-eventstore-tests")
              .WithCleanUp(true)
              .WithPortBinding(hostPort)
              .WithEnvironment(new Dictionary<string, string>
              {
                  { "EVENTSTORE_INSECURE", "true" },
                  { "EVENTSTORE_ENABLE_ATOM_PUB_OVER_HTTP", "true" },
              })
              .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(hostPort))
              .Build();

            await _container.StartAsync();

            var settings = EventStoreClientSettings
            .Create($"esdb://admin:changeit@127.0.0.1:{hostPort}?tls=false");

            _client = new EventStoreClient(settings);
            RepoUnderTest = new EventStoreAggregateRepository(_client);
        }

        protected async override Task CleanUpRepositoryAsync()
        {
            if (_container != null)
            {
                await _container.DisposeAsync();
            }
            
            _client?.Dispose();
        }
    }
}
