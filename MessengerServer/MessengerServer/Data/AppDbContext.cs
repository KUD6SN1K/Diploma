using Microsoft.EntityFrameworkCore;
using MessengerServer.Models;

namespace MessengerServer.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users => Set<User>();
        public DbSet<Contact> Contacts => Set<Contact>();
        public DbSet<Conversation> Conversations => Set<Conversation>();
        public DbSet<Message> Messages => Set<Message>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // User (already there, but we'll re-add for completeness)
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("users");
                entity.HasKey(e => e.UserId);
                entity.Property(e => e.UserId).HasColumnName("user_id");
                entity.Property(e => e.Username).IsRequired().HasMaxLength(50).HasColumnName("username");
                entity.HasIndex(e => e.Username).IsUnique();
                entity.Property(e => e.PasswordHash).IsRequired().HasColumnName("password_hash");
                entity.Property(e => e.DisplayName).HasColumnName("display_name");
                entity.Property(e => e.EccPublicKey).IsRequired().HasColumnName("ecc_public_key");
                entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            });

            // Contact
            modelBuilder.Entity<Contact>(entity =>
            {
                entity.ToTable("contacts");
                entity.HasKey(e => e.ContactId);
                entity.Property(e => e.ContactId).HasColumnName("contact_id");
                entity.Property(e => e.UserId).IsRequired().HasColumnName("user_id");
                entity.Property(e => e.ContactUserId).IsRequired().HasColumnName("contact_user_id");
                entity.Property(e => e.IsConfirmed).HasColumnName("is_confirmed");
                entity.Property(e => e.CreatedAt).HasColumnName("created_at");

                entity.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId);
                entity.HasOne(c => c.ContactUser).WithMany().HasForeignKey(c => c.ContactUserId);
                entity.HasIndex(c => new { c.UserId, c.ContactUserId }).IsUnique();
            });

            // Conversation
            modelBuilder.Entity<Conversation>(entity =>
            {
                entity.ToTable("conversations");
                entity.HasKey(e => e.ConversationId);
                entity.Property(e => e.ConversationId).HasColumnName("conversation_id");
                entity.Property(e => e.User1Id).IsRequired().HasColumnName("user1_id");
                entity.Property(e => e.User2Id).IsRequired().HasColumnName("user2_id");
                entity.Property(e => e.CreatedAt).HasColumnName("created_at");

                entity.HasOne(c => c.User1).WithMany().HasForeignKey(c => c.User1Id);
                entity.HasOne(c => c.User2).WithMany().HasForeignKey(c => c.User2Id);
                // Ensure only one conversation per pair (regardless of order)
                // This can't be done with a simple unique index on (user1_id, user2_id) because order matters.
                // We'll enforce via a unique index on (LEAST(user1_id, user2_id), GREATEST(user1_id, user2_id))
                // EF Core doesn't support that directly; we'll handle it with raw SQL in the migration or service logic.
                entity.HasIndex(c => new { c.User1Id, c.User2Id }).IsUnique();
            });

            // Message
            modelBuilder.Entity<Message>(entity =>
            {
                entity.ToTable("messages");
                entity.HasKey(e => e.MessageId);
                entity.Property(e => e.MessageId).HasColumnName("message_id");
                entity.Property(e => e.ConversationId).IsRequired().HasColumnName("conversation_id");
                entity.Property(e => e.SenderId).IsRequired().HasColumnName("sender_id");
                entity.Property(e => e.EncryptedContent).IsRequired().HasColumnName("encrypted_content");
                entity.Property(e => e.Timestamp).HasColumnName("timestamp");
                entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);

                entity.HasOne(m => m.Conversation).WithMany(c => c.Messages).HasForeignKey(m => m.ConversationId);
                entity.HasOne(m => m.Sender).WithMany().HasForeignKey(m => m.SenderId);
            });
        }
    }
}