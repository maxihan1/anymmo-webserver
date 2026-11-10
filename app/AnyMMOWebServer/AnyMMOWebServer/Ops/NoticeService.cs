// 공지 · 점검 상태 관리와 운영 기록(작업 · 아이템 흐름) 조회 — 게임이 읽는 서버 상태도 여기서 만든다
using AnyMMOWebServer.Database;

namespace AnyMMOWebServer.Ops
{
    public record NoticeInput(string Title, string Body, DateTime? StartsAt, DateTime? EndsAt, bool Enabled);
    public record MaintenanceInput(bool Enabled, string Message, DateTime? StartsAt, DateTime? EndsAt, List<int>? AllowedAccountIds);

    public class NoticeService
    {
        private readonly GameDbContext db;
        private readonly AuditService audit;

        public NoticeService(GameDbContext db, AuditService audit)
        {
            this.db = db;
            this.audit = audit;
        }

        private static void Check(bool ok, string message)
        {
            if (!ok) throw new OpsError(400, "INVALID", message);
        }

        public List<Notice> Notices() => db.Notices.OrderByDescending(n => n.Id).Take(200).ToList();

        public Notice Save(string op, int? id, NoticeInput input)
        {
            var title = (input.Title ?? "").Trim();
            var body = (input.Body ?? "").Trim();
            Check(title.Length is >= 1 and <= 60, "공지 제목을 1~60자로 입력하세요");
            Check(body.Length is >= 1 and <= 1000, "공지 내용을 1~1,000자로 입력하세요");
            var now = DateTime.UtcNow;
            var starts = input.StartsAt ?? now;
            Check(input.EndsAt == null || input.EndsAt > starts, "끝나는 시각은 시작보다 뒤여야 합니다");
            Notice n;
            if (id == null) {
                n = new Notice { CreatedBy = op, CreatedAt = now };
                db.Notices.Add(n);
            } else {
                n = db.Notices.Find(id.Value) ?? throw new OpsError(404, "NOT_FOUND", "공지를 찾을 수 없습니다");
            }
            n.Title = title;
            n.Body = body;
            n.StartsAt = starts;
            n.EndsAt = input.EndsAt;
            n.Enabled = input.Enabled;
            n.UpdatedAt = now;
            db.SaveChanges();
            audit.Write(op, "NOTICE", id == null ? "CREATE" : "UPDATE", $"공지 #{n.Id}", $"{title} · {(n.Enabled ? "켜짐" : "꺼짐")}");
            return n;
        }

        public void Delete(string op, int id)
        {
            var n = db.Notices.Find(id) ?? throw new OpsError(404, "NOT_FOUND", "공지를 찾을 수 없습니다");
            db.Notices.Remove(n);
            db.SaveChanges();
            audit.Write(op, "NOTICE", "DELETE", $"공지 #{id}", n.Title);
        }

        public Maintenance Maintenance() => db.Maintenances.Find(1) ?? new Maintenance { Id = 1 };

        public Maintenance SetMaintenance(string op, MaintenanceInput input)
        {
            var message = (input.Message ?? "").Trim();
            Check(!input.Enabled || message.Length is >= 1 and <= 300, "점검 안내 문구를 1~300자로 입력하세요");
            Check(message.Length <= 300, "점검 안내 문구는 300자까지입니다");
            Check(input.StartsAt == null || input.EndsAt == null || input.EndsAt > input.StartsAt, "끝나는 시각은 시작보다 뒤여야 합니다");
            var m = db.Maintenances.Find(1);
            if (m == null) {
                m = new Maintenance { Id = 1 };
                db.Maintenances.Add(m);
            }
            m.Enabled = input.Enabled;
            m.Message = message;
            m.StartsAt = input.StartsAt;
            m.EndsAt = input.EndsAt;
            m.AllowedAccountIds = string.Join(",", (input.AllowedAccountIds ?? new()).Distinct());
            m.UpdatedBy = op;
            m.UpdatedAt = DateTime.UtcNow;
            db.SaveChanges();
            audit.Write(op, "MAINTENANCE", input.Enabled ? "ON" : "OFF", "점검", $"{message} · 허용 계정 {(m.AllowedAccountIds == "" ? "없음" : m.AllowedAccountIds)}");
            return m;
        }

        // 게임 클라이언트 · 서버가 로그인 전에 읽는다 — 지금 걸린 점검과 보이는 공지
        public object ServerStatus()
        {
            var now = DateTime.UtcNow;
            var m = Maintenance();
            var notices = db.Notices.Where(n => n.Enabled && n.StartsAt <= now && (n.EndsAt == null || n.EndsAt > now))
                .OrderByDescending(n => n.StartsAt).Select(n => new { n.Id, n.Title, n.Body, n.StartsAt, n.EndsAt }).ToList();
            return new {
                Maintenance = new { Active = m.IsActive(now), m.Message, m.StartsAt, m.EndsAt },
                Notices = notices,
                ServerTime = now,
            };
        }

        public bool BlockedByMaintenance(int accountId)
        {
            var m = Maintenance();
            if (!m.IsActive(DateTime.UtcNow)) return false;
            return !m.AllowedAccountIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(accountId.ToString());
        }

        public object AuditLogs(string area, string q, int page, int size)
        {
            page = Math.Max(1, page);
            size = Math.Clamp(size, 1, 200);
            var logs = db.OperatorAuditLogs.AsQueryable();
            if (area != "") logs = logs.Where(a => a.Area == area);
            if (q != "") logs = logs.Where(a => a.Target.Contains(q) || a.Detail.Contains(q) || a.Operator.Contains(q));
            return new { Total = logs.Count(), Items = logs.OrderByDescending(a => a.Id).Skip((page - 1) * size).Take(size).ToList() };
        }

        public object ItemFlows(string kind, string q, int? characterId, int page, int size)
        {
            page = Math.Max(1, page);
            size = Math.Clamp(size, 1, 200);
            var flows = db.ItemFlowLogs.AsQueryable();
            if (kind != "") flows = flows.Where(f => f.Kind == kind);
            if (characterId != null) flows = flows.Where(f => f.PlayerCharacterId == characterId);
            if (q != "") flows = flows.Where(f => f.CharacterName.Contains(q) || f.ItemName.Contains(q));
            return new { Total = flows.Count(), Items = flows.OrderByDescending(f => f.Id).Skip((page - 1) * size).Take(size).ToList() };
        }
    }
}
