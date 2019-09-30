using System.ComponentModel.DataAnnotations.Schema;

namespace SourceIndexer.IndexDatabase
{
    public class IndexReference
    {
        public IndexReference(byte[] symbolId, int fileId, int fileReferenceId)
        {
            SymbolId = symbolId;
            FileId = fileId;
            FileReferenceId = fileReferenceId;
        }

        public int FileReferenceId { get; private set; }
        [Column(TypeName = "binary(20)")]
        public byte[] SymbolId { get; private set; }

        public IndexSymbol Symbol { get; private set; } = null!;
        public int FileId { get; private set; }
        public IndexFile File { get; private set; } = null!;
    }
}