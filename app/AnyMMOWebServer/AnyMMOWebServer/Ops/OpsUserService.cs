// 운영툴 유저 · 캐릭터 조회와 제재(정지 · 채팅 금지 · 해제 · 이력)
using AnyMMOWebServer.Database;
using AnyMMOWebServer.Models;
using System.Text.Json;

namespace AnyMMOWebServer.Ops
{
    public record UserRow(int Id, string UserName, string Email, int CharacterCount, bool Suspended, bool ChatMuted);
    public record CharacterRow(int Id, int AccountId, string Name, int? Level, string? CharacterClass, string? Race, string? Scene, string? SavedOn,
        List<JsonElement> Currencies, Dictionary<string, int> Items);
    public record Restrictions(Sanction? Suspend, Sanction? ChatMute);
    public record PageOf<T>(List<T> Items, int Total);

    public class OpsUserService
    {
        private readonly GameDbContext db;
        private readonly AuditService audit;

        public OpsUserService(GameDbContext db, AuditService audit)
        {
            this.db = db;
            this.audit = audit;
        }

        public PageOf<UserRow> Users(string q, int page, int size)
        {
            page = Math.Max(1, page);
            size = Math.Clamp(size, 1, 100);
            var users = db.Users.AsQueryable();
            q = q.Trim();
            if (q != string.Empty) {
                var byCharacter = db.PlayerCharacters.Where(c => c.Name.Contains(q)).Select(c => c.AccountId);
                users = int.TryParse(q, out var id)
                    ? users.Where(u => u.Id == id || u.UserName.Contains(q) || byCharacter.Contains(u.Id))
                    : users.Where(u => u.UserName.Contains(q) || u.Email.Contains(q) || byCharacter.Contains(u.Id));
            }
            int total = users.Count();
            var list = users.OrderByDescending(u => u.Id).Skip((page - 1) * size).Take(size).ToList();
            var ids = list.Select(u => u.Id).ToList();
            var counts = db.PlayerCharacters.Where(c => ids.Contains(c.AccountId)).GroupBy(c => c.AccountId)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionary(x => x.Key, x => x.Count);
            var now = DateTime.UtcNow;
            var active = ActiveSanctions(ids, now);
            var rows = list.Select(u => new UserRow(u.Id, u.UserName, u.Email, counts.GetValueOrDefault(u.Id),
                active.Any(s => s.AccountId == u.Id && s.Type == SanctionTypes.Suspend),
                active.Any(s => s.AccountId == u.Id && s.Type == SanctionTypes.ChatMute))).ToList();
            return new PageOf<UserRow>(rows, total);
        }

        public object User(int accountId)
        {
            var u = db.Users.Find(accountId) ?? throw new OpsError(404, "NOT_FOUND", "유저를 찾을 수 없습니다");
            var r = RestrictionsOf(accountId);
            return new {
                u.Id, u.UserName, u.Email,
                Suspended = r.Suspend != null,
                ChatMuted = r.ChatMute != null,
                Characters = db.PlayerCharacters.Where(c => c.AccountId == accountId).OrderBy(c => c.Id).ToList().Select(Summarize).ToList(),
                Sanctions = db.Sanctions.Where(s => s.AccountId == accountId).OrderByDescending(s => s.Id).ToList(),
            };
        }

        public List<CharacterRow> Characters(string q)
        {
            q = q.Trim();
            var list = db.PlayerCharacters.Where(c => q == string.Empty || c.Name.Contains(q)).OrderBy(c => c.Name).Take(50).ToList();
            return list.Select(Summarize).ToList();
        }

        public static CharacterRow Summarize(PlayerCharacter c)
        {
            int? level = null;
            string? cls = null, race = null, scene = null, saved = null;
            var currencies = new List<JsonElement>();
            var items = new Dictionary<string, int>();
            try {
                using var doc = JsonDocument.Parse(c.SaveData == string.Empty ? "{}" : c.SaveData);
                var root = doc.RootElement;
                var ch = root.TryGetProperty("CharacterSaveData", out var inner) ? inner : root;
                if (ch.TryGetProperty("CharacterLevel", out var lv) && lv.TryGetInt32(out var l)) level = l;
                cls = Str(ch, "CharacterClass");
                race = Str(ch, "CharacterRace");
                scene = Str(ch, "CurrentScene");
                saved = Str(ch, "DataSavedOn");
                if (ch.TryGetProperty("CurrencySaveData", out var cur) && cur.ValueKind == JsonValueKind.Array) {
                    currencies = cur.EnumerateArray().Select(e => e.Clone()).ToList();
                }
                if (root.TryGetProperty("ItemInstanceListSaveData", out var il) && il.TryGetProperty("ItemInstances", out var arr)
                    && arr.ValueKind == JsonValueKind.Array) {
                    foreach (var it in arr.EnumerateArray()) {
                        var name = Str(it, "ItemName") ?? "?";
                        items[name] = items.GetValueOrDefault(name) + 1;
                    }
                }
            } catch (JsonException) {
                // 저장 데이터가 깨졌어도 이름 · 번호는 보여 준다
            }
            return new CharacterRow(c.Id, c.AccountId, c.Name, level, cls, race, scene, saved, currencies, items);
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private List<Sanction> ActiveSanctions(List<int> accountIds, DateTime now) =>
            db.Sanctions.Where(s => accountIds.Contains(s.AccountId) && s.ReleasedAt == null && s.StartsAt <= now && (s.EndsAt == null || s.EndsAt > now)).ToList();

        public Restrictions RestrictionsOf(int accountId)
        {
            var active = ActiveSanctions(new List<int> { accountId }, DateTime.UtcNow);
            return new Restrictions(
                active.Where(s => s.Type == SanctionTypes.Suspend).OrderByDescending(s => s.EndsAt ?? DateTime.MaxValue).FirstOrDefault(),
                active.Where(s => s.Type == SanctionTypes.ChatMute).OrderByDescending(s => s.EndsAt ?? DateTime.MaxValue).FirstOrDefault());
        }

        public Sanction AddSanction(string op, int accountId, string type, string reason, DateTime? endsAt)
        {
            var u = db.Users.Find(accountId) ?? throw new OpsError(404, "NOT_FOUND", "유저를 찾을 수 없습니다");
            if (!SanctionTypes.All.Contains(type)) throw new OpsError(400, "INVALID", "제재 종류는 SUSPEND 또는 CHAT_MUTE 입니다");
            reason = reason.Trim();
            if (reason.Length is < 1 or > 200) throw new OpsError(400, "INVALID", "사유를 1~200자로 입력하세요");
            var now = DateTime.UtcNow;
            if (endsAt != null && endsAt <= now) throw new OpsError(400, "INVALID", "끝나는 시각은 지금보다 뒤여야 합니다");
            var r = RestrictionsOf(accountId);
            if ((type == SanctionTypes.Suspend ? r.Suspend : r.ChatMute) != null) {
                throw new OpsError(409, "ALREADY_ACTIVE", "같은 종류의 제재가 이미 걸려 있습니다. 먼저 해제하세요");
            }
            var s = new Sanction {
                AccountId = accountId, Type = type, Reason = reason, StartsAt = now, EndsAt = endsAt, CreatedBy = op, CreatedAt = now,
            };
            db.Sanctions.Add(s);
            db.SaveChanges();
            audit.Write(op, "SANCTION", type, $"{u.UserName} (#{u.Id})", $"{reason} · 끝 {(endsAt == null ? "해제 때까지" : endsAt.Value.ToString("u"))}");
            return s;
        }

        public Sanction Release(string op, int sanctionId, string reason)
        {
            var s = db.Sanctions.Find(sanctionId) ?? throw new OpsError(404, "NOT_FOUND", "제재를 찾을 수 없습니다");
            if (!s.IsActive(DateTime.UtcNow)) throw new OpsError(409, "NOT_ACTIVE", "이미 끝났거나 해제된 제재입니다");
            reason = reason.Trim();
            if (reason.Length is < 1 or > 200) throw new OpsError(400, "INVALID", "해제 사유를 1~200자로 입력하세요");
            s.ReleasedAt = DateTime.UtcNow;
            s.ReleasedBy = op;
            s.ReleaseReason = reason;
            db.SaveChanges();
            audit.Write(op, "SANCTION", "RELEASE", $"#{s.AccountId} {s.Type}", reason);
            return s;
        }
    }
}
