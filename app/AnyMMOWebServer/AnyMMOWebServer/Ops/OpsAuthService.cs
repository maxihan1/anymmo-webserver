// 운영자 로그인 · 토큰 발급 · 첫 운영자 계정 만들기 — 게임 계정 인증과 키 · 표가 따로다
using AnyMMOWebServer.Database;
using AnyMMOWebServer.Services;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace AnyMMOWebServer.Ops
{
    public class OpsSettings
    {
        // 게임 BearerKey 와 달라야 한다(같으면 서버가 켜지지 않는다)
        public string OperatorBearerKey { get; set; } = string.Empty;
        // 비어 있지 않으면 켤 때 이 운영자가 없을 경우 만든다
        public string SeedOperatorUserName { get; set; } = string.Empty;
        public string SeedOperatorPassword { get; set; } = string.Empty;
        // 우편 화폐 첨부 — 게임 설정(DefaultCurrencyGroup.BaseCurrency) 이름
        public string BaseCurrencyName { get; set; } = "Gold";
        public string CurrencyItemName { get; set; } = "System Currency Loot Item";
        public string MailSender { get; set; } = "운영팀";
        // 자동 테스트용 고정 운영 키(X-Admin-Key) — 운영자 토큰은 12시간이면 만료돼 밤새 도는 정기 실행이 깨진다. 비어 있으면 꺼진다
        public string AdminKey { get; set; } = string.Empty;
        // 고정 키로 들어온 요청이 기록에 남는 운영자 이름
        public string AdminKeyOperatorName { get; set; } = "qa-admin-key";
    }

    public static class OpsAuth
    {
        public const string Scheme = "OperatorBearer";
        public const string Issuer = "anymmo-ops";
        public const string ClaimOperatorId = "operatorId";
        public const string ClaimOperatorName = "operatorName";
        public const string AdminKeyHeader = "X-Admin-Key";

        // 고정 운영 키가 맞으면 그 이름의 운영자로 들인다(길이가 달라도 시간이 새지 않게 비교)
        public static ClaimsPrincipal? AdminKeyPrincipal(string? presented, OpsSettings settings)
        {
            if (string.IsNullOrEmpty(settings.AdminKey) || string.IsNullOrEmpty(presented)) return null;
            var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
            var b = SHA256.HashData(Encoding.UTF8.GetBytes(settings.AdminKey));
            if (!CryptographicOperations.FixedTimeEquals(a, b)) return null;
            return new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim(ClaimOperatorId, "0"),
                new Claim(ClaimOperatorName, settings.AdminKeyOperatorName),
            }, Scheme));
        }

        public static TokenValidationParameters Validation(string key) => new TokenValidationParameters {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Issuer,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        public static void CheckKeys(string gameKey, string operatorKey, string adminKey = "")
        {
            if (adminKey.Length > 0 && adminKey.Length < 32) {
                throw new InvalidOperationException("AnyMMOOpsSettings:AdminKey must be empty or at least 32 characters.");
            }
            if (operatorKey.Length < 32) {
                throw new InvalidOperationException("AnyMMOOpsSettings:OperatorBearerKey must be at least 32 characters.");
            }
            if (operatorKey == gameKey) {
                throw new InvalidOperationException("AnyMMOOpsSettings:OperatorBearerKey must differ from AnyMMOWebServerSettings:BearerKey.");
            }
        }
    }

    public class OpsAuthService
    {
        private readonly GameDbContext db;
        private readonly OpsSettings settings;

        public OpsAuthService(GameDbContext db, OpsSettings settings)
        {
            this.db = db;
            this.settings = settings;
        }

        public Operator Create(string userName, string password, string displayName)
        {
            if (string.IsNullOrWhiteSpace(userName) || password.Length < 8) {
                throw new OpsError(400, "INVALID", "운영자 아이디와 8자 이상 비밀번호가 필요합니다");
            }
            if (db.Operators.Any(o => o.UserName == userName)) {
                throw new OpsError(409, "DUPLICATE", "이미 있는 운영자 아이디입니다");
            }
            var op = new Operator {
                UserName = userName,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName,
                Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
                CreatedAt = DateTime.UtcNow,
            };
            op.PasswordHash = AuthenticationHelpers.ComputeHash(password, op.Salt);
            db.Operators.Add(op);
            db.SaveChanges();
            return op;
        }

        public void EnsureSeed()
        {
            if (settings.SeedOperatorUserName == string.Empty || settings.SeedOperatorPassword == string.Empty) {
                return;
            }
            if (!db.Operators.Any(o => o.UserName == settings.SeedOperatorUserName)) {
                Create(settings.SeedOperatorUserName, settings.SeedOperatorPassword, settings.SeedOperatorUserName);
            }
        }

        public (Operator op, string token)? Login(string userName, string password)
        {
            var op = db.Operators.SingleOrDefault(o => o.UserName == userName);
            if (op == null || op.PasswordHash != AuthenticationHelpers.ComputeHash(password, op.Salt)) {
                return null;
            }
            return (op, IssueToken(op));
        }

        public string IssueToken(Operator op)
        {
            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateToken(new SecurityTokenDescriptor {
                Subject = new ClaimsIdentity(new[] {
                    new Claim(OpsAuth.ClaimOperatorId, op.Id.ToString()),
                    new Claim(OpsAuth.ClaimOperatorName, op.UserName),
                }),
                Issuer = OpsAuth.Issuer,
                Audience = OpsAuth.Issuer,
                Expires = DateTime.UtcNow.AddHours(12),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.OperatorBearerKey)),
                    SecurityAlgorithms.HmacSha256Signature),
            });
            return handler.WriteToken(token);
        }
    }

    // 서비스가 던지면 컨트롤러가 { code, message } 와 상태 코드로 바꾼다
    public class OpsError : Exception
    {
        public int Status { get; }
        public string Code { get; }

        public OpsError(int status, string code, string message) : base(message)
        {
            Status = status;
            Code = code;
        }
    }

    public class AuditService
    {
        private readonly GameDbContext db;

        public AuditService(GameDbContext db)
        {
            this.db = db;
        }

        public void Write(string op, string area, string action, string target, string detail)
        {
            db.OperatorAuditLogs.Add(new OperatorAuditLog {
                Operator = op, Area = area, Action = action, Target = target, Detail = detail, At = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
    }
}
