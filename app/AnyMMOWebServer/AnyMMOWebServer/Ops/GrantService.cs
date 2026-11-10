// 운영 우편 지급 · 회수 — 아이템 인스턴스와 우편을 게임과 같은 저장 형식으로 만들고 수령 · 회수를 아이템 흐름에 남긴다
using AnyMMOWebServer.Database;
using AnyMMOWebServer.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnyMMOWebServer.Ops
{
    public record GrantItem(string ItemName, int Quantity);
    public record GrantRequest(List<int> CharacterIds, string Subject, string Body, List<GrantItem> Items, int CurrencyAmount, string Reason);
    public record RecallResult(int Recalled, int PartlyRecalled, int AlreadyTaken);

    // 게임 IdGen 과 같은 꼴(시각 41 · 발급기 10 · 순번 12 비트, 2026-01-01 기준). 게임은 발급기 0 · 1, 운영툴은 1023 이라 번호가 겹치지 않는다
    public static class OpsItemIds
    {
        private static readonly DateTime Epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const long Generator = 1023;
        private static readonly object Gate = new();
        private static long lastMs = -1;
        private static long seq;

        public static long Next()
        {
            lock (Gate) {
                long ms = (long)(DateTime.UtcNow - Epoch).TotalMilliseconds;
                if (ms < lastMs) ms = lastMs;
                if (ms == lastMs) {
                    seq = (seq + 1) & 0xFFF;
                    if (seq == 0) ms = ++lastMs;
                } else {
                    seq = 0;
                }
                lastMs = ms;
                return (ms << 22) | (Generator << 12) | seq;
            }
        }
    }

    public class GrantService
    {
        public const string DeletedByPlayer = "DELETED_BY_PLAYER";
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        private readonly GameDbContext db;
        private readonly OpsSettings settings;
        private readonly AuditService audit;

        public GrantService(GameDbContext db, OpsSettings settings, AuditService audit)
        {
            this.db = db;
            this.settings = settings;
            this.audit = audit;
        }

        private static void Check(bool ok, string message)
        {
            if (!ok) throw new OpsError(400, "INVALID", message);
        }

        public Grant Send(string op, GrantRequest req)
        {
            var ids = (req.CharacterIds ?? new()).Distinct().ToList();
            var items = req.Items ?? new();
            Check(ids.Count is >= 1 and <= 50, "받는 캐릭터를 1~50명 고르세요");
            Check(items.Count <= 5, "아이템은 한 번에 5종까지 보낼 수 있습니다");
            Check(items.All(i => !string.IsNullOrWhiteSpace(i.ItemName) && i.ItemName.Length <= 100), "아이템 이름을 입력하세요");
            Check(items.All(i => i.Quantity is >= 1 and <= 99), "아이템 수량은 1~99 입니다");
            Check(req.CurrencyAmount is >= 0 and <= 1_000_000, "재화는 0~1,000,000 입니다");
            Check(items.Count > 0 || req.CurrencyAmount > 0, "아이템이나 재화를 하나 이상 넣으세요");
            var subject = (req.Subject ?? "").Trim();
            var body = (req.Body ?? "").Trim();
            var reason = (req.Reason ?? "").Trim();
            Check(subject.Length is >= 1 and <= 50, "우편 제목을 1~50자로 입력하세요");
            Check(body.Length <= 500, "우편 본문은 500자까지입니다");
            Check(reason.Length is >= 1 and <= 200, "지급 사유를 1~200자로 입력하세요");
            var characters = db.PlayerCharacters.Where(c => ids.Contains(c.Id)).ToList();
            if (characters.Count != ids.Count) throw new OpsError(404, "NOT_FOUND", "없는 캐릭터가 섞여 있습니다");

            var now = DateTime.UtcNow;
            var grant = new Grant {
                Subject = subject, Body = body, ItemsJson = JsonSerializer.Serialize(items, Web), CurrencyAmount = req.CurrencyAmount,
                Reason = reason, CreatedBy = op, CreatedAt = now,
            };
            db.Grants.Add(grant);
            var pending = new List<(PlayerCharacter c, MailMessage mail, List<long> ids)>();
            foreach (var c in characters) {
                var slots = new JsonArray();
                var all = new List<long>();
                foreach (var it in items) {
                    var slot = new JsonArray();
                    for (int n = 0; n < it.Quantity; n++) {
                        long id = OpsItemIds.Next();
                        db.ItemInstances.Add(new ItemInstance { ItemInstanceId = id, SaveData = ItemSave(id, it.ItemName.Trim(), null, 0) });
                        slot.Add(id);
                        all.Add(id);
                    }
                    slots.Add(new JsonObject { ["ItemInstanceIds"] = slot });
                }
                if (req.CurrencyAmount > 0) {
                    long id = OpsItemIds.Next();
                    db.ItemInstances.Add(new ItemInstance {
                        ItemInstanceId = id, SaveData = ItemSave(id, settings.CurrencyItemName, settings.BaseCurrencyName, req.CurrencyAmount),
                    });
                    slots.Add(new JsonObject { ["ItemInstanceIds"] = new JsonArray(id) });
                    all.Add(id);
                }
                var mail = new MailMessage { PlayerCharacterId = c.Id, SaveData = MailSave(0, c.Name, subject, body, slots) };
                db.MailMessages.Add(mail);
                pending.Add((c, mail, all));
            }
            db.SaveChanges();

            foreach (var (c, mail, all) in pending) {
                // 게임은 우편 번호를 저장 데이터의 MessageId 로도 읽는다
                var node = JsonNode.Parse(mail.SaveData)!;
                node["MessageId"] = mail.Id;
                mail.SaveData = node.ToJsonString();
                db.GrantMails.Add(new GrantMail {
                    GrantId = grant.Id, PlayerCharacterId = c.Id, CharacterName = c.Name, MailMessageId = mail.Id,
                    ItemInstanceIds = string.Join(",", all),
                });
                foreach (var it in items) Flow(c, ItemFlowKinds.Granted, it.ItemName.Trim(), it.Quantity, 0, grant.Id, mail.Id, op);
                if (req.CurrencyAmount > 0) Flow(c, ItemFlowKinds.Granted, settings.BaseCurrencyName, 0, req.CurrencyAmount, grant.Id, mail.Id, op);
            }
            db.SaveChanges();
            audit.Write(op, "GRANT", "SEND", $"지급 #{grant.Id} · 캐릭터 {characters.Count}명",
                $"{subject} · {string.Join(", ", items.Select(i => $"{i.ItemName} x{i.Quantity}"))}{(req.CurrencyAmount > 0 ? $" · 재화 {req.CurrencyAmount}" : "")} · {reason}");
            return grant;
        }

        public RecallResult Recall(string op, int grantId, string reason)
        {
            var grant = db.Grants.Find(grantId) ?? throw new OpsError(404, "NOT_FOUND", "지급 건을 찾을 수 없습니다");
            reason = (reason ?? "").Trim();
            Check(reason.Length is >= 1 and <= 200, "회수 사유를 1~200자로 입력하세요");
            if (grant.RecalledAt != null) throw new OpsError(409, "ALREADY_RECALLED", "이미 회수한 지급 건입니다");
            int recalled = 0, partly = 0, taken = 0;
            foreach (var gm in db.GrantMails.Where(g => g.GrantId == grantId).ToList()) {
                var granted = Ids(gm.ItemInstanceIds);
                var mail = db.MailMessages.Find(gm.MailMessageId);
                var current = mail == null ? new HashSet<long>() : AttachedIds(mail.SaveData);
                var remaining = granted.Where(current.Contains).ToList();
                if (remaining.Count == 0) {
                    taken++;
                    continue;
                }
                var instances = db.ItemInstances.Where(i => remaining.Contains(i.ItemInstanceId)).ToList();
                LogFlows(gm, instances, ItemFlowKinds.Recalled, grantId, op);
                db.ItemInstances.RemoveRange(instances);
                var (left, empty) = RemoveIds(mail!.SaveData, remaining.ToHashSet());
                // 하나도 안 받은 우편은 통째로 지우고, 일부 받은 우편은 받은 기록이 남게 둔다
                if (empty && remaining.Count == granted.Count) db.MailMessages.Remove(mail); else mail.SaveData = left;
                if (remaining.Count == granted.Count) { gm.Status = GrantStatus.Recalled; recalled++; } else { gm.Status = GrantStatus.PartlyRecalled; partly++; }
            }
            grant.RecalledAt = DateTime.UtcNow;
            grant.RecalledBy = op;
            db.SaveChanges();
            audit.Write(op, "GRANT", "RECALL", $"지급 #{grantId}", $"회수 {recalled} · 일부 회수 {partly} · 이미 수령 {taken} · {reason}");
            return new RecallResult(recalled, partly, taken);
        }

        // 게임 서버가 우편을 저장하기 직전에 부른다 — 첨부에서 빠진 운영 지급 아이템을 「수령」으로 남긴다
        public void OnMailSaving(int mailId, string newSaveData)
        {
            var gm = db.GrantMails.FirstOrDefault(g => g.MailMessageId == mailId);
            var mail = db.MailMessages.Find(mailId);
            if (gm == null || mail == null) return;
            var before = AttachedIds(mail.SaveData);
            var after = AttachedIds(newSaveData);
            var granted = Ids(gm.ItemInstanceIds);
            var gone = granted.Where(id => before.Contains(id) && !after.Contains(id)).ToList();
            if (gone.Count == 0) return;
            LogFlows(gm, db.ItemInstances.Where(i => gone.Contains(i.ItemInstanceId)).ToList(), ItemFlowKinds.Taken, gm.GrantId, gm.CharacterName);
            if (!granted.Any(after.Contains)) gm.Status = GrantStatus.Taken;
            db.SaveChanges();
        }

        public void OnMailDeleting(int mailId)
        {
            var gm = db.GrantMails.FirstOrDefault(g => g.MailMessageId == mailId);
            var mail = db.MailMessages.Find(mailId);
            if (gm == null || mail == null || gm.Status != GrantStatus.Sent) return;
            var current = AttachedIds(mail.SaveData);
            gm.Status = Ids(gm.ItemInstanceIds).Any(current.Contains) ? DeletedByPlayer : GrantStatus.Taken;
            db.SaveChanges();
        }

        public object List(int page, int size)
        {
            page = Math.Max(1, page);
            size = Math.Clamp(size, 1, 100);
            var total = db.Grants.Count();
            var grants = db.Grants.OrderByDescending(g => g.Id).Skip((page - 1) * size).Take(size).ToList();
            var gids = grants.Select(g => g.Id).ToList();
            var mails = db.GrantMails.Where(m => gids.Contains(m.GrantId)).ToList();
            return new {
                Total = total,
                Items = grants.Select(g => new {
                    g.Id, g.Subject, g.Body, Items = JsonSerializer.Deserialize<List<GrantItem>>(g.ItemsJson, Web), g.CurrencyAmount, g.Reason,
                    g.CreatedBy, g.CreatedAt, g.RecalledAt, g.RecalledBy,
                    Mails = mails.Where(m => m.GrantId == g.Id).Select(m => new { m.PlayerCharacterId, m.CharacterName, m.MailMessageId, m.Status }),
                }),
            };
        }

        public List<string> KnownItems()
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var save in db.ItemInstances.OrderByDescending(i => i.Id).Take(5000).Select(i => i.SaveData)) {
                try {
                    var n = JsonNode.Parse(save)?["ItemName"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(n) && n != settings.CurrencyItemName) names.Add(n);
                } catch (Exception e) when (e is JsonException or InvalidOperationException) {
                    // 깨진 저장 데이터는 건너뛴다
                }
            }
            return names.ToList();
        }

        private void LogFlows(GrantMail gm, List<ItemInstance> instances, string kind, int grantId, string actor)
        {
            foreach (var group in instances.Select(i => JsonNode.Parse(i.SaveData)!).GroupBy(n => n["ItemName"]?.GetValue<string>() ?? "?")) {
                int currency = group.Sum(n => n["GainCurrencyAmount"]?.GetValue<int>() ?? 0);
                var name = currency > 0 ? (group.First()["GainCurrencyName"]?.GetValue<string>() ?? settings.BaseCurrencyName) : group.Key;
                db.ItemFlowLogs.Add(new ItemFlowLog {
                    PlayerCharacterId = gm.PlayerCharacterId, CharacterName = gm.CharacterName, Kind = kind, ItemName = name,
                    Quantity = currency > 0 ? 0 : group.Count(), CurrencyAmount = currency, GrantId = grantId, MailMessageId = gm.MailMessageId,
                    Actor = actor, At = DateTime.UtcNow,
                });
            }
        }

        private void Flow(PlayerCharacter c, string kind, string item, int qty, int currency, int grantId, int mailId, string actor) =>
            db.ItemFlowLogs.Add(new ItemFlowLog {
                PlayerCharacterId = c.Id, CharacterName = c.Name, Kind = kind, ItemName = item, Quantity = qty, CurrencyAmount = currency,
                GrantId = grantId, MailMessageId = mailId, Actor = actor, At = DateTime.UtcNow,
            });

        private static List<long> Ids(string csv) =>
            csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToList();

        public static HashSet<long> AttachedIds(string mailSave)
        {
            var set = new HashSet<long>();
            if (JsonNode.Parse(mailSave)?["AttachmentSlots"] is JsonArray slots) {
                foreach (var slot in slots) {
                    if (slot?["ItemInstanceIds"] is JsonArray arr) foreach (var id in arr) set.Add(id!.GetValue<long>());
                }
            }
            return set;
        }

        private static (string json, bool empty) RemoveIds(string mailSave, HashSet<long> remove)
        {
            var node = JsonNode.Parse(mailSave)!;
            var kept = new JsonArray();
            if (node["AttachmentSlots"] is JsonArray slots) {
                foreach (var slot in slots) {
                    var ids = (slot?["ItemInstanceIds"] as JsonArray ?? new JsonArray()).Select(x => x!.GetValue<long>()).Where(x => !remove.Contains(x)).ToList();
                    if (ids.Count > 0) kept.Add(new JsonObject { ["ItemInstanceIds"] = new JsonArray(ids.Select(x => (JsonNode)x).ToArray()) });
                }
            }
            node["AttachmentSlots"] = kept;
            return (node.ToJsonString(), kept.Count == 0);
        }

        // 게임 ItemInstanceSaveData(JsonUtility) 필드 이름 그대로
        private static string ItemSave(long id, string itemName, string? currencyName, int currencyAmount) => new JsonObject {
            ["ItemName"] = itemName, ["DisplayName"] = "", ["ItemQuality"] = "", ["DropLevel"] = 1, ["ItemInstanceId"] = id,
            ["RandomSecondaryStatIndexes"] = new JsonArray(), ["GainCurrencyName"] = currencyName ?? "", ["GainCurrencyAmount"] = currencyAmount,
        }.ToJsonString();

        private string MailSave(int messageId, string recipient, string subject, string body, JsonArray slots) => new JsonObject {
            ["MessageId"] = messageId, ["Sender"] = settings.MailSender, ["Recipient"] = recipient, ["Subject"] = subject, ["Body"] = body,
            ["AttachmentSlots"] = slots, ["CurrencyAmount"] = 0, ["IsRead"] = false,
        }.ToJsonString();
    }
}
