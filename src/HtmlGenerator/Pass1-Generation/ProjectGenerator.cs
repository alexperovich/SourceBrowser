using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public abstract partial class ProjectGenerator
    {
        public IProject Project { get; }
        public Dictionary<string, List<Tuple<string, long>>> SymbolIDToListOfLocationsMap { get; private set; }
        public Dictionary<ISourceSymbol, string> DeclaredSymbols { get; private set; }
        public Dictionary<ISourceSymbol, ISourceSymbol> BaseMembers { get; private set; }
        public MultiDictionary<ISourceSymbol, ISourceSymbol> ImplementedInterfaceMembers { get; set; }

        public string ProjectDestinationFolder { get; private set; }
        public string AssemblyName { get; private set; }
        public SolutionGenerator SolutionGenerator { get; private set; }
        public string ProjectSourcePath { get; set; }
        public string ProjectFilePath { get; private set; }
        public List<string> OtherFiles { get; set; }

        protected ProjectGenerator(SolutionGenerator solutionGenerator, IProject project) : this()
        {
            this.SolutionGenerator = solutionGenerator;
            this.Project = project;
            this.ProjectFilePath = project.FilePath ?? solutionGenerator.SolutionFilePath;
            this.DeclaredSymbols = new Dictionary<ISourceSymbol, string>();
            this.BaseMembers = new Dictionary<ISourceSymbol, ISourceSymbol>();
            this.ImplementedInterfaceMembers = new MultiDictionary<ISourceSymbol, ISourceSymbol>();
        }

        /// <summary>
        /// This constructor is used for non-C#/VB projects such as "MSBuildFiles"
        /// </summary>
        protected ProjectGenerator(string folderName, string solutionDestinationFolder) : this()
        {
            ProjectDestinationFolder = Path.Combine(solutionDestinationFolder, folderName);
            Directory.CreateDirectory(Path.Combine(ProjectDestinationFolder, Constants.ReferencesFileName));
        }

        private void AddHtmlFilesToRedirectMap()
        {
            var files = Directory
                            .GetFiles(ProjectDestinationFolder, "*.html", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var relativePath = file.Substring(ProjectDestinationFolder.Length + 1).Replace('\\', '/');
                relativePath = relativePath.Substring(0, relativePath.Length - 5); // strip .html
                AddFileToRedirectMap(relativePath);
                OtherFiles.Add(relativePath);
            }
        }

        private void AddFileToRedirectMap(string filePath)
        {
            lock (SymbolIDToListOfLocationsMap)
            {
                SymbolIDToListOfLocationsMap.Add(
                    SymbolIdService.GetId(filePath),
                    new List<Tuple<string, long>> { Tuple.Create(filePath, 0L) });
            }
        }

        private ProjectGenerator()
        {
            this.SymbolIDToListOfLocationsMap = new Dictionary<string, List<Tuple<string, long>>>();
            this.OtherFiles = new List<string>();
        }

        public async Task Generate()
        {
            try
            {
                if (string.IsNullOrEmpty(ProjectFilePath))
                {
                    Log.Exception("ProjectFilePath is empty: " + Project.ToString());
                    return;
                }

                ProjectDestinationFolder = GetProjectDestinationPath(Project, SolutionGenerator.SolutionDestinationFolder);
                if (ProjectDestinationFolder == null)
                {
                    Log.Exception("Errors evaluating project: " + Project.Id);
                    return;
                }

                Log.Write(ProjectDestinationFolder, ConsoleColor.DarkCyan);

                ProjectSourcePath = Paths.MakeRelativeToFolder(ProjectFilePath, SolutionGenerator.SolutionSourceFolder);

                if (File.Exists(Path.Combine(ProjectDestinationFolder, Constants.DeclaredSymbolsFileName + ".txt")))
                {
                    // apparently someone already generated a project with this assembly name - their assembly wins
                    Log.Exception(string.Format(
                        "A project with assembly name {0} was already generated, skipping current project: {1}",
                        this.AssemblyName,
                        this.ProjectFilePath), isSevere: false);
                    return;
                }

                if (Configuration.CreateFoldersOnDisk)
                {
                    Directory.CreateDirectory(ProjectDestinationFolder);
                }

                var documents = Project.Documents.Where(IncludeDocument).ToList();

                var generationTasks = Partitioner.Create(documents)
                    .GetPartitions(Environment.ProcessorCount)
                    .Select(partition =>
                        Task.Run(async () =>
                        {
                            using (partition)
                            {
                                while (partition.MoveNext())
                                {
                                  await GenerateDocument(partition.Current);
                                }
                            }
                        }));

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
                        SymbolIDToListOfLocationsMap);
                    GenerateReferencedAssemblyList();
                    GenerateUsedReferencedAssemblyList();
                    GenerateProjectExplorer();
                    GenerateNamespaceExplorer();
                    GenerateIndex();
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "Project generation failed for: " + ProjectSourcePath);
            }
        }

        public void GenerateNonProjectFolder()
        {
            AddHtmlFilesToRedirectMap();
            GenerateDeclarations();
            GenerateSymbolIDToListOfDeclarationLocationsMap(
                ProjectDestinationFolder,
                SymbolIDToListOfLocationsMap);
        }

        protected abstract void GenerateNamespaceExplorer();

        protected abstract DocumentGenerator CreateDocumentGenerator(ProjectGenerator projectGenerator, IDocument document);

        private async Task GenerateDocument(IDocument document)
        {
            try
            {
                var documentGenerator = CreateDocumentGenerator(this, document);
                await documentGenerator.GenerateAsync();
            }
            catch (Exception e)
            {
                Log.Exception(e, "Document generation failed for: " + (document.FilePath ?? document.ToString()));
            }
        }

        private void GenerateIndex()
        {
            Log.Write("Index.html...");
            var index = Path.Combine(ProjectDestinationFolder, "index.html");
            var sb = new StringBuilder();
            Markup.WriteProjectIndex(sb, Project.AssemblyName);
            File.WriteAllText(index, sb.ToString());
        }

        protected virtual bool IncludeDocument(IDocument document)
        {
            return true;
        }

        private string GetProjectDestinationPath(IProject project, string solutionDestinationPath)
        {
            var assemblyName = project.AssemblyName;
            if (assemblyName == "<Error>")
            {
                return null;
            }

            AssemblyName = SymbolIdService.GetAssemblyId(assemblyName);
            string subfolder = Path.Combine(solutionDestinationPath, AssemblyName);
            return subfolder;
        }
    }
}
