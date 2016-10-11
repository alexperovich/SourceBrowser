using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public abstract partial class SolutionGenerator : IDisposable
    {
        public string SolutionSourceFolder { get; }
        public string SolutionDestinationFolder { get; }
        public string SolutionFilePath { get; }
        public string ServerPath { get; }
        public string NetworkShare { get; }
        private Federation Federation { get; set; }
        private readonly HashSet<string> typeScriptFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// List of all assembly names included in the index, from all solutions
        /// </summary>
        public HashSet<string> GlobalAssemblyList { get; set; }

        protected ISolution Solution { get; }

        protected SolutionGenerator(string solutionFilePath, string solutionDestinationFolder, string solutionSourceFolder, ISolution solution, string serverPath = null, string networkShare = null, Federation federation = null)
        {
            SolutionFilePath = solutionFilePath;
            SolutionDestinationFolder = solutionDestinationFolder;
            SolutionSourceFolder = solutionSourceFolder;
            ServerPath = serverPath;
            NetworkShare = networkShare;
            Solution = solution;
            Federation = federation;
        }

        public IEnumerable<string> GetAssemblyNames()
        {
            if (Solution != null)
            {
                return Solution.Projects.Select(p => p.AssemblyName);
            }
            else
            {
                return Enumerable.Empty<string>();
            }
        }

        public static string CurrentAssemblyName = null;

        /// <returns>true if only part of the solution was processed and the method needs to be called again, false if all done</returns>
        public async Task<bool> GenerateAsync(HashSet<string> processedAssemblyList = null, Folder<IProject> solutionExplorerRoot = null)
        {
            if (Solution == null)
            {
                // we failed to open the solution earlier; just return
                Log.Message("Solution is null: " + this.SolutionFilePath);
                return false;
            }

            var allProjects = Solution.Projects.ToArray();
            if (allProjects.Length == 0)
            {
                Log.Exception("Solution " + this.SolutionFilePath + " has 0 projects - this is suspicious");
            }

            var projectsToProcess = allProjects
                .Where(p => processedAssemblyList == null || !processedAssemblyList.Contains(p.AssemblyName))
                .ToArray();
            var currentBatch = projectsToProcess
                .ToArray();
            foreach (var project in currentBatch)
            {
                try
                {
                    CurrentAssemblyName = project.AssemblyName;

                    var generator = CreateProjectGenerator(this, project);
                    await generator.Generate();

                    File.AppendAllText(Paths.ProcessedAssemblies, project.AssemblyName + Environment.NewLine, Encoding.UTF8);
                    processedAssemblyList?.Add(project.AssemblyName);
                }
                finally
                {
                    CurrentAssemblyName = null;
                }
            }

            new TypeScriptSupport().Generate(typeScriptFiles, SolutionDestinationFolder);

            if (currentBatch.Length > 1)
            {
                AddProjectsToSolutionExplorer(
                    solutionExplorerRoot,
                    currentBatch);
            }

            return currentBatch.Length < projectsToProcess.Length;
        }

        protected abstract ProjectGenerator CreateProjectGenerator(SolutionGenerator solutionGenerator, IProject project);

        private void SetFieldValue(object instance, string fieldName, object value)
        {
            var type = instance.GetType();
            var fieldInfo = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            fieldInfo.SetValue(instance, null);
        }

        //public void GenerateExternalReferences(HashSet<string> assemblyList)
        //{
        //    var externalReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        //    foreach (var project in Solution.Projects)
        //    {
        //        var references = project.MetadataReferences
        //            .OfType<PortableExecutableReference>()
        //            .Where(m => File.Exists(m.FilePath))
        //            .Where(m => !assemblyList.Contains(Path.GetFileNameWithoutExtension(m.FilePath)))
        //            .Where(m => !IsPartOfSolution(Path.GetFileNameWithoutExtension(m.FilePath)))
        //            .Where(m => GetExternalAssemblyIndex(Path.GetFileNameWithoutExtension(m.FilePath)) == -1)
        //            .Select(m => Path.GetFullPath(m.FilePath));
        //        foreach (var reference in references)
        //        {
        //            externalReferences[Path.GetFileNameWithoutExtension(reference)] = reference;
        //        }
        //    }

        //    foreach (var externalReference in externalReferences)
        //    {
        //        Log.Write(externalReference.Key, ConsoleColor.Magenta);
        //        var solutionGenerator = new SolutionGenerator(
        //            externalReference.Value,
        //            Paths.SolutionDestinationFolder);
        //        solutionGenerator.Generate(assemblyList);
        //    }
        //}

        public bool IsPartOfSolution(string assemblyName)
        {
            if (GlobalAssemblyList == null)
            {
                // if for some reason we don't know a global list, assume everything is in the solution
                // this is better than the alternative
                return true;
            }

            return GlobalAssemblyList.Contains(assemblyName);
        }

        public int GetExternalAssemblyIndex(string assemblyName)
        {
            if (Federation == null)
            {
                return -1;
            }

            return Federation.GetExternalAssemblyIndex(assemblyName);
        }

        public void AddTypeScriptFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            filePath = Path.GetFullPath(filePath);
            this.typeScriptFiles.Add(filePath);
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        ~SolutionGenerator()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}