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

            // MySQL 은 시간대를 저장하지 않는다 — 운영 표의 시각은 늘 UTC 로 쓰고 읽어 JSON 에 Z 가 붙게 한다
            var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
                v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
            foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(Operator).Namespace)) {
                foreach (var p in entity.GetProperties().Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))) {
                    p.SetValueConverter(utc);
                }
            }
        }
    }
}
