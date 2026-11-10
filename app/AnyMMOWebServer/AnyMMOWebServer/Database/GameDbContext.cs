using Microsoft.EntityFrameworkCore;
using AnyMMOWebServer.Models;
using AnyMMOWebServer.Ops;

namespace AnyMMOWebServer.Database
{
    public class GameDbContext : DbContext {
        public GameDbContext(DbContextOptions<GameDbContext> options) : base(options) {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<PlayerCharacter> PlayerCharacters { get; set; }
        public DbSet<Guild> Guilds { get; set; }
        public DbSet<ItemInstance> ItemInstances { get; set; }
        public DbSet<AuctionItem> AuctionItems { get; set; }
        public DbSet<MailMessage> MailMessages { get; set; }
        public DbSet<FriendList> FriendLists { get; set; }

        // ops
        public DbSet<Operator> Operators { get; set; }
        public DbSet<Sanction> Sanctions { get; set; }
        public DbSet<Grant> Grants { get; set; }
        public DbSet<GrantMail> GrantMails { get; set; }
        public DbSet<Notice> Notices { get; set; }
        public DbSet<Maintenance> Maintenances { get; set; }
        public DbSet<OperatorAuditLog> OperatorAuditLogs { get; set; }
        public DbSet<ItemFlowLog> ItemFlowLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder) {
            modelBuilder.Entity<Operator>().HasIndex(o => o.UserName).IsUnique();
            modelBuilder.Entity<Sanction>().HasIndex(s => s.AccountId);
            modelBuilder.Entity<GrantMail>().HasIndex(g => g.MailMessageId);
            modelBuilder.Entity<GrantMail>().HasIndex(g => g.GrantId);
            modelBuilder.Entity<OperatorAuditLog>().HasIndex(a => a.At);
            modelBuilder.Entity<ItemFlowLog>().HasIndex(f => f.At);
            modelBuilder.Entity<ItemFlowLog>().HasIndex(f => f.PlayerCharacterId);
            modelBuilder.Entity<Operator>().Property(o => o.UserName).HasMaxLength(64);
        }
    }
}
