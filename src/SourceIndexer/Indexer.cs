using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.CompilerServices;
using SourceIndexer.Contracts;
using SourceIndexer.IndexDatabase;

namespace SourceIndexer
{
    public class Indexer
    {
        private readonly ILanguageIndexerPlugin language;
        private readonly SourceIndexContext context;

        public string LanguageName => language.Name;

        private Indexer(SourceIndexContext context, ILanguageIndexerPlugin language)
        {
            this.language = language;
            this.context = context;
        }

        public static async Task<int> Run(IndexHostServer hostServer, DbContextOptions contextOptions, ILanguageIndexerPlugin language, ClientId clientId, IEnumerable<string> args)
        {
            await using var context = new SourceIndexContext(contextOptions);

            var indexPid = await language.LaunchIndexerProcessAsync($"http://localhost:{hostServer.Port}/", clientId.ToString(), args).ConfigureAwait(false);
            var indexer = new Indexer(context, language);
            hostServer.RegisterIndexer(indexer, clientId);

            var process = Process.GetProcessById(indexPid);
            var tcs = new TaskCompletionSource<int>();
            process.Exited += (s, e) => tcs.SetResult(process.ExitCode);
            return await tcs.Task.ConfigureAwait(false);
        }

        private static readonly Regex disalowedNameChars = new Regex("[^a-zA-Z_-]");
        private string NormalizeClassName(string name)
        {
            name = disalowedNameChars.Replace(name, "");
            return $"{LanguageName}-{name}".ToLowerInvariant();
        }

        private static readonly Regex normalizeSlashRegex = new Regex("[/\\\\]+");
        private string NormalizeSlashes(string path)
        {
            return normalizeSlashRegex.Replace(path, "/");
        }

        private bool initialized = false;

        private void EnsureInitialized()
        {
            if (!Volatile.Read(ref initialized))
            {
                throw new InvalidOperationException("Initialize must be called first.");
            }
        }

        private async Task ModifyDatabase(Func<Task> operation)
        {
            bool saved = false;
            while (!saved)
            {
                await operation().ConfigureAwait(false);

                try
                {
                    await context.SaveChangesAsync().ConfigureAwait(false);
                    saved = true;
                }
                catch (DbUpdateConcurrencyException)
                {
                    context.RejectChanges();
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        }

        public async Task<Empty> Initialize(InitializeData request)
        {
            await ModifyDatabase(async () =>
            {
                foreach (var (name, styles) in request.Styles)
                {
                    var normalizedName = NormalizeClassName(name);
                    var savedStyle = await context.Styles.FindAsync(normalizedName).ConfigureAwait(false);
                    if (savedStyle == null)
                    {
                        savedStyle = new IndexStyle(normalizedName);
                    }

                    savedStyle.Color = styles.Color;
                    savedStyle.BackgroundColor = styles.BackgroundColor;
                    savedStyle.FontWeight = styles.FontWeight;

                    context.Styles.Update(savedStyle);
                }
            }).ConfigureAwait(false);

            Volatile.Write(ref initialized, true);

            return null!;
        }

        private readonly SemaphoreSlim projectLock = new SemaphoreSlim(1, 1);
        private CurrentProject? currentProject;

        private class CurrentProject
        {
            public string ProjectPath { get; set; } = null!;
            public IndexFolder Folder { get; set; } = null!;
            public Dictionary<string, CurrentFile> Files { get; } = new Dictionary<string, CurrentFile>();
        }

        private class CurrentFile
        {
            public string Id { get; set; } = null!;
            public IndexFile File { get; set; } = null!;
            public StringBuilder Content { get; } = new StringBuilder();

            private int nextDeclarationId = 1;
            public int NextDeclarationId => nextDeclarationId++;
            private int nextReferenceId = 1;
            public int NextReferenceId => nextReferenceId++;
            private int nextImplementationId = 1;
            public int NextImplementationId => nextImplementationId++;
        }

        public async Task<ProjectId> BeginProject(Project request)
        {
            EnsureInitialized();
            var path = NormalizeSlashes(request.Path);
            var segments = path.Split('/');

            using (await SemaphoreLock.LockAsync(projectLock).ConfigureAwait(false))
            {
                if (currentProject != null)
                {
                    throw new InvalidOperationException("BeginProject cannot be called before ending the previous project.");
                }

                currentProject = new CurrentProject
                {
                    ProjectPath = string.Join("/", segments),
                };
            }

            string? projectId = null;

            await ModifyDatabase(async () =>
            {
                var folder = await context.GetRootFolderAsync().ConfigureAwait(false);
                foreach (var segment in segments)
                {
                    var newFolder = await this.context.Folders
                        .FirstOrDefaultAsync(f => f.Parent == folder && f.Name == segment).ConfigureAwait(false);
                    if (newFolder == null)
                    {
                        newFolder = new IndexFolder(segment)
                        {
                            Parent = folder,
                        };
                    }

                    context.Folders.Update(newFolder);
                    folder = newFolder;
                }

                folder.IsProject = true;
                context.Files.RemoveRange(context.Files.Where(f => f.FolderId == folder.Id));
                context.Folders.RemoveRange(context.Folders.Where(f => f.ParentId == folder.Id));

                projectId = folder.Id.ToString();
                currentProject.Folder = folder;
            }).ConfigureAwait(false);

            return new ProjectId
            {
                Id = projectId,
            };
        }

        public async Task<ProjectFileId> BeginFile(ProjectFile request)
        {
            EnsureInitialized();
            var path = NormalizeSlashes(request.Path);

            CurrentFile currentFile = new CurrentFile
            {
                Id = path,
            };
            IndexFolder? currentFolder;
            using (await SemaphoreLock.LockAsync(projectLock).ConfigureAwait(false))
            {
                if (currentProject == null)
                {
                    throw new InvalidOperationException("BeginProject must be called before BeginFile.");
                }

                currentFolder = currentProject.Folder;
                if (currentFolder == null)
                {
                    throw new InvalidOperationException("Project has no folder.");
                }

                if (request.ProjectId != currentFolder.Id.ToString())
                {
                    throw new ArgumentException("Invalid ProjectId", $"{nameof(request)}.{nameof(request.ProjectId)}");
                }

                if (!currentProject.Files.TryAdd(currentFile.Id, currentFile))
                {
                    throw new InvalidOperationException($"File '{currentFile.Id}' already started.");
                }
            }

            var segments = path.Split('/');
            var pathSegments = segments[..^1];
            var name = segments[^0];

            await ModifyDatabase(async () =>
            {
                var folder = currentFolder;
                foreach (var segment in pathSegments)
                {
                    var newFolder = await context.Folders
                        .FirstOrDefaultAsync(f => f.Parent == folder && f.Name == segment).ConfigureAwait(false);
                    if (newFolder == null)
                    {
                        newFolder = new IndexFolder(segment)
                        {
                            Parent = folder,
                        };
                    }

                    context.Folders.Update(newFolder);
                    folder = newFolder;
                }

                var file = await context.Files.FirstOrDefaultAsync(f => f.Folder == folder && f.Name == name).ConfigureAwait(false);
                if (file != null)
                {
                    context.Files.Remove(file);
                }

                file = new IndexFile(name, request.LineCount, request.Length)
                {
                    Folder = folder,
                };
                context.Files.Add(file);

                currentFile.File = file;
            }).ConfigureAwait(false);

            var lineNumbers = string.Concat(Enumerable.Range(1, request.LineCount).Select(l => $"<a id='line-{l}' href='#link-{l}'>{l}</a><br/>"));

            currentFile.Content.AppendLine(
                $@"<div class='cz'><table class='tb' cellpadding='0' cellspacing='0'><tr><td valign='top' align='right'><pre id='ln'>{lineNumbers}</pre></td><td valign='top' align='left'><pre id='code'>");
            return new ProjectFileId
            {
                Id = currentFile.Id,
            };
        }

        public byte[] GetSymbolKey(string name)
        {
            // SHA1 is used here because it is 12 bytes shorter than SHA256 and that is a >30% space savings on the final size of the symbol table.
            // This value is just used as an identifier and collisions don't cause a problem.
            using var sha = SHA1.Create();
            var bytes = Encoding.UTF8.GetBytes(name);
            return sha.ComputeHash(bytes);
        }

        private async ValueTask<byte[]> IndexSymbol(Symbol value)
        {
            var symbolName = NormalizeSymbolName(value.Name);
            var symbolKind = NormalizeSymbolKind(value.Kind);
            var symbolKey = GetSymbolKey(symbolName);
            var symbol = await context.Symbols.FindAsync(symbolKey).ConfigureAwait(false);
            if (symbol == null)
            {
                symbol = new IndexSymbol(symbolKey, symbolName, symbolKind);
                context.Symbols.Add(symbol);
            }
            else
            {
                if (symbol.Name != symbolName)
                {
                    throw new InvalidOperationException("Different symbols same id");
                }
                if (symbol.Kind != symbolKind)
                {
                    symbol.Kind = symbolKind;
                    context.Symbols.Update(symbol);
                }
            }

            return symbolKey;
        }

        private string NormalizeSymbolName(string name)
        {
            return LanguageName + ":" + name;
        }

        private string NormalizeSymbolKind(string kind)
        {
            return LanguageName + ":" + kind;
        }

        public async Task IndexToken(Token request)
        {
            CurrentFile? currentFile;
            using (await SemaphoreLock.LockAsync(projectLock).ConfigureAwait(false))
            {
                if (currentProject == null)
                {
                    throw new InvalidOperationException("BeginProject must be called before BeginFile.");
                }

                if (!currentProject.Files.TryGetValue(request.ProjectFileId, out currentFile))
                {
                    throw new InvalidOperationException($"File '{request.ProjectFileId}' doesn't exist.");
                }
            }

            var content = currentFile.Content;
            var text = request.Text;
            var classification = request.Classification.ToLowerInvariant();

            if (classification == "text")
            {
                content.Append(request.Text);
                return;
            }

            classification = NormalizeClassName(classification);

            content.Append($"<span class='{classification}'");
            if (request.DeclaredSymbol != null)
            {
                var declarationId = currentFile.NextDeclarationId;
                var symbol = request.DeclaredSymbol;
                content.Append($" id='d-{declarationId}'");
                await ModifyDatabase(async () =>
                {
                    var symbolKey = await IndexSymbol(symbol).ConfigureAwait(false);
                    context.Declarations.Add(new IndexDeclaration(symbolKey, currentFile.File.Id, declarationId));
                }).ConfigureAwait(false);
            }
            if (request.ReferencedSymbol != null)
            {
                var referenceId = currentFile.NextReferenceId;
                var symbol = request.ReferencedSymbol;
                content.Append($" id='r-{referenceId}'");
                await ModifyDatabase(async () =>
                {
                    var symbolKey = await IndexSymbol(symbol).ConfigureAwait(false);
                    context.References.Add(new IndexReference(symbolKey, currentFile.File.Id, referenceId));
                }).ConfigureAwait(false);
            }
            if (request.ImplementedSymbols != null && request.ImplementedSymbols.Any())
            {
                foreach (var symbol in request.ImplementedSymbols)
                {
                    var implementationId = currentFile.NextImplementationId;
                    content.Append($" id='i-{implementationId}'");
                    await ModifyDatabase(async () =>
                    {
                        var symbolKey = await IndexSymbol(symbol).ConfigureAwait(false);
                        context.Implementations.Add(new IndexImplementation(symbolKey, currentFile.File.Id,
                            implementationId));
                    }).ConfigureAwait(false);
                }
            }

            content.Append($">{text}</span>");
        }

        public async Task EndFile(ProjectFileId request)
        {
            EnsureInitialized();

            CurrentFile? currentFile;
            using (await SemaphoreLock.LockAsync(projectLock).ConfigureAwait(false))
            {
                if (currentProject == null)
                {
                    throw new InvalidOperationException("BeginProject must be called before BeginFile.");
                }

                if (!currentProject.Files.TryGetValue(request.Id, out currentFile))
                {
                    throw new InvalidOperationException($"File '{request.Id}' doesn't exist.");
                }
            }

            currentFile.Content.Append("</pre></td></tr></table></div>");

            await ModifyDatabase(() =>
            {
                currentFile.File.Content = currentFile.Content.ToString();
                context.Update(currentFile.File);

                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        public async Task EndProject(ProjectId request)
        {
            EnsureInitialized();
            using (await SemaphoreLock.LockAsync(projectLock).ConfigureAwait(false))
            {
                if (currentProject == null)
                {
                    throw new InvalidOperationException("BeginProject must be called before EndProject.");
                }

                if (currentProject.Folder == null)
                {
                    throw new InvalidOperationException("currentProject.Folder was null");
                }

                if (request.Id != currentProject.Folder.Id.ToString())
                {
                    throw new ArgumentException("Invalid ProjectId", $"{nameof(request)}.{nameof(request.Id)}");
                }

                if (currentProject.Files.Any())
                {
                    throw new InvalidOperationException("EndProject cannot be called when Files are still pending.");
                }

                currentProject = null;
            }
        }
    }
}