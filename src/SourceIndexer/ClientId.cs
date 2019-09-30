using System;

namespace SourceIndexer
{
    public struct ClientId
    {
        public string LanguageName { get; }
        public string Id { get; }
        private ClientId(string languageName, string id)
        {
            LanguageName = languageName;
            Id = id;
        }

        public static ClientId Create(string languageName)
        {
            if (languageName == null) throw new ArgumentNullException(nameof(languageName));
            return new ClientId(languageName, Guid.NewGuid().ToString());
        }

        public static ClientId Parse(string id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            var colonIndex = id.IndexOf(':');
            if (colonIndex < 0) throw new FormatException("The string is not a valid ClientId string.");

            var languageName = id.Substring(0, colonIndex);
            var guid = id.Substring(colonIndex + 1);
            return new ClientId(languageName, guid);
        }

        public override string ToString()
        {
            return $"{LanguageName}:{Id}";
        }

        public bool Equals(ClientId other)
        {
            return string.Equals(LanguageName, other.LanguageName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj)
        {
            return obj is ClientId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(LanguageName, Id);
        }

        public static bool operator ==(ClientId left, ClientId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ClientId left, ClientId right)
        {
            return !left.Equals(right);
        }
    }
}