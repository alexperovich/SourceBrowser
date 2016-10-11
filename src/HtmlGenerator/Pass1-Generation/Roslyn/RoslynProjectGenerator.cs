using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator.Roslyn
{
    class RoslynProjectGenerator : ProjectGenerator
    {
        public RoslynProjectGenerator(SolutionGenerator solutionGenerator, IProject project)
            : base(solutionGenerator, project)
        {
        }

        protected override void GenerateNamespaceExplorer()
        {
            Log.Write("Namespace Explorer...");
            var symbols = this.DeclaredSymbols.Keys.Select(s => ((RoslynSourceSymbol)s).Symbol).OfType<INamedTypeSymbol>()
                .Select(s => new DeclaredSymbolInfo(s, this.AssemblyName));
            NamespaceExplorer.WriteNamespaceExplorer(this.AssemblyName, symbols, ProjectDestinationFolder);
        }

        protected override DocumentGenerator CreateDocumentGenerator(ProjectGenerator projectGenerator, IDocument document)
        {
            return new RoslynDocumentGenerator(projectGenerator, document);
        }
    }

    internal class RoslynSourceSymbol : ISourceSymbol
    {
        public ISymbol Symbol { get; }
    }
}
