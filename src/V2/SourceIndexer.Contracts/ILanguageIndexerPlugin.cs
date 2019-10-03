using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Mono.Options;

namespace SourceIndexer.Contracts
{
    public interface ILanguageIndexerPlugin
    {
        string Name { get; }
        OptionSet GetOptions();

        Task<int> LaunchIndexerProcessAsync(string serverUrl, string clientId, IEnumerable<string> args);
    }

    public static class LanguageIndexerPlugin
    {
        public static string SerializeSettings<T>(T settings)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
            return Convert.ToBase64String(bytes);
        }

        public static T DeserializeSettings<T>(string value)
        {
            var bytes = Convert.FromBase64String(value);
            return JsonSerializer.Deserialize<T>(bytes);
        }
    }
}
