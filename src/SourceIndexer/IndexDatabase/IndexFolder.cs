using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SourceIndexer.IndexDatabase
{
    public class IndexFolder
    {
        public IndexFolder(string name)
        {
            Name = name;
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; } = 0;

        public bool IsProject { get; set; }

        public string Name { get; private set; }

        public int? ParentId { get; private set; } = null;
        public IndexFolder? Parent { get; set; } = null;

        public List<IndexFolder> ChildFolders { get; private set; } = new List<IndexFolder>();

        public List<IndexFile> Files { get; private set; } = new List<IndexFile>();
    }
}