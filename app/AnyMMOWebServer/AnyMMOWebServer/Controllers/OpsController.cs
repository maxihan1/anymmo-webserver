// 운영자 API (/opsapi) — 로그인 · 요약 · 유저 · 캐릭터 · 제재. 운영자 토큰(OperatorBearer)만 받는다
using AnyMMOWebServer.Database;
using AnyMMOWebServer.Ops;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AnyMMOWebServer.Controllers
{
    // OpsError 를 { code, message } 와 상태 코드로 바꾼다
    public class OpsErrorFilter : IExceptionFilter
    {
        public void OnException(ExceptionContext context)
        {
            if (context.Exception is OpsError e) {
                context.Result = new ObjectResult(new { code = e.Code, message = e.Message }) { StatusCode = e.Status };
                context.ExceptionHandled = true;
            }
        }
    }

    [ApiController]
    [Authorize(AuthenticationSchemes = OpsAuth.Scheme)]
    [TypeFilter(typeof(OpsErrorFilter))]
    public abstract class OpsControllerBase : ControllerBase
    {
        protected string Op => User.FindFirst(OpsAuth.ClaimOperatorName)?.Value ?? throw new OpsError(401, "UNAUTHORIZED", "운영자 로그인이 필요합니다");
    }

    public record OpsLoginRequest(string UserName, string Password);
    public record SanctionRequest(string Type, string Reason, DateTime? EndsAt);
    public record ReasonRequest(string Reason);

    [Route("opsapi")]
    public class OpsController : OpsControllerBase
    {
        private readonly GameDbContext db;
        private readonly OpsAuthService auth;
        private readonly OpsUserService users;
        private readonly AuditService audit;

        public OpsController(GameDbContext db, OpsAuthService auth, OpsUserService users, AuditService audit)
        {
            this.db = db;
            this.auth = auth;
            this.users = users;
            this.audit = audit;
        }

        [AllowAnonymous]
        [HttpPost("login")]
        public ActionResult Login(OpsLoginRequest req)
        {
            var r = auth.Login(req.UserName ?? "", req.Password ?? "");
            if (r == null) {
                return Unauthorized(new { code = "UNAUTHORIZED", message = "아이디 또는 비밀번호가 맞지 않습니다" });
            }
            audit.Write(r.Value.op.UserName, "AUTH", "LOGIN", r.Value.op.UserName, "");
            return Ok(new { token = r.Value.token, @operator = new { r.Value.op.Id, r.Value.op.UserName, r.Value.op.DisplayName } });
        }

        [HttpGet("me")]
        public ActionResult Me()
        {
            var op = db.Operators.SingleOrDefault(o => o.UserName == Op);
            if (op == null) return Unauthorized(new { code = "UNAUTHORIZED", message = "운영자 로그인이 필요합니다" });
            return Ok(new { op.Id, op.UserName, op.DisplayName });
        }

        [HttpGet("summary")]
        public ActionResult Summary()
        {
            var now = DateTime.UtcNow;
            var today = now.Date;
            var active = db.Sanctions.Where(s => s.ReleasedAt == null && s.StartsAt <= now && (s.EndsAt == null || s.EndsAt > now));
            var m = db.Maintenances.Find(1);
            return Ok(new {
                users = db.Users.Count(),
                characters = db.PlayerCharacters.Count(),
                suspended = active.Where(s => s.Type == SanctionTypes.Suspend).Select(s => s.AccountId).Distinct().Count(),
                chatMuted = active.Where(s => s.Type == SanctionTypes.ChatMute).Select(s => s.AccountId).Distinct().Count(),
                maintenance = m != null && m.IsActive(now),
                noticesShowing = db.Notices.Count(n => n.Enabled && n.StartsAt <= now && (n.EndsAt == null || n.EndsAt > now)),
                grantsToday = db.Grants.Count(g => g.CreatedAt >= today),
                unclaimedGrantMails = db.GrantMails.Count(g => g.Status == GrantStatus.Sent),
            });
        }

        [HttpGet("users")]
        public ActionResult Users(string q = "", int page = 1, int size = 20) => Ok(users.Users(q ?? "", page, size));

        [HttpGet("users/{id:int}")]
        public ActionResult UserDetail(int id) => Ok(users.User(id));

        [HttpPost("users/{id:int}/sanctions")]
        public ActionResult AddSanction(int id, SanctionRequest req) => Ok(users.AddSanction(Op, id, req.Type ?? "", req.Reason ?? "", req.EndsAt));

        [HttpPost("sanctions/{id:int}/release")]
        public ActionResult Release(int id, ReasonRequest req) => Ok(users.Release(Op, id, req.Reason ?? ""));

        [HttpGet("characters")]
        public ActionResult Characters(string q = "") => Ok(users.Characters(q ?? ""));
    }
}
