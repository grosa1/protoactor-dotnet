﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.ECS;
using Microsoft.Extensions.Logging;
using Proto;
using Proto.Cluster;
using Proto.Cluster.AmazonECS;
using Proto.Cluster.Gossip;
using Proto.Cluster.Identity;
using Proto.Cluster.Identity.Redis;
using Proto.Cluster.Partition;
using Proto.Remote;
using Proto.Remote.GrpcCore;
using StackExchange.Redis;

namespace EcsDiagnostics
{
    public static class Program
    {
        public static async Task Main()
        {
            Console.WriteLine("Starting...");
            
            var l = LoggerFactory.Create(c => c.AddConsole().SetMinimumLevel(LogLevel.Information));
            Log.SetLoggerFactory(l);
            var log = Log.CreateLogger("main");

            var identity = new PartitionIdentityLookup(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(2));//  new IdentityStorageLookup(GetRedisId("MyCluster"));
            
            /*
            - name: "REDIS"
              value: "redis"
            - name: PROTOPORT
              value: "8080"
            - name: PROTOHOST
              value: "0.0.0.0"
            - name: "PROTOHOSTPUBLIC"
             */

            var port = int.Parse(Environment.GetEnvironmentVariable("PROTOPORT") ?? "0");
            var host = Environment.GetEnvironmentVariable("PROTOHOST") ?? "127.0.0.1";
            var advertisedHost = Environment.GetEnvironmentVariable("PROTOHOSTPUBLIC");

            log.LogInformation("Host {Host}", host);
            log.LogInformation("Port {Port}", port);
            log.LogInformation("Advertised Host {AdvertisedHost}", advertisedHost);

            var clusterProvider = await GetProvider();

            var system = new ActorSystem(new ActorSystemConfig()
               //     .WithDeveloperReceiveLogging(TimeSpan.FromSeconds(1))
               //     .WithDeveloperSupervisionLogging(true)
                )
                .WithRemote(GrpcCoreRemoteConfig
                    .BindTo(host, port)
                    .WithAdvertisedHost(advertisedHost)
                    .WithEndpointWriterMaxRetries(2)
                )
                .WithCluster(ClusterConfig
                    .Setup("mycluster", clusterProvider, identity)
                    .WithClusterKind("empty", Props.Empty)
                );

            system.EventStream.Subscribe<GossipUpdate>(e => {
                    Console.WriteLine($"{DateTime.Now:O} Gossip update Member {e.MemberId} Key {e.Key}");
                }
            );
            
            system.EventStream.Subscribe<ClusterTopology>(e => {
                var members = e.Members;
                var x = members.Select(m => m.Id).OrderBy(i => i).ToArray();
                var key = string.Join("", x);
                var hash = MurmurHash2.Hash(key);

                Console.WriteLine($"{DateTime.Now:O} My members {hash}");

                // foreach (var member in members.OrderBy(m => m.Id))
                // {
                //     Console.WriteLine(member.Id + "\t" + member.Address + "\t" + member.Kinds);
                // }
            }
            );

            await system
                .Cluster()
                .StartMemberAsync();

            var props = Props.FromFunc(ctx => Task.CompletedTask);
            system.Root.SpawnNamed(props, "dummy");


            while (true)
            {
                var res = await system.Cluster().MemberList.TopologyConsensus(CancellationTokens.FromSeconds(5));

                var m = system.Cluster().MemberList.GetAllMembers();
                var hash = Member.TopologyHash(m);
                
                Console.WriteLine($"{DateTime.Now:O} Consensus {res}.. Hash {hash} Count {m.Length}");

                foreach (var member in m)
                {
                    var pid = new PID(member.Address,"dummy");

                    try
                    {
                        var t = await system.Root.RequestAsync<Touched>(pid, new Touch(), CancellationTokens.FromSeconds(1));

                        if (t != null)
                        {
                            Console.WriteLine($"called dummy actor {pid}");
                        }
                        else
                        {
                            Console.WriteLine($"call to dummy actor timed out {pid}");
                        }
                    }
                    catch
                    {
                        Console.WriteLine($"Could not call dummy actor {pid}");
                    }
                }

                await Task.Delay(3000);
            }
        }

        private static async Task<IClusterProvider> GetProvider()
        {
            var secrets = await AwsSecretsManager.GetSecret("api");
            Console.WriteLine(secrets.ApiKey);
            Console.WriteLine(secrets.ApiSecret);
            
            var client = new AmazonECSClient(secrets.ApiKey, secrets.ApiSecret, new AmazonECSConfig()
                {
                    RegionEndpoint = RegionEndpoint.EUNorth1,
                }
            );

            Console.WriteLine("Running with ECS Provider");
            return new AmazonEcsProvider(client, "default", new AmazonEcsProviderConfig(2, false));
        }

        private static IIdentityStorage GetRedisId(string clusterName)
        {
            var connectionString =
                Environment.GetEnvironmentVariable("REDIS");

            Console.WriteLine("REDIS " + connectionString);

            var multiplexer = ConnectionMultiplexer.Connect(connectionString);
            var identity = new RedisIdentityStorage(clusterName, multiplexer);
            return identity;
        }
    }
}