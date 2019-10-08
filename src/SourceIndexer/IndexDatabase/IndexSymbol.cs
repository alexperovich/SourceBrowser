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
    }
}