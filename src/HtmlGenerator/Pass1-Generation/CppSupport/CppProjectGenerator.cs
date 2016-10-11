using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator.CppSupport
{
    internal class CppProjectGenerator : IProjectGenerator
    {
        public ISolutionGenerator SolutionGenerator { get; }
        public IProject Project { get; }
        public Dictionary<string, List<Tuple<string, long>>> SymbolToListOfLocationsMap { get; }
        public string ProjectDestinationFolder { get; private set; }
        public string AssemblyName => Project.AssemblyName;
        public string ProjectSourcePath { get; }

        public CppProjectGenerator(ISolutionGenerator solutionGenerator, IProject project)
        {
            SolutionGenerator = solutionGenerator;
            Project = project;
            DeclaredSymbols = new Dictionary<string, string>();
            SymbolToListOfLocationsMap = new Dictionary<string, List<Tuple<string, long>>>();
        }

        public Dictionary<string, string> DeclaredSymbols { get; }

        public async Task GenerateAsync()
        {
            ProjectDestinationFolder = GetProjectDestinationPath();
            Log.Write(ProjectDestinationFolder, ConsoleColor.DarkCyan);
            var projectSourcePath = Paths.MakeRelativeToFolder(Project.FilePath, SolutionGenerator.SolutionSourceFolder);

            if (File.Exists(Path.Combine(ProjectDestinationFolder, Constants.DeclaredSymbolsFileName + ".txt")))
            {
                // apparently someone already generated a project with this assembly name - their assembly wins
                Log.Exception(
                    $"A project with assembly name {Project.AssemblyName} was already generated, skipping current project: {Project.FilePath}", isSevere: false);
                return;
            }

            if (Configuration.CreateFoldersOnDisk)
            {
                Directory.CreateDirectory(ProjectDestinationFolder);
            }

            var documents = Project.Documents;

            var generationTasks = Partitioner.Create(documents)
                .GetPartitions(Environment.ProcessorCount)
                .Select(partition =>
                    Task.Run((Func<Task>) (async () =>
                    {
                        using (partition)
                        {
                            while (partition.MoveNext())
                            {
                                await GenerateDocument(partition.Current);
                            }
                        }
                    })));

            await Task.WhenAll(generationTasks);

            foreach (var document in documents)
            {
                OtherFiles.Add(Paths.GetRelativeFilePathInProject(document));
            }

            if (Configuration.WriteProjectAuxiliaryFilesToDisk)
            {
                GenerateProjectFile();
                GenerateDeclarations();
                GenerateBaseMembers();
                GenerateImplementedInterfaceMembers();
                GenerateProjectInfo();
                GenerateReferencesDataFiles(
                    this.SolutionGenerator.SolutionDestinationFolder,
                    ReferencesByTargetAssemblyAndSymbolId);
                GenerateSymbolIDToListOfDeclarationLocationsMap(
                    ProjectDestinationFolder,
                    SymbolIdToListOfLocationsMap);
                GenerateReferencedAssemblyList();
                GenerateUsedReferencedAssemblyList();
                GenerateProjectExplorer();
                GenerateNamespaceExplorer();
                GenerateIndex();
            }

            throw new NotImplementedException();
        }

        protected string GetProjectDestinationPath()
        {
            return Path.Combine(SolutionGenerator.SolutionDestinationFolder, Project.AssemblyName);
        }

        private Task GenerateDocument(IDocument document)
        {
            try
            {
                var documentGenerator = new CppDocumentGenerator(this, document);
                return documentGenerator.GenerateAsync();
            }
            catch (Exception e)
            {
                Log.Exception(e, "Document generation failed for: " + (document.FilePath ?? document.ToString()));
                return Task.FromResult(e);
            }
        }

        public void AddDeclaredSymbolToRedirectMap(
            string symbolId,
            string documentRelativeFilePath,
            long positionInFile)
        {
            List<Tuple<string, long>> bucket = null;
            lock (SymbolToListOfLocationsMap)
            {
                if (!SymbolToListOfLocationsMap.TryGetValue(symbolId, out bucket))
                {
                    bucket = new List<Tuple<string, long>>();
                    SymbolToListOfLocationsMap.Add(symbolId, bucket);
                }
            }

            lock (bucket)
            {
                bucket.Add(Tuple.Create(documentRelativeFilePath, positionInFile));
            }
        }

        public void AddDeclaredSymbol(ISourceSymbol declaredSymbol, string declaredSymbolId,
            string documentRelativeFilePathWithoutHtmlExtension, long streamPosition)
        {
            throw new NotImplementedException();
        }

        public void AddReference(string documentDestinationPath, ISourceText text, string destinationAssemblyName, ISourceSymbol symbol,
            int start, int end, ReferenceKind kind)
        {
            throw new NotImplementedException();
        }
    }
}