// 운영자 API 를 실제 HTTP 로 검사한다 — 운영자 토큰과 게임 토큰이 서로의 API 를 못 쓰는지, 정지 · 점검이 로그인을 막는지
using AnyMMOWebServer.Database;
using AnyMMOWebServer.Models;
using AnyMMOWebServer.Ops;
using AnyMMOWebServer.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AnyMMOWebServer.Tests
{
    public class OpsApiTests : IClassFixture<OpsApiTests.Factory>
    {
        public class Factory : WebApplicationFactory<Program>
        {
            public const string OpUser = "opadmin";
            public const string OpPassword = "op-password-1";
            public const string AdminKey = "admin-key-for-tests-0123456789abcdef";
            private readonly string dbName = Guid.NewGuid().ToString();

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("ConnectionStrings:Db", "server=unused;database=unused");
                builder.UseSetting("AnyMMOWebServerSettings:BearerKey", "game-bearer-key-for-tests-0123456789");
                builder.UseSetting("AnyMMOWebServerSettings:SharedSecret", "shared-secret");
                builder.UseSetting("AnyMMOOpsSettings:OperatorBearerKey", "operator-bearer-key-for-tests-0123456789");
                builder.UseSetting("AnyMMOOpsSettings:SeedOperatorUserName", OpUser);
                builder.UseSetting("AnyMMOOpsSettings:SeedOperatorPassword", OpPassword);
                builder.UseSetting("AnyMMOOpsSettings:AdminKey", AdminKey);
                builder.ConfigureServices(services => {
                    var old = services.Single(d => d.ServiceType == typeof(DbContextOptions<GameDbContext>));
                    services.Remove(old);
                    services.AddDbContext<GameDbContext>(o => o.UseInMemoryDatabase(dbName));
                });
            }
        }

        private readonly Factory factory;

        public OpsApiTests(Factory factory)
        {
            this.factory = factory;
        }

        private async Task<string> OperatorToken(HttpClient c)
        {
            var res = await c.PostAsJsonAsync("/opsapi/login", new { userName = Factory.OpUser, password = Factory.OpPassword });
            res.EnsureSuccessStatusCode();
            return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        }

        private int AddUser(string name, string password)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            var u = new User { UserName = name, Email = $"{name}@x", PasswordHash = password };
            u.ProvideSaltAndHash();
            db.Users.Add(u);
            db.SaveChanges();
            return u.Id;
        }

        private static HttpRequestMessage Get(string url, string token)
        {
            var m = new HttpRequestMessage(HttpMethod.Get, url);
            m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return m;
        }

        [Fact]
        public async Task Ops_api_needs_an_operator_token_not_a_game_token()
        {
            var c = factory.CreateClient();
            AddUser("gamer1", "pw-gamer1");
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/opsapi/users")).StatusCode);

            var login = await c.PostAsJsonAsync("/api/login", new { userName = "gamer1", password = "pw-gamer1" });
            login.EnsureSuccessStatusCode();
            var gameToken = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Get("/opsapi/users", gameToken))).StatusCode);

            var opToken = await OperatorToken(c);
            var ok = await c.SendAsync(Get("/opsapi/users", opToken));
            Assert.True(ok.StatusCode == HttpStatusCode.OK, $"{ok.StatusCode} {ok.Headers.WwwAuthenticate}");

            var gameApi = new HttpRequestMessage(HttpMethod.Post, "/api/getallplayercharacters");
            gameApi.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(gameApi)).StatusCode);
        }

        [Fact]
        public async Task Game_server_reads_account_restrictions_with_its_token()
        {
            var c = factory.CreateClient();
            var id = AddUser("gamer4", "pw-gamer4");
            using (var scope = factory.Services.CreateScope()) {
                scope.ServiceProvider.GetRequiredService<OpsUserService>().AddSanction("op", id, SanctionTypes.ChatMute, "도배", DateTime.UtcNow.AddHours(1));
            }
            var server = await c.PostAsJsonAsync("/api/serverlogin", new { sharedSecret = "shared-secret" });
            server.EnsureSuccessStatusCode();
            var serverToken = (await server.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/getaccountrestrictions") { Content = JsonContent.Create(new { accountId = id }) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serverToken);
            var res = await c.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("chatMuted").GetBoolean());
            Assert.False(body.GetProperty("suspended").GetBoolean());

            var asOperator = new HttpRequestMessage(HttpMethod.Post, "/api/getaccountrestrictions") { Content = JsonContent.Create(new { accountId = id }) };
            asOperator.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await OperatorToken(c));
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(asOperator)).StatusCode);
        }

        [Fact]
        public async Task Wrong_operator_password_is_rejected_with_code()
        {
            var c = factory.CreateClient();
            var res = await c.PostAsJsonAsync("/opsapi/login", new { userName = Factory.OpUser, password = "nope" });
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.Equal("UNAUTHORIZED", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        [Fact]
        public async Task Suspended_account_cannot_log_in_until_released()
        {
            var c = factory.CreateClient();
            var id = AddUser("gamer2", "pw-gamer2");
            var op = await OperatorToken(c);
            var add = new HttpRequestMessage(HttpMethod.Post, $"/opsapi/users/{id}/sanctions") {
                Content = JsonContent.Create(new { type = "SUSPEND", reason = "작업장 의심" }),
            };
            add.Headers.Authorization = new AuthenticationHeaderValue("Bearer", op);
            var added = await c.SendAsync(add);
            added.EnsureSuccessStatusCode();
            var sanctionId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

            var blocked = await c.PostAsJsonAsync("/api/login", new { userName = "gamer2", password = "pw-gamer2" });
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Equal("SUSPENDED", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

            // 비밀번호가 틀리면 정지 여부를 알려 주지 않는다
            var wrong = await c.PostAsJsonAsync("/api/login", new { userName = "gamer2", password = "bad" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

            var release = new HttpRequestMessage(HttpMethod.Post, $"/opsapi/sanctions/{sanctionId}/release") {
                Content = JsonContent.Create(new { reason = "소명 확인" }),
            };
            release.Headers.Authorization = new AuthenticationHeaderValue("Bearer", op);
            (await c.SendAsync(release)).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/login", new { userName = "gamer2", password = "pw-gamer2" })).StatusCode);
        }

        [Fact]
        public async Task Maintenance_blocks_login_and_shows_in_server_status()
        {
            var c = factory.CreateClient();
            AddUser("gamer3", "pw-gamer3");
            var op = await OperatorToken(c);
            async Task SetMaintenance(bool on)
            {
                var put = new HttpRequestMessage(HttpMethod.Put, "/opsapi/maintenance") {
                    Content = JsonContent.Create(new { enabled = on, message = "정기 점검 중입니다" }),
                };
                put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", op);
                (await c.SendAsync(put)).EnsureSuccessStatusCode();
            }
            await SetMaintenance(true);
            try {
                var status = await c.GetFromJsonAsync<JsonElement>("/api/serverstatus");
                Assert.True(status.GetProperty("maintenance").GetProperty("active").GetBoolean());
                var res = await c.PostAsJsonAsync("/api/login", new { userName = "gamer3", password = "pw-gamer3" });
                Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
                Assert.Equal("MAINTENANCE", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            } finally {
                await SetMaintenance(false);
            }
        }

        [Fact]
        public async Task Grant_with_empty_body_goes_through_http()
        {
            var c = factory.CreateClient();
            int characterId;
            using (var scope = factory.Services.CreateScope()) {
                var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
                var pc = new PlayerCharacter { AccountId = 1, Name = "HttpHero", SaveData = "{}" };
                db.PlayerCharacters.Add(pc);
                db.SaveChanges();
                characterId = pc.Id;
            }
            var op = await OperatorToken(c);
            var post = new HttpRequestMessage(HttpMethod.Post, "/opsapi/grants") {
                Content = JsonContent.Create(new { characterIds = new[] { characterId }, subject = "보상", body = "", items = Array.Empty<object>(), currencyAmount = 100, reason = "테스트" }),
            };
            post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", op);
            var res = await c.SendAsync(post);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var bad = new HttpRequestMessage(HttpMethod.Post, "/opsapi/grants") {
                Content = JsonContent.Create(new { characterIds = new[] { characterId }, subject = "", body = "", items = Array.Empty<object>(), currencyAmount = 100, reason = "테스트" }),
            };
            bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", op);
            var badRes = await c.SendAsync(bad);
            Assert.Equal(HttpStatusCode.BadRequest, badRes.StatusCode);
            Assert.Equal("INVALID", (await badRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        [Fact]
        public async Task Fixed_admin_key_header_opens_ops_api_without_login()
        {
            var c = factory.CreateClient();
            var withKey = new HttpRequestMessage(HttpMethod.Get, "/opsapi/users");
            withKey.Headers.Add("X-Admin-Key", Factory.AdminKey);
            Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(withKey)).StatusCode);

            var wrongKey = new HttpRequestMessage(HttpMethod.Get, "/opsapi/users");
            wrongKey.Headers.Add("X-Admin-Key", Factory.AdminKey + "x");
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(wrongKey)).StatusCode);

            var gameApi = new HttpRequestMessage(HttpMethod.Post, "/api/getallplayercharacters");
            gameApi.Headers.Add("X-Admin-Key", Factory.AdminKey);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(gameApi)).StatusCode);
        }
    }
}
