using System.ComponentModel.DataAnnotations.Schema;

namespace SourceIndexer.IndexDatabase
{
    public class IndexDeclaration
    {
        public IndexDeclaration(byte[] symbolId, int fileId, int fileDeclarationId)
        {
            SymbolId = symbolId;
            FileId = fileId;
            FileDeclarationId = fileDeclarationId;
        }

        public int FileDeclarationId { get; private set; }
        public int FileId { get; private set; }
        public IndexFile File { get; private set; } = null!;

        [Column(TypeName = "binary(20)")]
        public byte[] SymbolId { get; private set; }

        public IndexSymbol Symbol { get; private set; } = null!;
    }
}