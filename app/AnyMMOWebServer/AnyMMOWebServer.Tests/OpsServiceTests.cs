// 운영 서비스(제재 · 우편 지급/회수 · 점검 · 번호 발급)를 메모리 DB 로 검사한다
using AnyMMOWebServer.Database;
using AnyMMOWebServer.Models;
using AnyMMOWebServer.Ops;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using Xunit;

namespace AnyMMOWebServer.Tests
{
    public class OpsServiceTests
    {
        private readonly GameDbContext db;
        private readonly OpsSettings settings = new() { BaseCurrencyName = "Gold", CurrencyItemName = "System Currency Loot Item" };
        private readonly AuditService audit;

        public OpsServiceTests()
        {
            db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            audit = new AuditService(db);
            db.Users.Add(new User { Id = 1, UserName = "alice", Email = "a@x", PasswordHash = "h", Salt = "s" });
            db.PlayerCharacters.Add(new PlayerCharacter { Id = 10, AccountId = 1, Name = "Hero", SaveData = "{\"CharacterSaveData\":{\"CharacterLevel\":7,\"CharacterClass\":\"Warrior\"},\"ItemInstanceListSaveData\":{\"ItemInstances\":[{\"ItemName\":\"Potion\"},{\"ItemName\":\"Potion\"}]}}" });
            db.PlayerCharacters.Add(new PlayerCharacter { Id = 11, AccountId = 1, Name = "Alt", SaveData = "not json" });
            db.SaveChanges();
        }

        private GrantService Grants() => new(db, settings, audit);

        [Fact]
        public void Sanction_blocks_duplicates_and_release_clears_it()
        {
            var users = new OpsUserService(db, audit);
            var s = users.AddSanction("op", 1, SanctionTypes.Suspend, "욕설", null);
            Assert.NotNull(users.RestrictionsOf(1).Suspend);
            var dup = Assert.Throws<OpsError>(() => users.AddSanction("op", 1, SanctionTypes.Suspend, "또", null));
            Assert.Equal(409, dup.Status);

            users.Release("op", s.Id, "오인");
            Assert.Null(users.RestrictionsOf(1).Suspend);
            Assert.Equal(409, Assert.Throws<OpsError>(() => users.Release("op", s.Id, "다시")).Status);
            Assert.Equal(2, db.OperatorAuditLogs.Count());
        }

        [Fact]
        public void Expired_sanction_is_not_active_and_bad_input_is_rejected()
        {
            db.Sanctions.Add(new Sanction { AccountId = 1, Type = SanctionTypes.ChatMute, Reason = "r", StartsAt = DateTime.UtcNow.AddHours(-2), EndsAt = DateTime.UtcNow.AddHours(-1) });
            db.SaveChanges();
            var users = new OpsUserService(db, audit);
            Assert.Null(users.RestrictionsOf(1).ChatMute);
            Assert.Equal(400, Assert.Throws<OpsError>(() => users.AddSanction("op", 1, "BAN", "r", null)).Status);
            Assert.Equal(400, Assert.Throws<OpsError>(() => users.AddSanction("op", 1, SanctionTypes.ChatMute, " ", null)).Status);
            Assert.Equal(400, Assert.Throws<OpsError>(() => users.AddSanction("op", 1, SanctionTypes.ChatMute, "r", DateTime.UtcNow.AddMinutes(-1))).Status);
            Assert.Equal(404, Assert.Throws<OpsError>(() => users.AddSanction("op", 99, SanctionTypes.ChatMute, "r", null)).Status);
        }

        [Fact]
        public void User_search_finds_by_character_name_and_summarizes_save_data()
        {
            var users = new OpsUserService(db, audit);
            var page = users.Users("Hero", 1, 20);
            Assert.Equal(1, page.Total);
            Assert.Equal(2, page.Items[0].CharacterCount);
            var hero = users.Characters("Hero").Single();
            Assert.Equal(7, hero.Level);
            Assert.Equal("Warrior", hero.CharacterClass);
            Assert.Equal(2, hero.Items["Potion"]);
            Assert.Null(users.Characters("Alt").Single().Level);
        }

        [Fact]
        public void Send_creates_game_format_mail_per_character()
        {
            var g = Grants().Send("op", new GrantRequest(new() { 10, 11 }, "보상", "점검 보상", new() { new GrantItem("Potion", 3) }, 500, "점검 보상"));
            var mails = db.GrantMails.Where(m => m.GrantId == g.Id).ToList();
            Assert.Equal(2, mails.Count);
            var mail = db.MailMessages.Find(mails[0].MailMessageId)!;
            var node = JsonNode.Parse(mail.SaveData)!;
            Assert.Equal(mail.Id, node["MessageId"]!.GetValue<int>());
            Assert.Equal("운영팀", node["Sender"]!.GetValue<string>());
            var slots = node["AttachmentSlots"]!.AsArray();
            Assert.Equal(2, slots.Count);
            Assert.Equal(3, slots[0]!["ItemInstanceIds"]!.AsArray().Count);
            var currencyId = slots[1]!["ItemInstanceIds"]![0]!.GetValue<long>();
            var currency = JsonNode.Parse(db.ItemInstances.Single(i => i.ItemInstanceId == currencyId).SaveData)!;
            Assert.Equal("System Currency Loot Item", currency["ItemName"]!.GetValue<string>());
            Assert.Equal("Gold", currency["GainCurrencyName"]!.GetValue<string>());
            Assert.Equal(500, currency["GainCurrencyAmount"]!.GetValue<int>());
            Assert.Equal(8, db.ItemInstances.Count());
            Assert.Equal(4, db.ItemFlowLogs.Count(f => f.Kind == ItemFlowKinds.Granted));
        }

        [Fact]
        public void Send_rejects_empty_or_oversized_requests()
        {
            var s = Grants();
            Assert.Equal(400, Assert.Throws<OpsError>(() => s.Send("op", new GrantRequest(new() { 10 }, "t", "", new(), 0, "r"))).Status);
            Assert.Equal(400, Assert.Throws<OpsError>(() => s.Send("op", new GrantRequest(new() { 10 }, "t", "", new() { new GrantItem("A", 100) }, 0, "r"))).Status);
            Assert.Equal(400, Assert.Throws<OpsError>(() => s.Send("op", new GrantRequest(new(), "t", "", new() { new GrantItem("A", 1) }, 0, "r"))).Status);
            Assert.Equal(404, Assert.Throws<OpsError>(() => s.Send("op", new GrantRequest(new() { 999 }, "t", "", new() { new GrantItem("A", 1) }, 0, "r"))).Status);
            Assert.Equal(0, db.MailMessages.Count());
        }

        [Fact]
        public void Taking_attachments_is_logged_and_recall_handles_full_partial_and_taken()
        {
            var s = Grants();
            var g = s.Send("op", new GrantRequest(new() { 10, 11 }, "보상", "", new() { new GrantItem("Potion", 1), new GrantItem("Sword", 1) }, 0, "r"));
            var heroMail = db.GrantMails.Single(m => m.PlayerCharacterId == 10);
            var altMail = db.GrantMails.Single(m => m.PlayerCharacterId == 11);

            // Hero 가 첫 칸(Potion)만 받는다 — 게임 서버가 그 번호를 뺀 우편을 저장한다
            var mail = db.MailMessages.Find(heroMail.MailMessageId)!;
            var node = JsonNode.Parse(mail.SaveData)!;
            node["AttachmentSlots"]![0]!["ItemInstanceIds"] = new JsonArray();
            s.OnMailSaving(mail.Id, node.ToJsonString());
            mail.SaveData = node.ToJsonString();
            db.SaveChanges();
            Assert.Equal(1, db.ItemFlowLogs.Count(f => f.Kind == ItemFlowKinds.Taken && f.ItemName == "Potion"));
            Assert.Equal(GrantStatus.Sent, heroMail.Status);

            var r = s.Recall("op", g.Id, "잘못 지급");
            Assert.Equal(new RecallResult(1, 1, 0), r);
            Assert.Equal(GrantStatus.PartlyRecalled, heroMail.Status);
            Assert.Equal(GrantStatus.Recalled, altMail.Status);
            Assert.Null(db.MailMessages.Find(altMail.MailMessageId));
            Assert.Empty(GrantService.AttachedIds(db.MailMessages.Find(heroMail.MailMessageId)!.SaveData));
            Assert.Equal(1, db.ItemInstances.Count());
            Assert.Equal(2 + 1, db.ItemFlowLogs.Count(f => f.Kind == ItemFlowKinds.Recalled));
            Assert.Equal(409, Assert.Throws<OpsError>(() => s.Recall("op", g.Id, "또")).Status);
        }

        [Fact]
        public void Recall_counts_fully_taken_mail()
        {
            var s = Grants();
            var g = s.Send("op", new GrantRequest(new() { 10 }, "보상", "", new() { new GrantItem("Potion", 2) }, 0, "r"));
            var gm = db.GrantMails.Single();
            var mail = db.MailMessages.Find(gm.MailMessageId)!;
            var emptied = JsonNode.Parse(mail.SaveData)!;
            emptied["AttachmentSlots"] = new JsonArray();
            s.OnMailSaving(mail.Id, emptied.ToJsonString());
            Assert.Equal(GrantStatus.Taken, gm.Status);
            mail.SaveData = emptied.ToJsonString();
            db.SaveChanges();
            Assert.Equal(new RecallResult(0, 0, 1), s.Recall("op", g.Id, "r"));
        }

        [Fact]
        public void Maintenance_blocks_everyone_except_allowed_accounts()
        {
            var n = new NoticeService(db, audit);
            Assert.False(n.BlockedByMaintenance(1));
            n.SetMaintenance("op", new MaintenanceInput(true, "정기 점검", null, DateTime.UtcNow.AddHours(1), new() { 2 }));
            Assert.True(n.BlockedByMaintenance(1));
            Assert.False(n.BlockedByMaintenance(2));
            Assert.Equal(400, Assert.Throws<OpsError>(() => n.SetMaintenance("op", new MaintenanceInput(true, "", null, null, null))).Status);
            n.SetMaintenance("op", new MaintenanceInput(false, "", null, null, null));
            Assert.False(n.BlockedByMaintenance(1));
        }

        [Fact]
        public void Server_status_shows_only_current_enabled_notices()
        {
            var n = new NoticeService(db, audit);
            n.Save("op", null, new NoticeInput("지금", "본문", null, null, true));
            n.Save("op", null, new NoticeInput("꺼짐", "본문", null, null, false));
            n.Save("op", null, new NoticeInput("나중", "본문", DateTime.UtcNow.AddDays(1), null, true));
            var json = System.Text.Json.JsonSerializer.Serialize(n.ServerStatus(), new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            Assert.Contains("지금", json);
            Assert.DoesNotContain("꺼짐", json);
            Assert.DoesNotContain("나중", json);
        }

        [Fact]
        public void Item_ids_are_unique_and_use_ops_generator()
        {
            var ids = Enumerable.Range(0, 10000).Select(_ => OpsItemIds.Next()).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.All(ids, id => Assert.Equal(1023, (id >> 12) & 0x3FF));
        }
    }
}
