using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SourceIndexer.IndexDatabase
{
    public class SourceIndexContextFactory : IDesignTimeDbContextFactory<SourceIndexContext>
    {
        public SourceIndexContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder()
                .UseSqlite("Data Source=index.db")
                .Options;
            return new SourceIndexContext(options);
        }
    }

    public class SourceIndexContext : DbContext
    {
        public SourceIndexContext(DbContextOptions options)
            :base(options)
        {
        }

        public ValueTask<IndexFolder> GetRootFolderAsync() => Folders.FindAsync(1);

        public DbSet<IndexFolder> Folders { get; set; } = null!;
        public DbSet<IndexFile> Files { get; set; } = null!;
        public DbSet<IndexDeclaration> Declarations { get; set; } = null!;
        public DbSet<IndexSymbol> Symbols { get; set; } = null!;
        public DbSet<IndexReference> References { get; set; } = null!;
        public DbSet<IndexStyle> Styles { get; set; } = null!;
        
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<IndexFolder>()
                .HasIndex(f => new
                {
                    f.ParentId,
                    f.Name,
                })
                .IsUnique();

            modelBuilder.Entity<IndexFolder>()
                .HasOne(f => f.Parent)
                .WithMany(f => f!.ChildFolders)
                .HasForeignKey(f => f.ParentId)
                .HasPrincipalKey(f => f!.Id);

            modelBuilder.Entity<IndexFolder>()
                .HasMany(f => f.Files)
                .WithOne(file => file.Folder)
                .HasForeignKey(file => file.FolderId)
                .HasPrincipalKey(f => f.Id);

            modelBuilder.Entity<IndexFolder>()
                .HasData(new IndexFolder("<root>")
                {
                    Id = 1,
                });

            modelBuilder.Entity<IndexFile>()
                .HasMany(file => file.Declarations)
                .WithOne(fs => fs.File)
                .HasForeignKey(fs => fs.FileId)
                .HasPrincipalKey(file => file.Id);

            modelBuilder.Entity<IndexFile>()
                .HasMany(file => file.References)
                .WithOne(r => r.File)
                .HasForeignKey(r => r.FileId)
                .HasPrincipalKey(file => file.Id);

            modelBuilder.Entity<IndexDeclaration>()
                .HasKey(ifs => new
                {
                    ifs.SymbolId,
                    ifs.FileId,
                });

            modelBuilder.Entity<IndexDeclaration>()
                .HasIndex(ifs => new
                {
                    ifs.FileId,
                    ifs.SymbolId,
                });

            modelBuilder.Entity<IndexDeclaration>()
                .HasOne(fs => fs.Symbol)
                .WithMany(sym => sym.Declarations)
                .HasForeignKey(fs => fs.SymbolId)
                .HasPrincipalKey(sym => sym.Id);

            modelBuilder.Entity<IndexSymbol>()
                .HasMany(sym => sym.References)
                .WithOne(r => r.Symbol)
                .HasForeignKey(r => r.SymbolId)
                .HasPrincipalKey(sym => sym.Id);

            modelBuilder.Entity<IndexReference>()
                .HasKey(r => new
                {
                    r.SymbolId,
                    r.FileId,
                });

            modelBuilder.Entity<IndexReference>()
                .HasIndex(r => new
                {
                    r.FileId,
                    r.SymbolId,
                });
        }

        public void RejectChanges()
        {
            foreach (var entry in ChangeTracker.Entries())
            {
                switch (entry.State)
                {
                    case EntityState.Modified:
                    case EntityState.Deleted:
                        entry.State = EntityState.Modified; //Revert changes made to deleted entity.
                        entry.State = EntityState.Unchanged;
                        break;
                    case EntityState.Added:
                        entry.State = EntityState.Detached;
                        break;
                }
            }
        }
    }
}