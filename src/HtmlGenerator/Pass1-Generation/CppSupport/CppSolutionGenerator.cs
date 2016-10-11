using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Build.BuildEngine;
using Microsoft.Build.Evaluation;
using Project = Microsoft.Build.Evaluation.Project;

namespace Microsoft.SourceBrowser.HtmlGenerator.CppSupport
{
    internal class CppSolutionGenerator : ISolutionGenerator
    {
        public string Path { get; }
        public string SolutionDestinationFolder { get; }
        public string SolutionSourceFolder => System.IO.Path.GetDirectoryName(Solution.FilePath);
        public ImmutableDictionary<string, string> Properties { get; }
        public HashSet<string> GlobalAssemblyList { get; set; }
        public ISolution Solution { get; }
        
        public CppSolutionGenerator(string path, string solutionDestinationFolder, ImmutableDictionary<string, string> properties)
        {
            Path = path;
            SolutionDestinationFolder = solutionDestinationFolder;
            Properties = properties;
            Solution = GetSolution(Path, Properties);
        }

        private static ISolution GetSolution(string path, IImmutableDictionary<string, string> properties)
        {
            if (path.EndsWith(".sln"))
            {
                return new CppSolution(path, properties);
            }
            return new CppProject(path, properties);
        }

        public void Dispose()
        {
        }

        public async Task GenerateAsync(Folder<IProject> solutionExplorerRoot)
        {
            foreach (var project in Solution.Projects)
            {
                var projectGenerator = new CppProjectGenerator(this, project);
                await projectGenerator.GenerateAsync();
                File.AppendAllText(Paths.ProcessedAssemblies, project.AssemblyName + Environment.NewLine, Encoding.UTF8);
            }
            //TODO: AddProjectsToSolutionExplorer
            throw new NotImplementedException();
        }
    }

    internal class CppClassifier : IClassifier
    {
        public Task<IEnumerable<Classification.Range>> Classify(IDocument document, ISourceText text)
        {
            throw new NotImplementedException();
        }
    }

    internal interface IClassifier
    {
        Task<IEnumerable<Classification.Range>> Classify(IDocument document, ISourceText text);
    }

    internal class CppProject : ISolution, IProject
    {
        public string FilePath { get; }
        public ProjectCollection ProjectCollection { get; }

        public CppProject(string filePath, IImmutableDictionary<string, string> properties)
            : this(filePath, new ProjectCollection(properties.ToDictionary(p => p.Key, p => p.Value)))
        {
        }

        public CppProject(string filePath, ProjectCollection projectCollection)
        {
            FilePath = filePath;
            ProjectCollection = projectCollection;
            Projects = ImmutableList.Create<IProject>(this);
            Project = ProjectCollection.LoadProject(filePath);
            Documents = Project.GetItems("ClCompile").Select(i => new CppDocument(i, this)).ToImmutableList<IDocument>();
        }

        public IImmutableList<IDocument> Documents { get; }
        public ISolution Solution => this;
        public Project Project { get; }
        public IImmutableList<IProject> Projects { get; }
        public string AssemblyName => Path.GetFileNameWithoutExtension(FilePath);
        public string Name => Path.GetFileNameWithoutExtension(FilePath);
    }

    internal class CppDocument : IDocument
    {
        public CppDocument(ProjectItem item, IProject project)
        {
            Name = item.GetMetadataValue("Filename") + item.GetMetadataValue("Extension");
            FilePath = item.GetMetadataValue("FullPath");
            Folders = item.GetMetadataValue("RelativeDir")
                .Split(new[] {'\\'}, StringSplitOptions.RemoveEmptyEntries)
                .ToImmutableList();
            Text = new CppSourceText(FilePath);
            Project = project;
        }

        public string Name { get; }
        public string FilePath { get; }
        public IReadOnlyList<string> Folders { get; }
        public ISourceText Text { get; }
        public IProject Project { get; }
    }

    internal class CppSourceText : ISourceText
    {
        public CppSourceText(string filePath)
        {
            throw new NotImplementedException();
        }
    }

    internal class CppSolution : ISolution
    {
        public CppSolution(string path, IImmutableDictionary<string, string> properties)
        {
            FilePath = path;
            var wrapperProject = SolutionWrapperProject.Generate(path, "14.0", null);
            ProjectCollection = new ProjectCollection(properties.ToDictionary(p => p.Key, p => p.Value));
            using (var xmlReader = new XmlTextReader(new StringReader(wrapperProject)))
            {
                SolutionProject = ProjectCollection.LoadProject(xmlReader);
                Projects = AllProjects(SolutionProject).Select(p => new CppProject(p, ProjectCollection)).ToImmutableList<IProject>();
            }
        }

        private IEnumerable<string> AllProjects(Project solution)
        {
            int buildLevel = 0;
            while (true)
            {
                var projectItems = solution.GetItems($"BuildLevel{buildLevel++}");
                if (projectItems.Count == 0)
                {
                    yield break;
                }
                foreach (var projectItem in projectItems)
                {
                    var path = projectItem.GetMetadataValue("FullPath");
                    if (path.EndsWith(".vcxproj"))
                    {
                        yield return path;
                    }
                }
            }
        }

        public Project SolutionProject { get; set; }

        public ProjectCollection ProjectCollection { get; }

        public IImmutableList<IProject> Projects { get; }
        public string FilePath { get; }
    }
}
