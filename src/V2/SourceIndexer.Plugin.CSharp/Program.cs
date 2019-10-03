using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Mono.Options;
using SourceIndexer.Contracts;
using Project = SourceIndexer.Contracts.Project;

namespace SourceIndexer.Plugin.CSharp
{
    public class CSharpLanguageIndexerPlugin : ILanguageIndexerPlugin
    {
        private readonly Dictionary<string, string> properties = new Dictionary<string,string>();

        public string Name => "csharp";

        public OptionSet GetOptions()
        {
            return new OptionSet
            {
                "usage: source-index index-csharp [OPTIONS]* project",
                "",
                "Options:",
                {"p:", "Set MSBuild Properties", (k, v) => properties.Add(k, v)},
            };
        }

        public Task<int> LaunchIndexerProcessAsync(string serverUrl, string clientId, IEnumerable<string> args)
        {
            var pluginAssemblyPath = typeof(CSharpLanguageIndexerPlugin).Assembly.Location;
            var pluginExecutable = Path.ChangeExtension(pluginAssemblyPath, ".exe");

            var settings = new CSharpLanguageIndexerSettings(properties, args);
            var settingsString = LanguageIndexerPlugin.SerializeSettings(settings);

            var p = Process.Start(pluginExecutable, $"\"{serverUrl}\" \"{clientId}\" \"{settingsString}\"");
            return Task.FromResult(p.Id);
        }
    }

    public class CSharpLanguageIndexerSettings
    {
        public CSharpLanguageIndexerSettings(IReadOnlyDictionary<string, string> properties, IEnumerable<string> args)
        {
            Properties = properties.ToImmutableDictionary();
            Arguments = args.ToImmutableArray();
        }

        public ImmutableArray<string> Arguments { get; }

        public ImmutableDictionary<string, string> Properties { get; }
    }

    public static class CSharpClassifications
    {
        public const string Keyword = "k";
        public const string Comment = "c";
        public const string StringLiteral = "s";
        public const string Identifier = "i";
        public const string TypeName = "t";
        public const string ExcludedCode = "x";
        public const string Operator = "o";
    }

    public static class CSharpSymbolKinds
    {
        public const string Class = "c";
        public const string Interface = "i";
        public const string Method = "m";
        public const string Namespace = "n";
        public const string Delegate = "d";
    }

    class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.WriteLine("Expected ServerUrl and ClientId");
                return -1;
            }
            var serverUrl = args[0];
            var clientId = args[1];
            var settingsString = args[2];
            var settings = LanguageIndexerPlugin.DeserializeSettings<CSharpLanguageIndexerSettings>(settingsString);
            var client = HostServer.Connect(serverUrl, clientId);

            await client.InitializeAsync(new InitializeData
            {
                Styles =
                {
                    [CSharpClassifications.Keyword] = new ClassificationStyle
                    {
                        Color = "blue",
                        FontWeight = "normal",
                    },
                    [CSharpClassifications.Comment] = new ClassificationStyle
                    {
                        Color = "#008000",
                    },
                    [CSharpClassifications.StringLiteral] = new ClassificationStyle
                    {
                        Color = "#A31515",
                    },
                    [CSharpClassifications.TypeName] = new ClassificationStyle
                    {
                        Color = "#2B91AF",
                    },
                    [CSharpClassifications.ExcludedCode] = new ClassificationStyle
                    {
                        Color = "#808080",
                    },
                    [CSharpClassifications.Operator] = new ClassificationStyle
                    {
                    },
                    [CSharpClassifications.Identifier] = new ClassificationStyle
                    {
                    },
                },
            });

            var workspace = new AdhocWorkspace();
            var project = workspace.AddProject("test", LanguageNames.CSharp);
            var projectId = await client.BeginProjectAsync(new Project
            {
                Path = "src/test",
            });

            project = project.AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

            var tree = CSharpSyntaxTree.ParseText(@"
using System;
namespace Test {
  interface ILogger {
    int Prop { get; set; }
    void Test(int a, Program p);
  }
  class T : ILogger {
    public int Prop { get; set; }
    public void Test(int a, Program p)
	{
	  Prop = a;
	}
  }
  class Program {
    static void Main(string[] args) {
      Console.WriteLine(""Hello World!"");
    }
  }
}
");

            var document = project.AddDocument("foo.cs", tree.GetRoot());
            var text = await document.GetTextAsync().ConfigureAwait(false);

            var fileId = await client.BeginFileAsync(new ProjectFile
            {
                Path = "foo.cs",
                Length = text.Length,
                LineCount = text.Lines.Count,
                ProjectId = projectId.Id,
            });


            await IndexFileSpansAsync(client, fileId, document, text).ConfigureAwait(false);


            await client.EndFileAsync(fileId);



            await client.EndProjectAsync(projectId);
            return 0;
        }

        private static async Task IndexFileSpansAsync(HostServer.HostServerClient client, ProjectFileId fileId, Document document, SourceText text)
        {
            var semanticModel = await document.GetSemanticModelAsync().ConfigureAwait(false);
            var syntaxRoot = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            var fullSpan = TextSpan.FromBounds(0, text.Length);


            var classified = await Classifier.GetClassifiedSpansAsync(document, fullSpan).ConfigureAwait(false);

            var spans = ClassificationMapper.MapClassifiedSpans(classified, text);

            foreach (var span in spans)
            {
                Symbol? declaredSymbol = null;
                Symbol? implementedSymbol = null;
                Symbol? referencedSymbol = null;
                switch (span.ClassificationType)
                {
                    case CSharpClassifications.Identifier:
                    case CSharpClassifications.TypeName:
                        (declaredSymbol, implementedSymbol) = GetDeclaredAndImplementedSymbol(span, text, syntaxRoot, semanticModel); 
                        if (declaredSymbol == null)
                        {
                            goto case CSharpClassifications.Operator;
                        }
                        else
                        {
                            goto case "";
                        }
                    case CSharpClassifications.Operator:
                        //TODO: referenced symbols
                        break;
                    case CSharpClassifications.Keyword:
                    case CSharpClassifications.Comment:
                    case CSharpClassifications.StringLiteral:
                    case CSharpClassifications.ExcludedCode:
                    case "":
                        await client.IndexTokenAsync(new Token
                        {
                            Classification = span.ClassificationType,
                            ProjectFileId = fileId.Id,
                            Text = text.GetSubText(span.TextSpan).ToString(),
                            DeclaredSymbol = declaredSymbol,
                            ImplementedSymbol = implementedSymbol,
                            ReferencedSymbol = referencedSymbol,
                        });
                        break;
                    default:
                        throw new NotSupportedException($"Unknown classification type '{span.ClassificationType}'");
                }
            }
        }

        private static (Symbol? declared, Symbol? implemented) GetDeclaredAndImplementedSymbol(ClassifiedSpan span, SourceText text, SyntaxNode syntaxRoot, SemanticModel semanticModel)
        {
            var token = syntaxRoot.FindToken(span.TextSpan.Start);
            var declaredSymbol = semanticModel.GetDeclaredSymbol(token.Parent);

            if (declaredSymbol == null)
            {
                return (null, null);
            }

            if (declaredSymbol is IParameterSymbol parameter && parameter.IsThis)
            {
                return (null, null);
            }

            return (
                new Symbol
                {
                    Name = declaredSymbol.GetDocumentationCommentId(),

                },
                GetImplementedSymbol(declaredSymbol)
            );
        }

        private static Symbol? GetImplementedSymbol(ISymbol symbol)
        {
            if (symbol is IMethodSymbol methodSymbol)
            {
                return methodSymbol.ExplicitInterfaceImplementations.FirstOrDefault();
            }

            if (symbol is IPropertySymbol propertySymbol)
            {
                return propertySymbol.ExplicitInterfaceImplementations.FirstOrDefault();
            }

            if (symbol is IEventSymbol eventSymbol)
            {
                return eventSymbol.ExplicitInterfaceImplementations.FirstOrDefault();
            }

            return null;
        }
    }

    public static class ClassificationMapper
    {
        public static IEnumerable<ClassifiedSpan> MapClassifiedSpans(IEnumerable<ClassifiedSpan> spans, SourceText text)
        {
            spans = FilterBadClassifications(spans);
            spans = GetFirstClassificationForEachSpan(spans);
            spans = FillGaps(spans, text);
            spans = MapClassificationTypes(spans);
            spans = MergeMergeableSpans(spans);

            return spans;
        }

        internal static IEnumerable<ClassifiedSpan> FilterBadClassifications(IEnumerable<ClassifiedSpan> spans)
        {
            foreach (var span in spans)
            {
                if (span.ClassificationType == ClassificationTypeNames.StaticSymbol)
                {
                    continue;
                }
                yield return span;
            }
        }

        internal static IEnumerable<ClassifiedSpan> GetFirstClassificationForEachSpan(IEnumerable<ClassifiedSpan> spans)
        {
            TextSpan cur = new TextSpan(0, 0);
            foreach (var span in spans)
            {
                if (span.TextSpan != cur)
                {
                    cur = span.TextSpan;
                    yield return span;
                }
            }
        }

        internal static IEnumerable<ClassifiedSpan> FillGaps(IEnumerable<ClassifiedSpan> spans, SourceText text)
        {
            int prevEnd = 0;
            foreach (var cs in spans)
            {
                if (prevEnd != cs.TextSpan.Start)
                {
                    yield return new ClassifiedSpan("", TextSpan.FromBounds(prevEnd, cs.TextSpan.Start));
                }

                yield return cs;
                prevEnd = cs.TextSpan.End;
            }
            if (prevEnd != text.Length)
            {
                yield return new ClassifiedSpan("", TextSpan.FromBounds(prevEnd, text.Length));
            }
        }

        private static readonly IImmutableSet<string> TextClassificationNames = new List<string>
        {
            ClassificationTypeNames.Text,
            ClassificationTypeNames.WhiteSpace,
            ClassificationTypeNames.PreprocessorText,
            ClassificationTypeNames.NumericLiteral,
            ClassificationTypeNames.StaticSymbol,
            ClassificationTypeNames.Punctuation,
        }.ToImmutableHashSet();

        private static readonly IImmutableDictionary<string, string> ClassificationReplacementMap = new Dictionary<string, string>
        {
            [ClassificationTypeNames.Comment] = CSharpClassifications.Comment,
            [ClassificationTypeNames.XmlDocCommentAttributeName] = CSharpClassifications.Comment,
            [ClassificationTypeNames.XmlDocCommentAttributeQuotes] = CSharpClassifications.Comment,
            [ClassificationTypeNames.XmlDocCommentAttributeValue] = CSharpClassifications.Comment,
            [ClassificationTypeNames.XmlDocCommentCDataSection] = CSharpClassifications.Comment,
            [ClassificationTypeNames.XmlDocCommentComment] = CSharpClassifications.Comment,
            [ClassificationTypeNames.XmlDocCommentDelimiter] = CSharpClassifications.Comment,
            [ClassificationTypeNames.XmlDocCommentEntityReference] = CSharpClassifications.Comment,
            [ClassificationTypeNames.XmlDocCommentName] = CSharpClassifications.Comment,
            [ClassificationTypeNames.XmlDocCommentProcessingInstruction] = CSharpClassifications.Comment,
            [ClassificationTypeNames.XmlDocCommentText] = CSharpClassifications.Comment,

            [ClassificationTypeNames.ExcludedCode] = CSharpClassifications.ExcludedCode,

            [ClassificationTypeNames.Identifier] = CSharpClassifications.Identifier,
            [ClassificationTypeNames.FieldName] = CSharpClassifications.Identifier,
            [ClassificationTypeNames.EnumMemberName] = CSharpClassifications.Identifier,
            [ClassificationTypeNames.ConstantName] = CSharpClassifications.Identifier,
            [ClassificationTypeNames.LocalName] = CSharpClassifications.Identifier,
            [ClassificationTypeNames.ParameterName] = CSharpClassifications.Identifier,
            [ClassificationTypeNames.MethodName] = CSharpClassifications.Identifier,
            [ClassificationTypeNames.ExtensionMethodName] = CSharpClassifications.Identifier,
            [ClassificationTypeNames.PropertyName] = CSharpClassifications.Identifier,
            [ClassificationTypeNames.EventName] = CSharpClassifications.Identifier,
            [ClassificationTypeNames.NamespaceName] = CSharpClassifications.Identifier,
            [ClassificationTypeNames.LabelName] = CSharpClassifications.Identifier,

            [ClassificationTypeNames.Operator] = CSharpClassifications.Operator,
            [ClassificationTypeNames.OperatorOverloaded] = CSharpClassifications.Operator,

            [ClassificationTypeNames.Keyword] = CSharpClassifications.Keyword,
            [ClassificationTypeNames.ControlKeyword] = CSharpClassifications.Keyword,
            [ClassificationTypeNames.PreprocessorKeyword] = CSharpClassifications.Keyword,

            [ClassificationTypeNames.StringLiteral] = CSharpClassifications.StringLiteral,
            [ClassificationTypeNames.VerbatimStringLiteral] = CSharpClassifications.StringLiteral,
            [ClassificationTypeNames.StringEscapeCharacter] = CSharpClassifications.StringLiteral,

            [ClassificationTypeNames.ClassName] = CSharpClassifications.TypeName,
            [ClassificationTypeNames.DelegateName] = CSharpClassifications.TypeName,
            [ClassificationTypeNames.EnumName] = CSharpClassifications.TypeName,
            [ClassificationTypeNames.InterfaceName] = CSharpClassifications.TypeName,
            [ClassificationTypeNames.ModuleName] = CSharpClassifications.TypeName,
            [ClassificationTypeNames.StructName] = CSharpClassifications.TypeName,
            [ClassificationTypeNames.TypeParameterName] = CSharpClassifications.TypeName,
        }.ToImmutableDictionary();

        internal static IEnumerable<ClassifiedSpan> MapClassificationTypes(IEnumerable<ClassifiedSpan> spans)
        {
            foreach (var span in spans)
            {
                if (ClassificationReplacementMap.TryGetValue(span.ClassificationType, out string newClassification))
                {
                    yield return new ClassifiedSpan(newClassification, span.TextSpan);
                }
                else if (TextClassificationNames.Contains(span.ClassificationType))
                {
                    yield return new ClassifiedSpan("", span.TextSpan);
                }
                else if (string.IsNullOrEmpty(span.ClassificationType))
                {
                    yield return span;
                }
                else
                {
                    throw new NotSupportedException($"Unknown classification: '{span.ClassificationType}'");
                }
            }
        }

        internal static IEnumerable<ClassifiedSpan> MergeMergeableSpans(IEnumerable<ClassifiedSpan> spans)
        {
            ClassifiedSpan? currentSpan = null;
            foreach (var span in spans)
            {
                if (span.ClassificationType != "")
                {
                    if (currentSpan != null)
                    {
                        yield return currentSpan.Value;
                        currentSpan = null;
                    }

                    yield return span;
                }
                else
                {
                    if (currentSpan == null)
                    {
                        currentSpan = span;
                    }
                    else
                    {
                        var start = currentSpan.Value.TextSpan.Start;
                        var end = span.TextSpan.End;
                        currentSpan = new ClassifiedSpan(currentSpan.Value.ClassificationType, TextSpan.FromBounds(start, end));
                    }
                }
            }

            if (currentSpan != null)
            {
                yield return currentSpan.Value;
            }
        }
    }
}
