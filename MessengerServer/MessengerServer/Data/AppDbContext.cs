using Microsoft.EntityFrameworkCore;

namespace MessengerServer.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
        public DbSet<User> Users { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("users");

                entity.HasKey(e => e.UserId);
                entity.Property(e => e.UserId)
                      .HasColumnName("user_id");

                entity.Property(e => e.Username)
                      .IsRequired()
                      .HasMaxLength(50)
                      .HasColumnName("username");
                entity.HasIndex(e => e.Username).IsUnique();

                entity.Property(e => e.PasswordHash)
                      .IsRequired()
                      .HasColumnName("password_hash");

                entity.Property(e => e.DisplayName)
                      .HasColumnName("display_name");

                entity.Property(e => e.EccPublicKey)
                      .IsRequired()
                      .HasColumnName("ecc_public_key");

                entity.Property(e => e.CreatedAt)
                      .HasColumnName("created_at");
            });
        }
    }
}