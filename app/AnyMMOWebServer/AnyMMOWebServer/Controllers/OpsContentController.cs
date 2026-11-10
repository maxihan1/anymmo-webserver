// 운영자 API (/opsapi) — 우편 지급 · 회수 · 공지 · 점검 · 작업 기록 · 아이템 흐름
using AnyMMOWebServer.Ops;
using Microsoft.AspNetCore.Mvc;

namespace AnyMMOWebServer.Controllers
{
    [Route("opsapi")]
    public class OpsContentController : OpsControllerBase
    {
        private readonly GrantService grants;
        private readonly NoticeService notices;

        public OpsContentController(GrantService grants, NoticeService notices)
        {
            this.grants = grants;
            this.notices = notices;
        }

        [HttpGet("items/known")]
        public ActionResult KnownItems() => Ok(grants.KnownItems());

        [HttpGet("grants")]
        public ActionResult Grants(int page = 1, int size = 20) => Ok(grants.List(page, size));

        [HttpPost("grants")]
        public ActionResult Send(GrantRequest req) => Ok(new { grants.Send(Op, req).Id });

        [HttpPost("grants/{id:int}/recall")]
        public ActionResult Recall(int id, ReasonRequest req) => Ok(grants.Recall(Op, id, req.Reason ?? ""));

        [HttpGet("notices")]
        public ActionResult Notices() => Ok(notices.Notices());

        [HttpPost("notices")]
        public ActionResult CreateNotice(NoticeInput input) => Ok(notices.Save(Op, null, input));

        [HttpPut("notices/{id:int}")]
        public ActionResult UpdateNotice(int id, NoticeInput input) => Ok(notices.Save(Op, id, input));

        [HttpDelete("notices/{id:int}")]
        public ActionResult DeleteNotice(int id)
        {
            notices.Delete(Op, id);
            return NoContent();
        }

        [HttpGet("maintenance")]
        public ActionResult Maintenance() => Ok(notices.Maintenance());

        [HttpPut("maintenance")]
        public ActionResult SetMaintenance(MaintenanceInput input) => Ok(notices.SetMaintenance(Op, input));

        [HttpGet("logs/audit")]
        public ActionResult AuditLogs(string area = "", string q = "", int page = 1, int size = 50) =>
            Ok(notices.AuditLogs(area ?? "", q ?? "", page, size));

        [HttpGet("logs/items")]
        public ActionResult ItemFlows(string kind = "", string q = "", int? characterId = null, int page = 1, int size = 50) =>
            Ok(notices.ItemFlows(kind ?? "", q ?? "", characterId, page, size));
    }
}
