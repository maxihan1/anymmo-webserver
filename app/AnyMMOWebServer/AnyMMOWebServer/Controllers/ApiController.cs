using AnyMMOWebServer.Database;
using AnyMMOWebServer.Models;
using AnyMMOWebServer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using NuGet.Protocol;
using Microsoft.EntityFrameworkCore.Diagnostics;
using AnyMMOWebServer.Ops;

namespace AnyMMOWebServer.Controllers
{
    [ApiController]
    [Route("api")]
    public class ApiController : Controller
    {
        private readonly GameDbContext dbContext;
        private readonly PlayerCharacterService playerCharacterService;
        private readonly GuildService guildService;
        private readonly FriendListService friendListService;
        private readonly AuctionItemService auctionItemService;
        private readonly ItemInstanceService itemInstanceService;
        private readonly MailMessageService mailMessageService;
        private readonly ILogger<ApiController> logger;
        private readonly AnyMMOWebServer.Services.IAuthenticationService authenticationService;
        private readonly AnyMMOWebServerSettings anyMMOWebServerSettings;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly OpsUserService opsUserService;
        private readonly NoticeService noticeService;
        private readonly GrantService grantService;


        public ApiController(
            ILogger<ApiController> logger,
            GameDbContext dbContext,
            AnyMMOWebServerSettings anyMMOWebServerSettings,
            AnyMMOWebServer.Services.IAuthenticationService authenticationService,
            IHttpContextAccessor httpContextAccessor,
            OpsUserService opsUserService,
            NoticeService noticeService,
            GrantService grantService)
        {
            this.opsUserService = opsUserService;
            this.noticeService = noticeService;
            this.grantService = grantService;
            this.dbContext = dbContext;
            this._httpContextAccessor = httpContextAccessor;
            this.anyMMOWebServerSettings = anyMMOWebServerSettings;
            playerCharacterService = new PlayerCharacterService(dbContext, logger, httpContextAccessor);
            guildService = new GuildService(dbContext, logger, httpContextAccessor);
            friendListService = new FriendListService(dbContext, logger, httpContextAccessor);
            auctionItemService = new AuctionItemService(dbContext, logger, httpContextAccessor);
            itemInstanceService = new ItemInstanceService(dbContext, logger, httpContextAccessor);
            mailMessageService = new MailMessageService(dbContext, logger, httpContextAccessor);
            this.authenticationService = authenticationService;
            //authenticationService = new AnyMMOWebServer.Services.AuthenticationService(anyMMOWebServerSettings, dbContext, logger);
            this.logger = logger;
        }

        [HttpPost("login")]
        public ActionResult Login(AuthenticationRequest authenticationRequest)
        {
            try
            {
                var (success, content) = authenticationService.Login(authenticationRequest);
                if (!success)
                {
                    return Unauthorized(content);
                }
                // ops: 비밀번호가 맞은 뒤에만 정지 · 점검을 알려 준다(틀린 비밀번호로 계정 상태를 떠보지 못하게)
                var suspend = opsUserService.RestrictionsOf(content!.AccountId).Suspend;
                if (suspend != null) {
                    return StatusCode(403, new { code = "SUSPENDED", message = suspend.Reason, until = suspend.EndsAt });
                }
                if (noticeService.BlockedByMaintenance(content.AccountId)) {
                    var m = noticeService.Maintenance();
                    return StatusCode(503, new { code = "MAINTENANCE", message = m.Message, until = m.EndsAt });
                }
                return Ok(content);
            } catch (Exception e)
            {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("serverlogin")]
        public ActionResult ServerLogin(ServerAuthenticationRequest authenticationRequest) {
            try {
                var (success, content) = authenticationService.ServerLogin(authenticationRequest);
                if (!success) {
                    return BadRequest(content);
                }
                AuthenticationResponse authenticationResponse = new AuthenticationResponse() { Token = content };
                return Ok(authenticationResponse);
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        // *******************************************************************
        // PLAYER CHARACTER
        // *******************************************************************

        [HttpPost("createplayercharacter")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult CreatePlayerCharacter(CreateCharacterRequest createCharacterRequest)
        {
            try
            {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                (bool success, PlayerCharacter playerCharacter) = playerCharacterService.AddPlayerCharacter(createCharacterRequest);
                if (!success)
                {
                    return BadRequest();
                }
                return Ok(new CreatePlayerCharacterResponse() { Id = playerCharacter.Id });
            } catch (Exception e)
            {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("saveplayercharacter")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult SavePlayerCharacter(SaveCharacterRequest saveCharacterRequest)
        {
            try
            {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                var success = playerCharacterService.SavePlayerCharacter(saveCharacterRequest);
                if (!success)
                {
                    return BadRequest();
                }
                return Ok();
            } catch (Exception e)
            {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("deleteplayercharacter")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult DeletePlayerCharacter(DeleteCharacterRequest deleteCharacterRequest)
        {
            try
            {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                var success = playerCharacterService.DeletePlayerCharacter(deleteCharacterRequest);
                if (!success)
                {
                    return BadRequest();
                }
                return Ok();
            } catch (Exception e)
            {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("getplayercharacters")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult GetPlayerCharacters(LoadPlayerCharacterListRequest loadPlayerCharacterListRequest)
        {
            try
            {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                PlayerCharacterListResponse playerCharacterListResponse = playerCharacterService.GetPlayerCharacters(loadPlayerCharacterListRequest.AccountId);
                return Ok(playerCharacterListResponse);
            } catch (Exception e)
            {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("getallplayercharacters")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult GetAllPlayerCharacters() {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                // get list of characters
                PlayerCharacterListResponse playerCharacterListResponse = playerCharacterService.GetAllPlayerCharacters();
                return Ok(playerCharacterListResponse);
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }


        // *******************************************************************
        // GUILD
        // *******************************************************************

        [HttpPost("createguild")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult CreateGuild(CreateGuildRequest createGuildRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                // add new guild
                (bool success, Guild guild) = guildService.AddGuild(createGuildRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok(new CreateGuildResponse() { Id = guild.Id });
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("saveguild")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult SaveGuild(SaveGuildRequest saveGuildRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                var success = guildService.SaveGuild(saveGuildRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok();
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("deleteguild")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult DeleteGuild(DeleteGuildRequest deleteGuildRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                var success = guildService.DeleteGuild(deleteGuildRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok();
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("getguilds")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult GetGuilds() {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                GuildListResponse guildListResponse = guildService.GetGuilds();
                return Ok(guildListResponse);
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        // *******************************************************************
        // FRIEND LIST
        // *******************************************************************

        [HttpPost("savefriendlist")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult SaveFriendList(SaveFriendListRequest saveFriendListRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                var success = friendListService.SaveFriendList(saveFriendListRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok();
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("getfriendlists")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult GetFriendLists() {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                FriendListResponse friendListResponse = friendListService.GetFriendLists();
                return Ok(friendListResponse);
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        // *******************************************************************
        // AUCTION ITEM
        // *******************************************************************

        [HttpPost("createauctionitem")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult CreateAuctionItem(CreateAuctionItemRequest createAuctionItemRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                // add new auctionItem
                (bool success, AuctionItem auctionItem) = auctionItemService.AddAuctionItem(createAuctionItemRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok(new CreateAuctionItemResponse() { Id = auctionItem.Id });
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("saveauctionitem")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult SaveAuctionItem(SaveAuctionItemRequest saveAuctionItemRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                var success = auctionItemService.SaveAuctionItem(saveAuctionItemRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok();
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("deleteauctionitem")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult DeleteAuctionItem(DeleteAuctionItemRequest deleteAuctionItemRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                var success = auctionItemService.DeleteAuctionItem(deleteAuctionItemRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok();
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("getauctionitems")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult GetAuctionItems() {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                AuctionItemListResponse auctionItemListResponse = auctionItemService.GetAuctionItems();
                return Ok(auctionItemListResponse);
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        // *******************************************************************
        // ITEM INSTANCE
        // *******************************************************************

        [HttpPost("createiteminstance")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult CreateItemInstance(CreateItemInstanceRequest createItemInstanceRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                // add new itemInstance
                (bool success, ItemInstance itemInstance) = itemInstanceService.AddItemInstance(createItemInstanceRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok(new CreateItemInstanceResponse() { Id = itemInstance.Id });
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("saveiteminstance")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult SaveItemInstance(SaveItemInstanceRequest saveItemInstanceRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                var success = itemInstanceService.SaveItemInstance(saveItemInstanceRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok();
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("deleteiteminstance")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult DeleteItemInstance(DeleteItemInstanceRequest deleteItemInstanceRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                var success = itemInstanceService.DeleteItemInstance(deleteItemInstanceRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok();
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("getiteminstances")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult GetItemInstances() {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                ItemInstanceListResponse itemInstanceListResponse = itemInstanceService.GetItemInstances();
                return Ok(itemInstanceListResponse);
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        // *******************************************************************
        // MAIL MESSAGE
        // *******************************************************************

        [HttpPost("createmailmessage")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult CreateMailMessage(CreateMailMessageRequest createMailMessageRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                (bool success, MailMessage mailMessage) = mailMessageService.AddMailMessage(createMailMessageRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok(new CreateMailMessageResponse() { Id = mailMessage.Id });
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("savemailmessage")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult SaveMailMessage(SaveMailMessageRequest saveMailMessageRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                grantService.OnMailSaving(saveMailMessageRequest.Id, saveMailMessageRequest.SaveData);
                var success = mailMessageService.SaveMailMessage(saveMailMessageRequest);
                if (!success) {
                    return BadRequest();
                }
                return Ok();
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("deletemailmessage")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult DeleteMailMessage(DeleteMailMessageRequest deleteMailMessageRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                grantService.OnMailDeleting(deleteMailMessageRequest.Id);
                var success = mailMessageService.DeleteMailMessage(deleteMailMessageRequest);
                if (!success) {
                    return BadRequest("Delete mail message failed.  Message not found.");
                }
                return Ok();
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("getmailmessages")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult GetMailMessages(LoadMailMessagesRequest loadMailMessagesRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                MailMessageListResponse mailMessageListResponse = mailMessageService.GetMailMessages(loadMailMessagesRequest.PlayerCharacterId);
                return Ok(mailMessageListResponse);
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }

        [HttpPost("getmailmessage")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult GetMailMessage(LoadMailMessageRequest loadMailMessageRequest) {
            try {
                var sharedSecretString = User.FindFirst("sharedSecret")?.Value;
                if (sharedSecretString == null || sharedSecretString != anyMMOWebServerSettings.SharedSecret) {
                    return Unauthorized("invalid shared secret");
                }

                MailMessage? mailMessage = mailMessageService.GetMailMessage(loadMailMessageRequest.MessageId);
                if (mailMessage == null) {
                    return BadRequest("Mail message not found.");
                }
                return Ok(mailMessage);
            } catch (Exception e) {
                logger.LogError(e.ToString());
                return BadRequest("Error occured on server.  See server logs for more details.");
            }
        }


        // GET: Account/logout
        /*
        public ActionResult Logout()
        {
            authenticationService.Logout(HttpContext);
            return RedirectToAction(nameof(Index));
        }
        */

    }
}
