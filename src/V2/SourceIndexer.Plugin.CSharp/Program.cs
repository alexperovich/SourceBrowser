using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Mono.Options;
using SourceIndexer.Contracts;

namespace SourceIndexer.Plugin.CSharp
{
    public class CSharpLanguageIndexerPlugin : ILanguageIndexerPlugin
    {
        private readonly Dictionary<string, string> properties = new Dictionary<string,string>();

        public string Name => "csharp";

        public OptionSet GetOptions()
        {
            return new OptionSet
            {
                "usage: source-index index-csharp [OPTIONS]* project",
                "",
                "Options:",
                {"p:", "Set MSBuild Properties", (k, v) => properties.Add(k, v)},
            };
        }

        public Task<(int pid, string settings)> LaunchIndexerProcessAsync(string serverUrl, string clientId, IEnumerable<string> args)
        {
            var pluginAssemblyPath = typeof(CSharpLanguageIndexerPlugin).Assembly.Location;
            var pluginExecutable = Path.ChangeExtension(pluginAssemblyPath, ".exe");

            var settings = new CSharpLanguageIndexerSettings(properties, args);

            var p = Process.Start(pluginExecutable, $"\"{serverUrl}\" \"{clientId}\"");
            return Task.FromResult((p.Id, JsonSerializer.Serialize(settings)));
        }
    }

    public class CSharpLanguageIndexerSettings
    {
        public CSharpLanguageIndexerSettings(IReadOnlyDictionary<string, string> properties, IEnumerable<string> args)
        {
            Properties = properties.ToImmutableDictionary();
            Arguments = args.ToImmutableArray();
        }

        public ImmutableArray<string> Arguments { get; }

        public ImmutableDictionary<string, string> Properties { get; }
    }

    public static class CSharpClassifications
    {
        public static readonly string Keyword = "kw";
        public static readonly string Comment = "c";
        public static readonly string StringLiteral = "s";
    }

    class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Expected ServerUrl and ClientId");
                return -1;
            }
            var serverUrl = args[0];
            var clientId = args[1];
            var client = HostServer.Connect(serverUrl, clientId);

            var parameters = await client.InitializeAsync(new InitializeData
            {
                Styles =
                {
                    [CSharpClassifications.Keyword] = new ClassificationStyle
                    {
                        Color = "blue",
                        FontWeight = "normal",
                    },
                    [CSharpClassifications.Comment] = new ClassificationStyle
                    {
                        Color = "#008000",
                    },
                    [CSharpClassifications.StringLiteral] = new ClassificationStyle
                    {
                        Color = "#A31515",
                    },
                },
            });
            return 0;
        }
    }
}
