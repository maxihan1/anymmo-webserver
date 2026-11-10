// 운영툴이 쓰는 DB 표 — 운영자 계정 · 제재 · 우편 지급 · 공지 · 점검 · 작업/아이템 흐름 기록
namespace AnyMMOWebServer.Ops
{
    // 게임 계정(Users)과 표 · 토큰 서명 키를 따로 둬서 게임 토큰으로 운영 API 를 부를 수 없게 한다
    public class Operator
    {
        public int Id { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Salt { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public static class SanctionTypes
    {
        public const string Suspend = "SUSPEND";
        public const string ChatMute = "CHAT_MUTE";
        public static readonly string[] All = { Suspend, ChatMute };
    }

    public class Sanction
    {
        public int Id { get; set; }
        public int AccountId { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTime StartsAt { get; set; }
        // null 이면 풀 때까지 계속
        public DateTime? EndsAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? ReleasedAt { get; set; }
        public string? ReleasedBy { get; set; }
        public string? ReleaseReason { get; set; }

        public bool IsActive(DateTime now) => ReleasedAt == null && StartsAt <= now && (EndsAt == null || EndsAt > now);
    }

    public static class GrantStatus
    {
        public const string Sent = "SENT";
        public const string Taken = "TAKEN";
        public const string Recalled = "RECALLED";
        public const string PartlyRecalled = "PARTLY_RECALLED";
    }

    public class Grant
    {
        public int Id { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        // [{"itemName":"..","quantity":n}] 그대로 남긴다
        public string ItemsJson { get; set; } = "[]";
        public int CurrencyAmount { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? RecalledAt { get; set; }
        public string? RecalledBy { get; set; }
    }

    // 지급 한 건이 캐릭터마다 우편 한 통이 된다
    public class GrantMail
    {
        public int Id { get; set; }
        public int GrantId { get; set; }
        public int PlayerCharacterId { get; set; }
        public string CharacterName { get; set; } = string.Empty;
        public int MailMessageId { get; set; }
        // 이 우편에 붙인 아이템 인스턴스 번호(쉼표)
        public string ItemInstanceIds { get; set; } = string.Empty;
        public string Status { get; set; } = GrantStatus.Sent;
    }

    public class Notice
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime StartsAt { get; set; }
        public DateTime? EndsAt { get; set; }
        public bool Enabled { get; set; } = true;
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public bool IsShowing(DateTime now) => Enabled && StartsAt <= now && (EndsAt == null || EndsAt > now);
    }

    // 한 줄만 쓴다(Id 1)
    public class Maintenance
    {
        public int Id { get; set; }
        public bool Enabled { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime? StartsAt { get; set; }
        public DateTime? EndsAt { get; set; }
        // 점검 중에도 들어올 수 있는 게임 계정 번호(쉼표) — 점검 확인용
        public string AllowedAccountIds { get; set; } = string.Empty;
        public string UpdatedBy { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }

        public bool IsActive(DateTime now) => Enabled && (StartsAt == null || StartsAt <= now) && (EndsAt == null || EndsAt > now);
    }

    public class OperatorAuditLog
    {
        public int Id { get; set; }
        public string Operator { get; set; } = string.Empty;
        // USER · SANCTION · GRANT · NOTICE · MAINTENANCE · AUTH
        public string Area { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public DateTime At { get; set; }
    }

    public static class ItemFlowKinds
    {
        public const string Granted = "GRANTED";
        public const string Taken = "TAKEN";
        public const string Recalled = "RECALLED";
    }

    public class ItemFlowLog
    {
        public int Id { get; set; }
        public int PlayerCharacterId { get; set; }
        public string CharacterName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public int CurrencyAmount { get; set; }
        public int? GrantId { get; set; }
        public int? MailMessageId { get; set; }
        public string Actor { get; set; } = string.Empty;
        public DateTime At { get; set; }
    }
}
