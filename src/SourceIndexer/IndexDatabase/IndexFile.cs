using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SourceIndexer.IndexDatabase
{
    public class IndexFile
    {
        public IndexFile(string name, int lineCount, int length)
        {
            Name = name;
            LineCount = lineCount;
            Length = length;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; private set; } = 0;

        public string Name { get; private set; }
        public int LineCount { get; private set; }
        public int Length { get; private set; }

        public string Content { get; set; } = null!;

        public int FolderId { get; private set; } = 0;
        public IndexFolder Folder { get; set; } = null!;

        public List<IndexDeclaration> Declarations { get; private set; } = new List<IndexDeclaration>();
        public List<IndexReference> References { get; private set; } = new List<IndexReference>();
    }
}