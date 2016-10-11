using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.SourceBrowser.Common;

namespace Microsoft.SourceBrowser.HtmlGenerator.CppSupport
{
    internal class CppDocumentGenerator
    {
        private string _relativePathToRoot;
        private string documentRelativeFilePathWithoutHtmlExtension;
        private string documentDestinationFilePath;
        public IProjectGenerator ProjectGenerator { get; }
        public IDocument Document { get; }
        public ISourceText Text => Document.Text;

        public CppDocumentGenerator(IProjectGenerator projectGenerator, IDocument document)
        {
            ProjectGenerator = projectGenerator;
            Document = document;
        }

        public async Task GenerateAsync()
        {
            this.documentRelativeFilePathWithoutHtmlExtension = Paths.GetRelativeFilePathInProject(Document);
            this.documentDestinationFilePath = Path.Combine(ProjectGenerator.ProjectDestinationFolder, documentRelativeFilePathWithoutHtmlExtension) + ".html";

            this._relativePathToRoot = Paths.CalculateRelativePathToRoot(documentDestinationFilePath, ProjectGenerator.SolutionGenerator.SolutionDestinationFolder);

            // add the file itself as a "declared symbol", so that clicking on document in search
            // results redirects to the document
            ProjectGenerator.AddDeclaredSymbolToRedirectMap(
                SymbolIdService.GetId(this.Document),
                documentRelativeFilePathWithoutHtmlExtension,
                0);

            if (File.Exists(documentDestinationFilePath))
            {
                // someone already generated this file, likely a shared linked file from elsewhere
                return;
            }

            Log.Write(documentDestinationFilePath);

            try
            {
                var directoryName = Path.GetDirectoryName(documentDestinationFilePath);
                var sanitized = Paths.SanitizeFolder(directoryName);
                if (directoryName != sanitized)
                {
                    Log.Exception("Illegal characters in path: " + directoryName + " Project: " + ProjectGenerator.AssemblyName);
                }

                if (Configuration.CreateFoldersOnDisk)
                {
                    Directory.CreateDirectory(directoryName);
                }
            }
            catch (PathTooLongException)
            {
                // there's one case where a path is too long - we don't care enough about it
                return;
            }

            if (Configuration.WriteDocumentsToDisk)
            {
                using (var streamWriter = new StreamWriter(
                    documentDestinationFilePath,
                    append: false,
                    encoding: Encoding.UTF8))
                {
                    await GenerateHtml(streamWriter);
                }
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                using (var streamWriter = new StreamWriter(memoryStream))
                {
                    await GeneratePre(streamWriter);
                }
            }
        }

        private async Task GenerateHtml(StreamWriter writer)
        {
            var title = Document.Name;
            var lineCount = Text.Lines.Count;

            // if the document is very long, pregenerate line numbers statically
            // to make the page load faster and avoid JavaScript cost
            bool pregenerateLineNumbers = IsLargeFile(lineCount);

            // pass a value larger than 0 to generate line numbers in JavaScript (to reduce HTML size)
            var prefix = Markup.GetDocumentPrefix(title, _relativePathToRoot, pregenerateLineNumbers ? 0 : lineCount);
            writer.Write(prefix);
            var documentUrl = GenerateHeader(writer.WriteLine);

            // pass a value larger than 0 to generate line numbers statically at HTML generation time
            var table = Markup.GetTablePrefix(documentUrl, pregenerateLineNumbers ? lineCount : 0);
            writer.WriteLine(table);

            await GeneratePre(writer, lineCount);
            var suffix = Markup.GetDocumentSuffix();
            writer.WriteLine(suffix);
        }

        private string GenerateHeader(Action<string> writeLine)
        {
            string documentDisplayName = documentRelativeFilePathWithoutHtmlExtension;
            string documentUrl = "/#" + Document.Project.AssemblyName + "/" + documentRelativeFilePathWithoutHtmlExtension.Replace('\\', '/');
            string projectDisplayName = ProjectGenerator.ProjectSourcePath;
            string projectUrl = "/#" + Document.Project.AssemblyName;

            string documentLink =
                $"File: <a id=\"filePath\" class=\"blueLink\" href=\"{documentUrl}\" target=\"_top\">{documentDisplayName}</a><br/>";
            string projectLink =
                $"Project: <a id=\"projectPath\" class=\"blueLink\" href=\"{projectUrl}\" target=\"_top\">{projectDisplayName}</a> ({ProjectGenerator.AssemblyName})";

            string fileShareLink = GetFileShareLink();
            if (fileShareLink != null)
            {
                fileShareLink = Markup.A(fileShareLink, "File", "_blank");
            }
            else
            {
                fileShareLink = "";
            }

            string webLink = GetWebLink();
            if (webLink != null)
            {
                webLink = Markup.A(webLink, "Web&nbsp;Access", "_blank");
            }
            else
            {
                webLink = "";
            }

            string firstRow = $"<tr><td>{documentLink}</td><td>{webLink}</td></tr>";
            string secondRow = $"<tr><td>{projectLink}</td><td>{fileShareLink}</td></tr>";

            Markup.WriteLinkPanel(writeLine, firstRow, secondRow);

            return documentUrl;
        }

        private string GetWebLink()
        {
            var serverPath = ProjectGenerator.SolutionGenerator.ServerPath;
            if (string.IsNullOrEmpty(serverPath))
            {
                return null;
            }

            string filePath = GetDocumentPathFromSourceSolutionRoot();
            filePath = filePath.Replace('\\', '/');

            string urlTemplate = @"{0}{1}";

            string url = string.Format(
                urlTemplate,
                serverPath,
                filePath);
            return url;
        }

        private string GetDocumentPathFromSourceSolutionRoot()
        {
            string projectPath = Path.GetDirectoryName(ProjectGenerator.ProjectSourcePath);
            string filePath = @"C:\" + Path.Combine(projectPath, documentRelativeFilePathWithoutHtmlExtension);
            filePath = Path.GetFullPath(filePath);
            filePath = filePath.Substring(3); // strip the artificial "C:\"
            return filePath;
        }

        private string GetFileShareLink()
        {
            var networkShare = ProjectGenerator.SolutionGenerator.NetworkShare;
            if (string.IsNullOrEmpty(networkShare))
            {
                return null;
            }

            string filePath = GetDocumentPathFromSourceSolutionRoot();
            filePath = Path.Combine(networkShare, filePath);
            return filePath;
        }

        private async Task GeneratePre(StreamWriter writer, int lineCount = 0)
        {
            IClassifier classifier = new CppClassifier();
            var ranges = await classifier.Classify(Document, Text);
            if (ranges == null)
            {
                // if there was an error in Roslyn, don't fail the entire index, just return
                return;
            }

            foreach (var range in ranges)
            {
                string html = GenerateRange(writer, range, lineCount);
                writer.Write(html);
            }
        }

        private bool IsLargeFile(int lineCount)
        {
            return lineCount > 30000;
        }

        private string GenerateRange(StreamWriter writer, Classification.Range range, int lineCount = 0)
        {
            var html = range.Text;
            html = Markup.HtmlEscape(html);
            bool isLargeFile = IsLargeFile(lineCount);
            string classAttributeValue = GetClassAttribute(html, range, isLargeFile);
            HtmlElementInfo hyperlinkInfo = GenerateLinks(range, isLargeFile);

            if (hyperlinkInfo == null)
            {
                if (classAttributeValue == null || isLargeFile)
                {
                    return html;
                }

                if (classAttributeValue == "k")
                {
                    return "<b>" + html + "</b>";
                }

            }

            var sb = new StringBuilder();

            var elementName = "span";
            if (hyperlinkInfo != null)
            {
                elementName = hyperlinkInfo.Name;
            }

            sb.Append("<" + elementName);
            bool overridingClassAttributeSpecified = false;
            if (hyperlinkInfo != null)
            {
                foreach (var attribute in hyperlinkInfo.Attributes)
                {
                    AddAttribute(sb, attribute.Key, attribute.Value);
                    if (attribute.Key == "class")
                    {
                        overridingClassAttributeSpecified = true;
                    }
                }
            }

            if (!overridingClassAttributeSpecified)
            {
                AddAttribute(sb, "class", classAttributeValue);
            }

            sb.Append('>');

            html = AddIdSpanForImplicitConstructorIfNecessary(hyperlinkInfo, html);

            sb.Append(html);
            sb.Append("</" + elementName + ">");

            html = sb.ToString();

            if (hyperlinkInfo != null && hyperlinkInfo.DeclaredSymbol != null)
            {
                writer.Flush();
                long streamPosition = writer.BaseStream.Length;

                streamPosition += html.IndexOf(hyperlinkInfo.Attributes["id"] + ".html");
                ProjectGenerator.AddDeclaredSymbol(
                    hyperlinkInfo.DeclaredSymbol,
                    hyperlinkInfo.DeclaredSymbolId,
                    documentRelativeFilePathWithoutHtmlExtension,
                    streamPosition);
            }

            return html;
        }

        private HtmlElementInfo GenerateLinks(Classification.Range range, bool isLargeFile = false)
        {
            var text = range.Text;

            if (range.ClassificationType == Constants.ClassificationLiteral)
            {
                return TryProcessGuid(range);
            }

            if (range.ClassificationType != Constants.ClassificationIdentifier &&
                range.ClassificationType != Constants.ClassificationTypeName &&
                text != "this" &&
                text != "base" &&
                text != "var" &&
                text != "New" &&
                text != "new" &&
                text != "[" &&
                text != "partial" &&
                text != "Partial")
            {
                return null;
            }

            var position = range.ClassifiedSpan.TextSpan.Start;
            var token = Root.FindToken(position, findInsideTrivia: true);
            if (IsZeroLengthArrayAllocation(token))
            {
                ProjectGenerator.AddReference(
                    this.documentDestinationFilePath,
                    Text,
                    "mscorlib",
                    null,
                    range.ClassifiedSpan.TextSpan.Start,
                    range.ClassifiedSpan.TextSpan.End,
                    ReferenceKind.EmptyArrayAllocation);
                return null;
            }

            // now that we've passed the empty array allocation check, disable all further new keywords
            if (range.ClassificationType == Constants.ClassificationKeyword && text == "new")
            {
                return null;
            }

            var declaredSymbol = SemanticModel.GetDeclaredSymbol(token.Parent);
            if (declaredSymbol is IParameterSymbol && text == "this")
            {
                // it's a 'this' in the first parameter of an extension method - we don't want it to
                // hyperlink to anything
                return null;
            }

            if (declaredSymbol != null)
            {
                if (token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword) ||
                    token.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.PartialKeyword))
                {
                    if (declaredSymbol is INamedTypeSymbol)
                    {
                        return TryProcessPartialKeyword((INamedTypeSymbol)declaredSymbol);
                    }

                    return null;
                }

                var explicitlyImplementedMember = GetExplicitlyImplementedMember(declaredSymbol);
                if (explicitlyImplementedMember == null)
                {
                    if (token.Span.Contains(position) &&
                        (declaredSymbol.Kind == SymbolKind.Event ||
                         declaredSymbol.Kind == SymbolKind.Field ||
                         declaredSymbol.Kind == SymbolKind.Local ||
                         declaredSymbol.Kind == SymbolKind.Method ||
                         declaredSymbol.Kind == SymbolKind.NamedType ||
                         declaredSymbol.Kind == SymbolKind.Parameter ||
                         declaredSymbol.Kind == SymbolKind.Property ||
                         declaredSymbol.Kind == SymbolKind.TypeParameter
                            ) &&
                        DeclaredSymbols.Add(declaredSymbol))
                    {
                        if ((declaredSymbol.Kind == SymbolKind.Method ||
                             declaredSymbol.Kind == SymbolKind.Property ||
                             declaredSymbol.Kind == SymbolKind.Event) &&
                            !declaredSymbol.IsStatic)
                        {
                            // declarations of overridden members are also "references" to their
                            // base members. This is needed for "Find Overridding Members" and
                            // "Find Implementations"
                            AddReferencesToOverriddenMembers(range, token, declaredSymbol);
                            AddReferencesToImplementedMembers(range, token, declaredSymbol);
                        }

                        return ProcessDeclaredSymbol(declaredSymbol, isLargeFile);
                    }
                }
                else
                {
                    projectGenerator.AddImplementedInterfaceMember(
                        declaredSymbol,
                        explicitlyImplementedMember);
                    return ProcessReference(
                        range,
                        explicitlyImplementedMember,
                        ReferenceKind.InterfaceMemberImplementation);
                }
            }
            else
            {
                return ProcessReference(range, token, isLargeFile);
            }

            return null;
        }

        private HtmlElementInfo TryProcessGuid(Classification.Range range)
        {
            var text = range.Text;
            var spanStart = range.ClassifiedSpan.TextSpan.Start;
            var spanEnd = range.ClassifiedSpan.TextSpan.End;

            if (text.StartsWith("@"))
            {
                text = text.Substring(1);
                spanStart++;
            }

            if (text.StartsWith("\"") && text.EndsWith("\"") && text.Length >= 2)
            {
                spanStart++;
                spanEnd--;
                text = text.Substring(1, text.Length - 2);
            }

            // quick check to reject non-Guids even before trying to parse
            if (text.Length != 32 && text.Length != 36 && text.Length != 38)
            {
                return null;
            }

            Guid guid;
            if (!Guid.TryParse(text, out guid))
            {
                return null;
            }

            var symbolId = guid.ToString();

            var referencesFilePath = Path.Combine(
                ProjectGenerator.SolutionGenerator.SolutionDestinationFolder,
                Constants.GuidAssembly,
                Constants.ReferencesFileName,
                symbolId + ".html");
            string href = Paths.MakeRelativeToFile(referencesFilePath, documentDestinationFilePath);
            href = href.Replace('\\', '/');

            var link = new HtmlElementInfo
            {
                Name = "a",
                Attributes =
                {
                    { "href", href },
                    { "target", "n" },
                },
                DeclaredSymbolId = symbolId
            };

            projectGenerator.AddReference(
                this.documentDestinationFilePath,
                Text,
                Constants.GuidAssembly,
                null,
                symbolId,
                spanStart,
                spanEnd,
                ReferenceKind.GuidUsage);

            return link;
        }

        private string GetClassAttribute(string rangeText, Classification.Range range, bool isLargeFile = false)
        {
            string classificationType = range.ClassificationType;

            if (classificationType == null ||
                classificationType == Constants.ClassificationPunctuation)
            {
                return null;
            }

            if (range.ClassificationType == Constants.ClassificationLiteral)
            {
                return classificationType;
            }

            if (range.ClassificationType != Constants.ClassificationIdentifier &&
                range.ClassificationType != Constants.ClassificationTypeName &&
                rangeText != "this" &&
                rangeText != "base" &&
                rangeText != "var" &&
                rangeText != "New" &&
                rangeText != "new" &&
                rangeText != "[" &&
                rangeText != "partial" &&
                rangeText != "Partial")
            {
                return classificationType;
            }

            if (range.ClassificationType == Constants.ClassificationKeyword)
            {
                return classificationType;
            }

            var position = range.ClassifiedSpan.TextSpan.Start;
            var token = Root.FindToken(position, findInsideTrivia: true);

            var declaredSymbol = SemanticModel.GetDeclaredSymbol(token.Parent);
            if (declaredSymbol is IParameterSymbol && rangeText == "this")
            {
                return classificationType;
            }

            if (declaredSymbol != null)
            {
                return ClassFromSymbol(declaredSymbol, classificationType);
            }

            var node = GetBindableParent(token);
            if (token.ToString() == "[" &&
                token.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.BracketedArgumentListSyntax &&
                token.Parent.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.ElementAccessExpressionSyntax)
            {
                node = token.Parent.Parent;
            }

            if (node == null)
            {
                return classificationType;
            }

            var symbol = GetSymbol(node);
            if (symbol == null)
            {
                return classificationType;
            }

            return ClassFromSymbol(symbol, classificationType);
        }

        private string AddIdSpanForImplicitConstructorIfNecessary(HtmlElementInfo hyperlinkInfo, string html)
        {
            if (hyperlinkInfo != null && hyperlinkInfo.DeclaredSymbol != null)
            {
                INamedTypeSymbol namedTypeSymbol = hyperlinkInfo.DeclaredSymbol as INamedTypeSymbol;
                if (namedTypeSymbol != null)
                {
                    var implicitInstanceConstructor = namedTypeSymbol.Constructors.FirstOrDefault(c => !c.IsStatic && c.IsImplicitlyDeclared);
                    if (implicitInstanceConstructor != null)
                    {
                        var symbolId = SymbolIdService.GetId(implicitInstanceConstructor);
                        html = Markup.Tag("span", html, new Dictionary<string, string> { { "id", symbolId } });
                        projectGenerator.AddDeclaredSymbol(
                            implicitInstanceConstructor,
                            symbolId,
                            documentRelativeFilePathWithoutHtmlExtension,
                            0);
                    }
                }
            }

            return html;
        }

        private void AddAttribute(StringBuilder sb, string name, string value)
        {
            if (value != null)
            {
                sb.Append(" " + name + "=\"" + value + "\"");
            }
        }
    }
}