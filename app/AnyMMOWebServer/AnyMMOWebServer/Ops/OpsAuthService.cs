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
    }

    public static class OpsAuth
    {
        public const string Scheme = "OperatorBearer";
        public const string Issuer = "anymmo-ops";
        public const string ClaimOperatorId = "operatorId";
        public const string ClaimOperatorName = "operatorName";

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

        public static void CheckKeys(string gameKey, string operatorKey)
        {
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
