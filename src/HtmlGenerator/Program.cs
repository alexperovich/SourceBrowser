using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.Common;
using Microsoft.CodeAnalysis.MSBuild;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Diagnostics;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public class Program
    {
        private static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            var projects = new List<string>();
            var properties = new Dictionary<string, string>();
            var emitAssemblyList = false;
            var force = false;
            var noBuiltInFederations = false;
            var offlineFederations = new Dictionary<string, string>();
            var federations = new HashSet<string>();
            var serverPathMappings = new Dictionary<string, string>();

            foreach (var arg in args)
            {
                if (arg.StartsWith("/out:"))
                {
                    Paths.SolutionDestinationFolder = Path.GetFullPath(arg.Substring("/out:".Length).StripQuotes());
                    continue;
                }

                if (arg.StartsWith("/serverPath:"))
                {
                    var mapping = arg.Substring("/serverPath:".Length).StripQuotes();
                    var parts = mapping.Split('=');
                    if (parts.Length != 2)
                    {
                        Log.Write($"Invalid Server Path: '{mapping}'", ConsoleColor.Red);
                        continue;
                    }
                    serverPathMappings.Add(Path.GetFullPath(parts[0]), parts[1]);
                    continue;
                }

                if (arg == "/force")
                {
                    force = true;
                    continue;
                }

                if (arg.StartsWith("/in:"))
                {
                    string inputPath = arg.Substring("/in:".Length).StripQuotes();
                    try
                    {
                        if (!File.Exists(inputPath))
                        {
                            continue;
                        }

                        string[] paths = File.ReadAllLines(inputPath);
                        foreach (string path in paths)
                        {
                            AddProject(projects, path);
                        }
                    }
                    catch
                    {
                        Log.Write("Invalid argument: " + arg, ConsoleColor.Red);
                    }

                    continue;
                }

                if (arg.StartsWith("/p:"))
                {
                    var match = Regex.Match(arg, "/p:(?<name>[^=]+)=(?<value>.+)");
                    if (match.Success)
                    {
                        var propertyName = match.Groups["name"].Value;
                        var propertyValue = match.Groups["value"].Value;
                        properties.Add(propertyName, propertyValue);
                        continue;
                    }
                }

                if (arg == "/assemblylist")
                {
                    emitAssemblyList = true;
                    continue;
                }

                if (arg == "/nobuiltinfederations")
                {
                    noBuiltInFederations = true;
                    Log.Message("Disabling built-in federations.");
                    continue;
                }

                if (arg.StartsWith("/federation:"))
                {
                    string server = arg.Substring("/federation:".Length);
                    Log.Message($"Adding federation '{server}'.");
                    federations.Add(server);
                    continue;
                }

                if (arg.StartsWith("/offlinefederation:"))
                {
                    var match = Regex.Match(arg, "/offlinefederation:(?<server>[^=]+)=(?<file>.+)");
                    if (match.Success)
                    {
                        var server = match.Groups["server"].Value;
                        var assemblyListFileName = match.Groups["file"].Value;
                        offlineFederations[server] = assemblyListFileName;
                        Log.Message($"Adding federation '{server}' (offline from '{assemblyListFileName}').");
                        continue;
                    }
                    
                    continue;
                }

                try
                {
                    AddProject(projects, arg);
                }
                catch (Exception ex)
                {
                    Log.Write("Exception: " + ex.ToString(), ConsoleColor.Red);
                }
            }

            if (projects.Count == 0)
            {
                PrintUsage();
                return;
            }

            AssertTraceListener.Register();
            AppDomain.CurrentDomain.FirstChanceException += FirstChanceExceptionHandler.HandleFirstChanceException;

            if (Paths.SolutionDestinationFolder == null)
            {
                Paths.SolutionDestinationFolder = Path.Combine(Microsoft.SourceBrowser.Common.Paths.BaseAppFolder, "Index");
            }

            Log.ErrorLogFilePath = Path.Combine(Paths.SolutionDestinationFolder, Log.ErrorLogFile);
            Log.MessageLogFilePath = Path.Combine(Paths.SolutionDestinationFolder, Log.MessageLogFile);

            // Warning, this will delete and recreate your destination folder
            Paths.PrepareDestinationFolder(force);

            using (Disposable.Timing("Generating website"))
            {
                var federation = noBuiltInFederations ? new Federation(null) : new Federation(federations);
                foreach (var entry in offlineFederations)
                {
                    federation.AddFederation(entry.Key, entry.Value);
                }

                IndexSolutions(projects, properties, federation, serverPathMappings);
                FinalizeProjects(emitAssemblyList, federation);
            }
        }

        private static void AddProject(List<string> projects, string path)
        {
            var project = Path.GetFullPath(path);
            if (IsSupportedProject(project))
            {
                projects.Add(project);
            }
            else
            {
                Log.Exception("Project not found or not supported: " + path, isSevere: false);
            }
        }

        private static bool IsSupportedProject(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            return filePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"Usage: HtmlGenerator "
                + @"[/out:<outputdirectory>] "
                + @"[/force] "
                + @"<pathtosolution1.csproj|vbproj|sln> [more solutions/projects..] "
                + @"[/in:<filecontaingprojectlist>] "
                + @"[/nobuiltinfederations] "
                + @"[/offlinefederation:server=assemblyListFile] "
                + @"[/assemblylist]");
        }

        private static readonly Folder<CodeAnalysis.Project> mergedSolutionExplorerRoot = new Folder<CodeAnalysis.Project>();

        private static void IndexSolutions(IEnumerable<string> solutionFilePaths, Dictionary<string, string> properties, Federation federation, Dictionary<string, string> serverPathMappings)
        {
            var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in solutionFilePaths)
            {
                using (Disposable.Timing("Reading assembly names from " + path))
                {
                    foreach (var assemblyName in AssemblyNameExtractor.GetAssemblyNames(path))
                    {
                        assemblyNames.Add(assemblyName);
                    }
                }
            }

            var typeForwards = new Dictionary<(string assemblyName, string typeName), string>();

            foreach (var path in solutionFilePaths)
            {
                using (Disposable.Timing($"Reading type forwards from {path}"))
                {
                    GetTypeForwardsAsync(path, typeForwards).Wait();
                }
            }

            foreach (var path in solutionFilePaths)
            {
                using (Disposable.Timing("Generating " + path))
                {
                    using (var solutionGenerator = new SolutionGenerator(
                        path,
                        Paths.SolutionDestinationFolder,
                        properties: properties.ToImmutableDictionary(),
                        federation: federation,
                        serverPathMappings: serverPathMappings,
                        typeForwards: typeForwards))
                    {
                        solutionGenerator.GlobalAssemblyList = assemblyNames;
                        solutionGenerator.Generate(solutionExplorerRoot: mergedSolutionExplorerRoot);
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        private static async Task GetTypeForwardsAsync(string path, Dictionary<(string assemblyName, string typeName), string> typeForwards)
        {
            var workspace = MSBuildWorkspace.Create();
            Solution solution;
            if (path.EndsWith(".sln"))
            {
                solution = await workspace.OpenSolutionAsync(path).ConfigureAwait(false);
            }
            else
            {
                solution = (await workspace.OpenProjectAsync(path).ConfigureAwait(false)).Solution;
            }

            var projects = solution.Projects.Select(p => p.FilePath).ToList();
            var assemblies = (await Task.WhenAll(projects.Select(GetAssemblyAsync))).Where(a => a != null).ToList();
            foreach (var assemblyFile in assemblies)
            {
                var thisAssemblyName = Path.GetFileNameWithoutExtension(assemblyFile);
                using (var peReader = new PEReader(File.ReadAllBytes(assemblyFile).ToImmutableArray()))
                {
                    var reader = peReader.GetMetadataReader();
                    foreach (var exportedTypeHandle in reader.ExportedTypes)
                    {
                        var exportedType = reader.GetExportedType(exportedTypeHandle);
                        ProcessExportedType(exportedType, reader, typeForwards, thisAssemblyName);
                    }
                }
            }
        }

        private static void ProcessExportedType(ExportedType exportedType, MetadataReader reader, Dictionary<(string assemblyName, string typeName), string> typeForwards, string thisAssemblyName)
        {
            if (!exportedType.IsForwarder) return;
            string GetFullName(ExportedType type)
            {
                Debug.Assert(type.IsForwarder);
                if (type.Implementation.Kind == HandleKind.AssemblyReference)
                {
                    var name = reader.GetString(type.Name);
                    var ns = type.Namespace.IsNil ? null : reader.GetString(type.Namespace);
                    var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                    return fullName;
                }
                if (type.Implementation.Kind == HandleKind.ExportedType)
                {
                    var name = reader.GetString(type.Name);
                    Debug.Assert(type.Namespace.IsNil);
                    return GetFullName(reader.GetExportedType((ExportedTypeHandle)type.Implementation)) + "." + name;
                }
                throw new NotSupportedException(type.Implementation.Kind.ToString());
            }
            string GetAssemblyName(ExportedType type)
            {
                Debug.Assert(type.IsForwarder);
                if (type.Implementation.Kind == HandleKind.AssemblyReference)
                {
                    return reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.Implementation).Name);
                }
                if (type.Implementation.Kind == HandleKind.ExportedType)
                {
                    return GetAssemblyName(reader.GetExportedType((ExportedTypeHandle)type.Implementation));
                }
                throw new NotSupportedException(type.Implementation.Kind.ToString());
            }
            typeForwards[(thisAssemblyName, "T:" + GetFullName(exportedType))] = GetAssemblyName(exportedType);
        }

        private static async Task<string> GetAssemblyAsync(string projectPath)
        {
            var collection = new ProjectCollection();

            var project = collection.LoadProject(projectPath, new Dictionary<string, string>(), "14.0");
            var instance = project.CreateProjectInstance();
            var manager = new BuildManager();

            var getTargetPathResult = manager.Build(new BuildParameters(), new BuildRequestData(instance, new[] { "GetTargetPath" }));
            if (getTargetPathResult.HasResultsForTarget("GetTargetPath") && getTargetPathResult.ResultsByTarget["GetTargetPath"].ResultCode == TargetResultCode.Success)
            {
                var targetPath = getTargetPathResult.ResultsByTarget["GetTargetPath"].Items.Select(i => i.GetMetadata("FullPath")).First();
                if (File.Exists(targetPath))
                {
                    return targetPath;
                }
            }

            var buildResult = manager.Build(new BuildParameters
            {
                DisableInProcNode = true,
                Loggers = new List<ILogger> { new ConsoleLogger(LoggerVerbosity.Quiet) },
            },
                new BuildRequestData(instance, new[] { "Build" }));
            if (buildResult.HasResultsForTarget("Build") && buildResult.ResultsByTarget["Build"].ResultCode == TargetResultCode.Success)
            {
                return buildResult.ResultsByTarget["Build"].Items.Select(i => i.GetMetadata("FullPath")).First();
            }
            return null;
        }

        private static void FinalizeProjects(bool emitAssemblyList, Federation federation)
        {
            GenerateLooseFilesProject(Constants.MSBuildFiles, Paths.SolutionDestinationFolder);
            GenerateLooseFilesProject(Constants.TypeScriptFiles, Paths.SolutionDestinationFolder);
            using (Disposable.Timing("Finalizing references"))
            {
                try
                {
                    var solutionFinalizer = new SolutionFinalizer(Paths.SolutionDestinationFolder);
                    solutionFinalizer.FinalizeProjects(emitAssemblyList, federation, mergedSolutionExplorerRoot);
                }
                catch (Exception ex)
                {
                    Log.Exception(ex, "Failure while finalizing projects");
                }
            }
        }

        private static void GenerateLooseFilesProject(string projectName, string solutionDestinationPath)
        {
            var projectGenerator = new ProjectGenerator(projectName, solutionDestinationPath);
            projectGenerator.GenerateNonProjectFolder();
        }
    }
}
