﻿using Akka.Event;
using Akka.Persistence.Snapshot;
using Akka.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Util;
using SharpPulsar.Akka;
using SharpPulsar.Akka.Configuration;
using SharpPulsar.Akka.InternalCommands;
using SharpPulsar.Akka.InternalCommands.Producer;
using SharpPulsar.Handlers;
using SharpPulsar.Impl.Schema;

namespace Akka.Persistence.Pulsar.Snapshot
{
    /// <summary>
    ///     Pulsar-backed snapshot store for Akka.Persistence.
    /// </summary>
    /// 
    
    public class PulsarSnapshotStore : SnapshotStore
    {

        private readonly PulsarSettings _settings;
        private readonly ILoggingAdapter _log = Context.GetLogger();
        private readonly PulsarSystem _client; public static readonly ConcurrentDictionary<string, Dictionary<string, IActorRef>> _producers = new ConcurrentDictionary<string, Dictionary<string, IActorRef>>();
        private DefaultProducerListener _producerListener;
        private List<string> _pendingTopicProducer = new List<string>();
        private static readonly Type SnapshotType = typeof(Serialization.Snapshot);
        private readonly Serializer _serializer;
        private JsonSchema _snapshotEntrySchema; 
        private readonly Akka.Serialization.Serialization _serialization;


        //public Akka.Serialization.Serialization Serialization => _serialization ??= Context.System.Serialization;

        public PulsarSnapshotStore() : this(PulsarPersistence.Get(Context.System).JournalSettings)
        {

        }

        public PulsarSnapshotStore(PulsarSettings settings)
        {
            _snapshotEntrySchema = JsonSchema.Of(typeof(SnapshotEntry));
            _producerListener = new DefaultProducerListener(o =>
            {
                _log.Info(o.ToString());
            }, (to, n, p) =>
            {
                if (_producers.ContainsKey(to))
                    _producers[to].Add(n, p);
                else
                {
                    _producers[to] = new Dictionary<string, IActorRef> { { n, p } };
                }
                _pendingTopicProducer.Remove(to);
            }, s =>
            {

            });
            _serializer = Context.System.Serialization.FindSerializerForType(SnapshotType);
            _settings = settings;
            _client = settings.CreateSystem();
            _client.SetupSqlServers(new SqlServers(new List<string> { _settings.PrestoServer }.ToImmutableList()));
        }
        
        protected override async Task DeleteAsync(SnapshotMetadata metadata)
        {
            await Task.CompletedTask;
        }

        protected override async Task DeleteAsync(string persistenceId, SnapshotSelectionCriteria criteria)
        {
            await Task.CompletedTask;
        }

        protected override async Task<SelectedSnapshot> LoadAsync(string persistenceId, SnapshotSelectionCriteria criteria)
        {
            var queryActive = true;
            SelectedSnapshot shot = null;
            _client.QueryData(new QueryData($"select * from pulsar.\"{_settings.Tenant}/{_settings.Namespace}\".snapshot where persistenceid = '{persistenceId}' AND SequenceNr <= {criteria.MaxSequenceNr} AND Timestamp <= {new DateTimeOffset(criteria.MaxTimeStamp).ToUnixTimeMilliseconds()} ORDER BY SequenceNr DESC LIMIT 1",
                d =>
                {
                    if (d.ContainsKey("Finished"))
                    {
                        queryActive = false;
                        return;
                    }
                    var m = JsonSerializer.Deserialize<SnapshotEntry>(d["Message"]);
                    shot = ToSelectedSnapshot(m);
                }, e =>
                {
                    _log.Error(e.ToString());
                }, _settings.PrestoServer, true));
            while (queryActive)
            {
                await Task.Delay(500);
            }

            return shot;
        }

        protected override async Task SaveAsync(SnapshotMetadata metadata, object snapshot)
        {
            var (topic, producer) = GetProducer(metadata.PersistenceId, "Snapshot");
            while (producer == null)
            {
                (topic, producer) = GetProducer(metadata.PersistenceId, "Snapshot");
                await Task.Delay(1000);
            }
            var snapshotEntry = ToSnapshotEntry(metadata, snapshot);
            _client.Send(new Send(snapshotEntry, topic, ImmutableDictionary<string, object>.Empty), producer);
        }
        private void CreateSnapshotProducer(string persistenceid)
        {
            var topic = $"{_settings.TopicPrefix.TrimEnd('/')}/snapshot".ToLower();
            var producerConfig = new ProducerConfigBuilder()
                .ProducerName($"snapshot-{persistenceid}")
                .Topic(topic)
                .Schema(_snapshotEntrySchema)
                .SendTimeout(10000)
                .EventListener(_producerListener)
                .ProducerConfigurationData;
            _client.CreateProducer(new CreateProducer(_snapshotEntrySchema, producerConfig));
        }
        private (string topic, IActorRef producer) GetProducer(string persistenceid, string type)
        {
            var topic = $"{_settings.TopicPrefix.TrimEnd('/')}/{type}".ToLower();
            if (!_pendingTopicProducer.Contains(topic))
            {
                var p = _producers.FirstOrDefault(x => x.Key == topic && x.Value.ContainsKey($"{type.ToLower()}-{persistenceid}")).Value?.Values.FirstOrDefault();
                if (p == null)
                {
                    switch (type.ToLower())
                    {
                        case "snapshot":
                            CreateSnapshotProducer(persistenceid);
                            break;
                    }
                    _pendingTopicProducer.Add(topic);
                    return (null, null);
                }
                return (topic, p);
            }
            return (null, null);
        }

        protected override void PostStop()
        {
            base.PostStop();
            _client.DisposeAsync();
        }
        private object Deserialize(byte[] bytes)
        {
            return ((Serialization.Snapshot)_serializer.FromBinary(bytes, SnapshotType)).Data;
        }

        private byte[] Serialize(object snapshotData)
        {
            return _serializer.ToBinary(new Serialization.Snapshot(snapshotData));
        }
        private SnapshotEntry ToSnapshotEntry(SnapshotMetadata metadata, object snapshot)
        {
            var snapshotRep = new Serialization.Snapshot(snapshot);
            var serializer = _serialization.FindSerializerFor(snapshotRep);
            var binary = serializer.ToBinary(snapshotRep);

            var manifest = "";
            if (serializer is SerializerWithStringManifest stringManifest)
                manifest = stringManifest.Manifest(snapshotRep);
            else
                manifest = snapshotRep.GetType().TypeQualifiedName();

            return new SnapshotEntry
            {
                Id = metadata.PersistenceId + "_" + metadata.SequenceNr,
                PersistenceId = metadata.PersistenceId,
                SequenceNr = metadata.SequenceNr,
                Snapshot = binary,
                Timestamp = metadata.Timestamp.Ticks,
                Manifest = manifest,
                SerializerId = serializer.Identifier
            };
        }

        private SelectedSnapshot ToSelectedSnapshot(SnapshotEntry entry)
        {
            var legacy = entry.SerializerId > 0 || !string.IsNullOrEmpty(entry.Manifest);

            if (!legacy)
            {
                var ser = _serialization.FindSerializerForType(typeof(Serialization.Snapshot));
                var snapshot = ser.FromBinary<Serialization.Snapshot>((byte[])entry.Snapshot);
                return new SelectedSnapshot(new SnapshotMetadata(entry.PersistenceId, entry.SequenceNr), snapshot.Data);
            }

            int? serializerId = null;
            Type type = null;

            // legacy serialization
            if (!(entry.SerializerId > 0) && !string.IsNullOrEmpty(entry.Manifest))
                type = Type.GetType(entry.Manifest, true);
            else
                serializerId = entry.SerializerId;

            if (entry.Snapshot is byte[] bytes)
            {
                object deserialized;

                if (serializerId.HasValue)
                {
                    deserialized = _serialization.Deserialize(bytes, serializerId.Value, entry.Manifest);
                }
                else
                {
                    var deserializer = _serialization.FindSerializerForType(type);
                    deserialized = deserializer.FromBinary(bytes, type);
                }

                if (deserialized is Serialization.Snapshot snap)
                    return new SelectedSnapshot(
                        new SnapshotMetadata(entry.PersistenceId, entry.SequenceNr, new DateTime(entry.Timestamp)), snap.Data);

                return new SelectedSnapshot(
                    new SnapshotMetadata(entry.PersistenceId, entry.SequenceNr, new DateTime(entry.Timestamp)), deserialized);
            }

            // backwards compat - loaded an old snapshot using BSON serialization. No need to deserialize via Akka.NET
            return new SelectedSnapshot(
                new SnapshotMetadata(entry.PersistenceId, entry.SequenceNr, new DateTime(entry.Timestamp)), entry.Snapshot);
        }
    }
}
