using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Mono.Options;
using SourceIndexer.Contracts;
using SourceIndexer.IndexDatabase;

namespace SourceIndexer
{
    class Program
    {
        public static int Verbosity { get; private set; }
        public static string Database { get; private set; }

        private static IServiceProvider BuildServiceProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging(config =>
            {
                config.AddConsole(options =>
                {
                    if (Console.IsOutputRedirected)
                    {
                        options.DisableColors = true;
                    }

                    options.Format = ConsoleLoggerFormat.Systemd;
                });
            });

            return services.BuildServiceProvider();
        }

        private static void Main(string[] args)
        {
            var provider = BuildServiceProvider();

            var languages = GetLanguages(provider);

            var program = new CommandSet("source-index")
            {
                new ResponseFileSource(),
                "usage: source-index COMMAND [OPTIONS]*",
                "",
                "Global Options:",
                {"v:", "verbosity", (int? v) => Verbosity = v ?? Verbosity + 1},
                {"d|database=", "database", db => Database = db },
                "",
                "Available Commands:",
            };
            foreach (var (name, language) in languages)
            {
                var commandName = "index-" + name;
                program.Add(new Command(commandName)
                {
                    Options = language.GetOptions(),
                    Run = commandArgs =>
                    {
                        RunIndex(provider, language, commandArgs).GetAwaiter().GetResult();
                    },
                });
            }

            program.Run(args);
        }

        private static async Task RunIndex(IServiceProvider provider, ILanguageIndexerPlugin language, IEnumerable<string> args)
        {
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var host = new IndexHostServer(loggerFactory);
            var cancel = new CancellationTokenSource();
            var hostTask = host.Run(cancel.Token);
            var options = new DbContextOptionsBuilder()
                .UseSqlite($"Data Source={Database}")
                .Options;
            var clientId = ClientId.Create(language.Name);
            var code = await Indexer.Run(host, options, language, clientId, args).ConfigureAwait(false);
            cancel.Cancel();
            await hostTask.ConfigureAwait(false);
        }

        private static IImmutableDictionary<string, ILanguageIndexerPlugin> GetLanguages(IServiceProvider provider)
        {
            var languages = ImmutableDictionary.CreateBuilder<string, ILanguageIndexerPlugin>();
            IEnumerable<string> GetPluginDirs()
            {
                if (Debugger.IsAttached)
                {
                    var searchDir = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "../../../../V2"));
                    foreach (var dir in Directory.EnumerateDirectories(searchDir))
                    {
                        var name = Path.GetFileName(dir);
                        if (name!.StartsWith("SourceIndexer.Plugin"))
                        {
                            yield return Path.Join(dir, "bin", "Debug", "netcoreapp3.0");
                        }
                    }
                }
                else
                {
                    var pluginsDir = Path.Join(AppContext.BaseDirectory, "plugins");
                    foreach (var dir in Directory.EnumerateDirectories(pluginsDir))
                    {
                        yield return Path.Join(dir);
                    }
                }
            }

            foreach (var dir in GetPluginDirs())
            {
                var dll = Directory.EnumerateFiles(dir)
                    .Single(f =>
                    {
                        var name = Path.GetFileName(f);
                        return name.StartsWith("SourceIndexer.Plugin") && name.EndsWith(".dll");
                    });
                var plugin = LoadPlugin(dll);
                languages.Add(plugin.Name, plugin);
            }

            return languages.ToImmutable();
        }

        private static ILanguageIndexerPlugin LoadPlugin(string pluginAssemblyPath)
        {
            var context = new PluginLoadContext(pluginAssemblyPath);
            var assembly = context.LoadFromAssemblyPath(pluginAssemblyPath);
            var type = assembly.ExportedTypes
                .Single(t => typeof(ILanguageIndexerPlugin).IsAssignableFrom(t));

            var value = Activator.CreateInstance(type);
            if (value == null)
            {
                throw new InvalidOperationException($"Plugin {pluginAssemblyPath} not valid.");
            }

            return (ILanguageIndexerPlugin)value;
        }
    }

    internal class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        public PluginLoadContext(string pluginPath)
        {
            resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var assemblyPath = resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }

            return IntPtr.Zero;
        }
    }
}
