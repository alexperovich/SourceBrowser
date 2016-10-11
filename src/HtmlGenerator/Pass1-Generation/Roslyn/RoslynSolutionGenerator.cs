using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator.Roslyn
{
    class RoslynSolutionGenerator : SolutionGenerator
    {
        public RoslynSolutionGenerator(string solutionFilePath, string solutionDestinationFolder, string serverPath = null, ImmutableDictionary<string, string> properties = null, Federation federation = null)
            : base(solutionFilePath, solutionDestinationFolder, Path.GetDirectoryName(solutionFilePath), CreateSolution(solutionFilePath, properties), serverPath, null, federation)
        {
        }

        public RoslynSolutionGenerator(
            string solutionFilePath,
            string commandLineArguments,
            string outputAssemblyPath,
            string solutionSourceFolder,
            string solutionDestinationFolder,
            string serverPath,
            string networkShare)
            : base(solutionFilePath,
                   solutionDestinationFolder,
                   solutionSourceFolder,
                   CreateSolution(commandLineArguments,
                                  Path.GetFileNameWithoutExtension(solutionFilePath),
                                  solutionFilePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ? LanguageNames.VisualBasic : LanguageNames.CSharp,
                                  Path.GetDirectoryName(solutionFilePath),
                                  outputAssemblyPath),
                   serverPath,
                   networkShare)
        {
        }

        private static ISolution CreateSolution(
            string commandLineArguments,
            string projectName,
            string language,
            string projectSourceFolder,
            string outputAssemblyPath)
        {
            var workspace = CreateWorkspace();
            var projectInfo = CommandLineProject.CreateProjectInfo(
                projectName,
                language,
                commandLineArguments,
                projectSourceFolder,
                workspace);
            var solution = workspace.CurrentSolution.AddProject(projectInfo);

            solution = RemoveNonExistingFiles(solution);
            solution = AddAssemblyAttributesFile(language, outputAssemblyPath, solution);
            solution = DisambiguateSameNameLinkedFiles(solution);

            solution.Workspace.WorkspaceFailed += WorkspaceFailed;

            return new RoslynSolution(solution);
        }

        private static Solution DisambiguateSameNameLinkedFiles(Solution solution)
        {
            foreach (var projectId in solution.ProjectIds.ToArray())
            {
                var project = solution.GetProject(projectId);
                solution = DisambiguateSameNameLinkedFiles(project);
            }

            return solution;
        }

        /// <summary>
        /// If there are two linked files both outside the project cone, and they have same names,
        /// they will logically appear as the same file in the project root. To disambiguate, we
        /// remove both files from the project's root and re-add them each into a folder chain that
        /// is formed from the full path of each document.
        /// </summary>
        private static Solution DisambiguateSameNameLinkedFiles(Project project)
        {
            var nameMap = project.Documents.Where(d => !d.Folders.Any()).ToLookup(d => d.Name);
            foreach (var conflictedItemGroup in nameMap.Where(g => g.Count() > 1))
            {
                foreach (var conflictedDocument in conflictedItemGroup)
                {
                    project = project.RemoveDocument(conflictedDocument.Id);
                    string filePath = conflictedDocument.FilePath;
                    DocumentId newId = DocumentId.CreateNewId(project.Id, filePath);
                    var folders = filePath.Split('\\').Select(p => p.TrimEnd(':'));
                    project = project.Solution.AddDocument(
                        newId,
                        conflictedDocument.Name,
                        conflictedDocument.GetTextAsync().Result,
                        folders,
                        filePath).GetProject(project.Id);
                }
            }

            return project.Solution;
        }

        private static Solution RemoveNonExistingFiles(Solution solution)
        {
            foreach (var projectId in solution.ProjectIds.ToArray())
            {
                var project = solution.GetProject(projectId);
                solution = RemoveNonExistingDocuments(project);

                project = solution.GetProject(projectId);
                solution = RemoveNonExistingReferences(project);
            }

            return solution;
        }

        private static Solution RemoveNonExistingDocuments(Project project)
        {
            foreach (var documentId in project.DocumentIds.ToArray())
            {
                var document = project.GetDocument(documentId);
                if (!File.Exists(document.FilePath))
                {
                    Log.Message("Document doesn't exist on disk: " + document.FilePath);
                    project = project.RemoveDocument(documentId);
                }
            }

            return project.Solution;
        }

        private static Solution RemoveNonExistingReferences(Project project)
        {
            foreach (var metadataReference in project.MetadataReferences.ToArray())
            {
                if (!File.Exists(metadataReference.Display))
                {
                    Log.Message("Reference assembly doesn't exist on disk: " + metadataReference.Display);
                    project = project.RemoveMetadataReference(metadataReference);
                }
            }

            return project.Solution;
        }

        private static Solution AddAssemblyAttributesFile(string language, string outputAssemblyPath, Solution solution)
        {
            if (!File.Exists(outputAssemblyPath))
            {
                Log.Exception("AddAssemblyAttributesFile: assembly doesn't exist: " + outputAssemblyPath);
                return solution;
            }

            var assemblyAttributesFileText = MetadataReading.GetAssemblyAttributesFileText(
                assemblyFilePath: outputAssemblyPath,
                language: language);
            if (assemblyAttributesFileText != null)
            {
                var extension = language == LanguageNames.CSharp ? ".cs" : ".vb";
                var newAssemblyAttributesDocumentName = MetadataAsSource.GeneratedAssemblyAttributesFileName + extension;
                var existingAssemblyAttributesFileName = "AssemblyAttributes" + extension;

                var project = solution.Projects.First();
                if (project.Documents.All(d => d.Name != existingAssemblyAttributesFileName || d.Folders.Count != 0))
                {
                    var document = project.AddDocument(
                        newAssemblyAttributesDocumentName,
                        assemblyAttributesFileText,
                        filePath: newAssemblyAttributesDocumentName);
                    solution = document.Project.Solution;
                }
            }

            return solution;
        }

        protected override ProjectGenerator CreateProjectGenerator(SolutionGenerator solutionGenerator, IProject project)
        {
            return new RoslynProjectGenerator(solutionGenerator, project);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                ((RoslynSolution)Solution).Workspace?.Dispose();
            }
        }

        private static MSBuildWorkspace CreateWorkspace(ImmutableDictionary<string, string> propertiesOpt = null)
        {
            propertiesOpt = propertiesOpt ?? ImmutableDictionary<string, string>.Empty;

            // Explicitly add "CheckForSystemRuntimeDependency = true" property to correctly resolve facade references.
            // See https://github.com/dotnet/roslyn/issues/560
            propertiesOpt = propertiesOpt.Add("CheckForSystemRuntimeDependency", "true");
            propertiesOpt = propertiesOpt.Add("VisualStudioVersion", "14.0");

            return MSBuildWorkspace.Create(properties: propertiesOpt, hostServices: WorkspaceHacks.Pack);
        }

        private static ISolution CreateSolution(string solutionFilePath, ImmutableDictionary<string, string> properties = null)
        {
            try
            {
                Solution solution = null;
                if (solutionFilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    properties = AddSolutionProperties(properties, solutionFilePath);
                    var ws = CreateWorkspace(properties);
                    ws.SkipUnrecognizedProjects = true;
                    ws.WorkspaceFailed += WorkspaceFailed;
                    solution = ws.OpenSolutionAsync(solutionFilePath).GetAwaiter().GetResult();
                }
                else if (
                    solutionFilePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    solutionFilePath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
                {
                    var ws = CreateWorkspace(properties);
                    ws.WorkspaceFailed += WorkspaceFailed;
                    solution = ws.OpenProjectAsync(solutionFilePath).GetAwaiter().GetResult().Solution;
                }
                else if (
                    solutionFilePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    solutionFilePath.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase) ||
                    solutionFilePath.EndsWith(".netmodule", StringComparison.OrdinalIgnoreCase))
                {
                    solution = MetadataAsSource.LoadMetadataAsSourceSolution(solutionFilePath);
                    if (solution != null)
                    {
                        solution.Workspace.WorkspaceFailed += WorkspaceFailed;
                    }
                }

                if (solution == null)
                {
                    return null;
                }

                return new RoslynSolution(solution);
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "Failed to open solution: " + solutionFilePath);
                return null;
            }
        }

        private static ImmutableDictionary<string, string> AddSolutionProperties(ImmutableDictionary<string, string> properties, string solutionFilePath)
        {
            // http://referencesource.microsoft.com/#MSBuildFiles/C/ProgramFiles(x86)/MSBuild/14.0/bin_/amd64/Microsoft.Common.CurrentVersion.targets,296
            properties = properties ?? ImmutableDictionary<string, string>.Empty;
            properties = properties.Add("SolutionName", Path.GetFileNameWithoutExtension(solutionFilePath));
            properties = properties.Add("SolutionFileName", Path.GetFileName(solutionFilePath));
            properties = properties.Add("SolutionPath", solutionFilePath);
            properties = properties.Add("SolutionDir", Path.GetDirectoryName(solutionFilePath));
            properties = properties.Add("SolutionExt", Path.GetExtension(solutionFilePath));
            return properties;
        }

        private static void WorkspaceFailed(object sender, WorkspaceDiagnosticEventArgs e)
        {
            var message = e.Diagnostic.Message;
            if (message.StartsWith("Could not find file") || message.StartsWith("Could not find a part of the path"))
            {
                return;
            }

            if (message.StartsWith("The imported project "))
            {
                return;
            }

            if (message.Contains("because the file extension '.shproj'"))
            {
                return;
            }

            var project = ((Workspace)sender).CurrentSolution.Projects.FirstOrDefault();
            if (project != null)
            {
                message = message + " Project: " + project.Name;
            }

            Log.Exception("Workspace failed: " + message);
            Log.Write(message, ConsoleColor.Red);
        }

        private class RoslynSolution : ISolution
        {
            public Workspace Workspace => _solution.Workspace;
            private readonly Solution _solution;

            public RoslynSolution(Solution solution)
            {
                _solution = solution;
            }

            public IImmutableList<IProject> Projects
                => _solution.Projects.Select(p => new RoslynProject(p, this)).ToImmutableList<IProject>();

            public string FilePath => _solution.FilePath;
        }
    }
}
