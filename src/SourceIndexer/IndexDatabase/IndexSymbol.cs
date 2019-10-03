using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SourceIndexer.IndexDatabase
{
    public class IndexSymbol
    {
        public IndexSymbol(byte[] id, string name, string kind)
        {
            Id = id;
            Name = name;
            Kind = kind;
        }

        [Key]
        [Column(TypeName = "binary(20)")]
        public byte[] Id { get; set; }

        public string Name { get; set; }
        public string Kind { get; set; }

        public List<IndexDeclaration> Declarations { get; set; } = new List<IndexDeclaration>();

        public List<IndexReference> References { get; set; } = new List<IndexReference>();

        public List<IndexImplementation> Implementations { get; set; } = new List<IndexImplementation>();
    }

    public class IndexImplementation
    {
        public IndexImplementation(byte[] symbolId, int fileId, int fileImplementationId)
        {
            SymbolId = symbolId;
            FileId = fileId;
            FileImplementationId = fileImplementationId;
        }

        public int FileImplementationId { get; private set; }
        [Column(TypeName = "binary(20)")]
        public byte[] SymbolId { get; private set; }

        public IndexSymbol Symbol { get; private set; } = null!;
        public int FileId { get; private set; }
        public IndexFile File { get; private set; } = null!;
    }
}