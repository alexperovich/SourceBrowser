using System.Collections.Generic;
using System.Threading.Tasks;
using Mono.Options;

namespace SourceIndexer.Contracts
{
    public interface ILanguageIndexerPlugin
    {
        string Name { get; }
        OptionSet GetOptions();

        Task<(int pid, string settings)> LaunchIndexerProcessAsync(string serverUrl, string clientId, IEnumerable<string> args);
    }
}
