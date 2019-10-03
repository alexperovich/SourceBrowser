using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceIndexer.Contracts;

namespace SourceIndexer
{
    public class IndexHostServer : HostServer.HostServerBase
    {
        private readonly ILoggerFactory loggerFactory;
        private readonly ConcurrentDictionary<string, ILogger> languageLoggers = new ConcurrentDictionary<string, ILogger>();
        private readonly ConcurrentDictionary<ClientId, Indexer> indexers = new ConcurrentDictionary<ClientId, Indexer>();

        public int Port { get; }

        public IndexHostServer(ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
            Port = FreeTcpPort();
        }

        private static int FreeTcpPort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }


        public override Task<Empty> WriteLog(LogMessage request, ServerCallContext context)
        {
            var clientId = GetClientId(context);
            var logger = languageLoggers.GetOrAdd(clientId.LanguageName, loggerFactory.CreateLogger);

            var message = request.Message;
            if (!string.IsNullOrEmpty(request.ExceptionInfo))
            {
                message += "Exception Info:\n\n" + request.ExceptionInfo;
            }

            logger.Log(
                (LogLevel) request.LogLevel,
                message
            );
            return Task.FromResult<Empty>(null);
        }

        private static ClientId GetClientId(ServerCallContext context)
        {
            var clientIdHeader = context.RequestHeaders.First(e => e.Key == "X-Client-Id");
            var clientId = ClientId.Parse(clientIdHeader.Value);
            return clientId;
        }

        public async Task Run(CancellationToken cancellationToken)
        {
            var host = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.AddServerHeader = false;
                    options.ListenLocalhost(Port, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http2;
                    });
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton(this);
                    services.AddSingleton<IStartup, Startup>();
                })
                .Build();

            try
            {
                await host.StartAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(-1), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        public class Startup : IStartup
        {
            public void Configure(IApplicationBuilder app)
            {
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGrpcService<IndexHostServer>();
                });
            }

            public IServiceProvider ConfigureServices(IServiceCollection services)
            {
                services.AddGrpc();

                return services.BuildServiceProvider();
            }
        }

        public void RegisterIndexer(Indexer indexer, ClientId clientId)
        {
            if (!indexers.TryAdd(clientId, indexer))
            {
                throw new InvalidOperationException($"Indexer for client id {clientId} already exists.");
            }
        }

        private Indexer GetIndexer(ServerCallContext context)
        {
            var clientId = GetClientId(context);
            if (!indexers.TryGetValue(clientId, out var indexer))
            {
                throw new InvalidOperationException($"ClientId {clientId} not recognized.");
            }

            return indexer;
        }

        public override Task<Empty> Initialize(InitializeData request, ServerCallContext context)
        {
            var indexer = GetIndexer(context);
            return indexer.Initialize(request);
        }

        public override Task<ProjectId> BeginProject(Project request, ServerCallContext context)
        {
            var indexer = GetIndexer(context);
            return indexer.BeginProject(request);
        }

        public override Task<ProjectFileId> BeginFile(ProjectFile request, ServerCallContext context)
        {
            var indexer = GetIndexer(context);
            return indexer.BeginFile(request);
        }

        public override async Task<Empty> IndexToken(Token request, ServerCallContext context)
        {
            var indexer = GetIndexer(context);
            await indexer.IndexToken(request).ConfigureAwait(false);
            return null;
        }

        public override async Task<Empty> EndFile(ProjectFileId request, ServerCallContext context)
        {
            var indexer = GetIndexer(context);
            await indexer.EndFile(request).ConfigureAwait(false);
            return null;
        }

        public override async Task<Empty> EndProject(ProjectId request, ServerCallContext context)
        {
            var indexer = GetIndexer(context);
            await indexer.EndProject(request).ConfigureAwait(false);
            return null;
        }
    }
}