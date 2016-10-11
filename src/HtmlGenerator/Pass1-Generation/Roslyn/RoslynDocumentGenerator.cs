using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.SourceBrowser.HtmlGenerator.Roslyn
{
    class RoslynDocumentGenerator : DocumentGenerator
    {
        public RoslynDocumentGenerator(ProjectGenerator projectGenerator, IDocument document)
            : base(projectGenerator, document)
        {
        }

        public SourceText Text;
        public SyntaxNode Root;
        public SemanticModel SemanticModel;
        public HashSet<ISymbol> DeclaredSymbols;
        public object SemanticFactsService;
        public object SyntaxFactsService;
        private Func<SemanticModel, SyntaxNode, CancellationToken, bool> isWrittenToDelegate;
        private Func<SyntaxToken, SyntaxNode> getBindableParentDelegate;

        public override async Task GenerateAsync()
        {
            if (Configuration.CalculateRoslynSemantics)
            {
                var doc = ((RoslynDocument) Document).Document;
                this.Text = await doc.GetTextAsync();
                this.Root = await doc.GetSyntaxRootAsync();
                this.SemanticModel = await doc.GetSemanticModelAsync();
                this.SemanticFactsService = WorkspaceHacks.GetSemanticFactsService(doc);
                this.SyntaxFactsService = WorkspaceHacks.GetSyntaxFactsService(doc);

                var semanticFactsServiceType = SemanticFactsService.GetType();
                var isWrittenTo = semanticFactsServiceType.GetMethod("IsWrittenTo");
                this.isWrittenToDelegate = (Func<SemanticModel, SyntaxNode, CancellationToken, bool>)
                    Delegate.CreateDelegate(typeof(Func<SemanticModel, SyntaxNode, CancellationToken, bool>), SemanticFactsService, isWrittenTo);

                var syntaxFactsServiceType = SyntaxFactsService.GetType();
                var getBindableParent = syntaxFactsServiceType.GetMethod("GetBindableParent");
                this.getBindableParentDelegate = (Func<SyntaxToken, SyntaxNode>)
                    Delegate.CreateDelegate(typeof(Func<SyntaxToken, SyntaxNode>), SyntaxFactsService, getBindableParent);

                this.DeclaredSymbols = new HashSet<ISymbol>();

                Interlocked.Increment(ref ProjectGenerator.DocumentCount);
                Interlocked.Add(ref ProjectGenerator.LinesOfCode, Text.Lines.Count);
                Interlocked.Add(ref ProjectGenerator.BytesOfCode, Text.Length);
            }
            await base.GenerateAsync();
        }
    }
}
