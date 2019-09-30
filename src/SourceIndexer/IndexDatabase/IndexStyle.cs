using System.ComponentModel.DataAnnotations;

namespace SourceIndexer.IndexDatabase
{
    public class IndexStyle
    {
        public IndexStyle(string name)
        {
            Name = name;
        }

        [Key]
        public string Name { get; private set; }
        public string? Color { get; set; }
        public string? BackgroundColor { get; set; }
        public string? FontWeight { get; set; }
    }
}