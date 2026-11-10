// 게임 클라이언트 · 게임 서버가 읽는 운영 상태 — 점검 · 공지(누구나), 계정 제재(게임 서버 토큰)
using AnyMMOWebServer.Models;
using AnyMMOWebServer.Ops;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnyMMOWebServer.Controllers
{
    public record AccountRestrictionsRequest(int AccountId);

    [ApiController]
    [Route("api")]
    public class GameStatusController : ControllerBase
    {
        private readonly NoticeService notices;
        private readonly OpsUserService users;
        private readonly AnyMMOWebServerSettings settings;

        public GameStatusController(NoticeService notices, OpsUserService users, AnyMMOWebServerSettings settings)
        {
            this.notices = notices;
            this.users = users;
            this.settings = settings;
        }

        [HttpGet("serverstatus")]
        public ActionResult ServerStatus() => Ok(notices.ServerStatus());

        // 게임 서버가 채팅을 막을지 · 접속을 끊을지 판단할 때 부른다
        [HttpPost("getaccountrestrictions")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult AccountRestrictions(AccountRestrictionsRequest req)
        {
            var secret = User.FindFirst("sharedSecret")?.Value;
            if (secret == null || secret != settings.SharedSecret) {
                return Unauthorized("invalid shared secret");
            }
            var r = users.RestrictionsOf(req.AccountId);
            return Ok(new {
                accountId = req.AccountId,
                suspended = r.Suspend != null,
                suspendedUntil = r.Suspend?.EndsAt,
                suspendReason = r.Suspend?.Reason,
                chatMuted = r.ChatMute != null,
                chatMutedUntil = r.ChatMute?.EndsAt,
                chatMuteReason = r.ChatMute?.Reason,
                maintenanceBlocked = notices.BlockedByMaintenance(req.AccountId),
            });
        }
    }
}
