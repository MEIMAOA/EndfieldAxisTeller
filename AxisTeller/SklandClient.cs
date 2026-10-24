using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AxisTeller;

public sealed class SklandSyncResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string RoleName { get; set; } = "";
    public string UserName { get; set; } = "";
    public List<Operator> Operators { get; } = new();
}

/// <summary>
/// 森空岛（国服 Skland / 国际服 Skport）数据同步。
/// 采用森空岛 App token -> OAuth grant -> cred -> 角色卡片 的公开流程，
/// 接口与签名算法参考社区实现（endfield-py / skland 系列项目）。
/// </summary>
public sealed class SklandClient
{
    private sealed record ServerConfig(
        string Key,
        string Name,
        string AppCode,
        string GrantUrl,
        string CredUrl,
        string BindUrl,
        string ApiHost,
        string Origin);

    private static readonly ServerConfig Cn = new(
        "cn",
        "国服 · 森空岛",
        "4ca99fa6b56cc2ba",
        "https://as.hypergryph.com/user/oauth2/v2/grant",
        "https://zonai.skland.com/api/v1/user/auth/generate_cred_by_code",
        "https://zonai.skland.com/api/v1/game/player/binding",
        "https://zonai.skland.com",
        "https://game.skland.com");

    private static readonly ServerConfig Global = new(
        "global",
        "国际服 · Skport",
        "6eb76d4e13aa36e6",
        "https://as.gryphline.com/user/oauth2/v2/grant",
        "https://zonai.skport.com/web/v1/user/auth/generate_cred_by_code",
        "https://zonai.skport.com/api/v1/game/player/binding",
        "https://zonai.skport.com",
        "https://game.skport.com");

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly Dictionary<string, string> ProfessionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DEFENDER"] = "重装",
        ["STRIKER"] = "突击",
        ["ASSAULT"] = "突击",
        ["GUARD"] = "近卫",
        ["CASTER"] = "术师",
        ["VANGUARD"] = "先锋",
        ["SUPPORTER"] = "辅助",
        ["重装"] = "重装",
        ["突击"] = "突击",
        ["近卫"] = "近卫",
        ["术师"] = "术师",
        ["先锋"] = "先锋",
        ["辅助"] = "辅助",
    };

    private static readonly Dictionary<string, (string name, string color)> ElementMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Physical"] = ("物理", "#d9dde3"),
        ["物理"] = ("物理", "#d9dde3"),
        ["Heat"] = ("灼热", "#ff7a3c"),
        ["Fire"] = ("灼热", "#ff7a3c"),
        ["灼热"] = ("灼热", "#ff7a3c"),
        ["Cryo"] = ("寒冷", "#63d8ff"),
        ["Cryst"] = ("寒冷", "#63d8ff"),
        ["寒冷"] = ("寒冷", "#63d8ff"),
        ["Electric"] = ("电磁", "#b98bff"),
        ["Pulse"] = ("电磁", "#b98bff"),
        ["电磁"] = ("电磁", "#b98bff"),
        ["Nature"] = ("自然", "#7ddc6a"),
        ["Natural"] = ("自然", "#7ddc6a"),
        ["自然"] = ("自然", "#7ddc6a"),
    };

    public static string NormalizeToken(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                var node = JsonNode.Parse(text);
                var content = node?["data"]?["content"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(content)) return content.Trim();
            }
            catch
            {
                // 不是 JSON，按原样处理
            }
        }

        return text;
    }

    public async Task<SklandSyncResult> SyncAsync(
        string serverKey,
        string rawToken,
        IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        var result = new SklandSyncResult();
        var cfg = string.Equals(serverKey, "global", StringComparison.OrdinalIgnoreCase) ? Global : Cn;
        var appToken = NormalizeToken(rawToken);
        if (string.IsNullOrWhiteSpace(appToken))
        {
            result.Message = "请先在设置里填入森空岛 App 的 token。";
            return result;
        }

        try
        {
            progress?.Report("正在向森空岛换取登录凭证…");
            var grant = await PostJsonAsync(cfg.GrantUrl, new JsonObject
            {
                ["appCode"] = cfg.AppCode,
                ["token"] = appToken,
                ["type"] = 0,
            }, token);

            if (!IsSuccess(grant, allowStatusField: true))
            {
                result.Message = "登录失败：" + ReadMessage(grant) + "（token 可能已过期，重新复制一个试试）";
                return result;
            }

            var grantCode = grant?["data"]?["code"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(grantCode))
            {
                result.Message = "登录失败：没有拿到授权码。";
                return result;
            }

            progress?.Report("正在获取森空岛访问凭证…");
            var credResp = await PostJsonAsync(cfg.CredUrl, new JsonObject
            {
                ["code"] = grantCode,
                ["kind"] = 1,
            }, token);

            if (!IsSuccess(credResp))
            {
                result.Message = "获取凭证失败：" + ReadMessage(credResp);
                return result;
            }

            var cred = credResp?["data"]?["cred"]?.GetValue<string>();
            var signToken = credResp?["data"]?["token"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(cred) || string.IsNullOrWhiteSpace(signToken))
            {
                result.Message = "获取凭证失败：凭证内容为空。";
                return result;
            }

            progress?.Report("正在读取绑定角色…");
            var binding = await GetSignedAsync(
                cfg,
                "/api/v1/game/player/binding",
                "",
                cred,
                signToken,
                skGameRole: null,
                language: "zh-cn",
                token);

            if (!IsSuccess(binding, allowStatusField: true))
            {
                result.Message = "读取绑定角色失败：" + ReadMessage(binding);
                return result;
            }

            var role = FindEndfieldRole(binding);
            if (role is null)
            {
                result.Message = "这个森空岛账号没有绑定终末地角色。";
                return result;
            }

            var roleId = role["roleId"]?.ToString() ?? "";
            var serverId = role["serverId"]?.ToString() ?? "";
            var nickname = role["nickname"]?.GetValue<string>() ?? "";
            var serverName = role["serverName"]?.GetValue<string>() ?? "";
            result.RoleName = string.Join(" · ", new[] { nickname, serverName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(result.RoleName)) result.RoleName = "终末地角色";

            if (string.IsNullOrWhiteSpace(roleId) || string.IsNullOrWhiteSpace(serverId))
            {
                result.Message = "读取角色信息失败：roleId/serverId 缺失。";
                return result;
            }

            progress?.Report("正在拉取角色数据…");
            var userId = await TryGetUserIdAsync(cfg, cred, signToken, token);
            var userId2 = string.IsNullOrWhiteSpace(userId) ? "" : userId;
            var skGameRole = $"3_{roleId}_{serverId}";

            JsonNode? card = null;
            foreach (var includeUser in new[] { true, false })
            {
                if (includeUser && string.IsNullOrWhiteSpace(userId2)) continue;
                var query = includeUser
                    ? $"roleId={Uri.EscapeDataString(roleId)}&serverId={Uri.EscapeDataString(serverId)}&userId={Uri.EscapeDataString(userId2)}"
                    : $"roleId={Uri.EscapeDataString(roleId)}&serverId={Uri.EscapeDataString(serverId)}";
                card = await GetSignedAsync(cfg, "/api/v1/game/endfield/card/detail", query, cred, signToken, skGameRole, "zh-cn", token);
                if (IsSuccess(card)) break;
            }

            if (!IsSuccess(card))
            {
                result.Message = "拉取角色卡片失败：" + ReadMessage(card);
                return result;
            }

            var chars = card?["data"]?["detail"]?["chars"] as JsonArray;
            if (chars is null || chars.Count == 0)
            {
                result.Message = "接口返回成功，但角色列表为空。";
                return result;
            }

            var iconDir = Store.SklandIconDir;
            Directory.CreateDirectory(iconDir);
            var index = 0;
            foreach (var item in chars)
            {
                token.ThrowIfCancellationRequested();
                index++;
                var charData = item?["charData"];
                if (charData is null) continue;
                var name = charData["name"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;

                var op = new Operator
                {
                    Id = item?["id"]?.ToString() ?? name,
                    Name = name,
                    Rarity = ReadInt(charData["rarity"]?["value"]),
                    Profession = MapProfession(charData["profession"]?["value"]?.ToString()),
                    Element = MapElement(charData["property"]?["value"]?.ToString()),
                };
                op.ElementColor = ElementMap.TryGetValue(op.Element, out var el) ? el.color : "#8b93a1";

                var iconUrl = charData["avatarSqUrl"]?.GetValue<string>()
                              ?? charData["avatarRtUrl"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(iconUrl))
                {
                    progress?.Report($"正在下载角色头像 {index}/{chars.Count} …");
                    var iconFile = await TryDownloadIconAsync(iconUrl, op.Id, iconDir, token);
                    if (iconFile is not null)
                    {
                        op.IconFile = iconFile;
                        op.Avatar = LoadBitmap(Path.Combine(iconDir, iconFile));
                    }
                }

                result.Operators.Add(op);
            }

            result.UserName = nickname;
            result.Ok = true;
            result.Message = $"同步完成：{result.Operators.Count} 名角色";
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Message = "同步已取消。";
            return result;
        }
        catch (Exception ex)
        {
            result.Message = "同步失败：" + ex.Message;
            return result;
        }
    }

    private static System.Windows.Media.ImageSource? LoadBitmap(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryDownloadIconAsync(string url, string id, string iconDir, CancellationToken token)
    {
        try
        {
            using var resp = await Http.GetAsync(url, token);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(token);
            var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            if (ext is ".webp" or ".gif" or ".avif") return null; // WPF 解码器不支持
            if (ext is not ".png" and not ".jpg" and not ".jpeg") ext = ".png";
            var safeId = new string(id.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-').ToArray());
            if (string.IsNullOrWhiteSpace(safeId)) safeId = "skland_" + Guid.NewGuid().ToString("N")[..8];
            var fileName = safeId + ext;
            await File.WriteAllBytesAsync(Path.Combine(iconDir, fileName), bytes, token);
            return fileName;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryGetUserIdAsync(ServerConfig cfg, string cred, string signToken, CancellationToken token)
    {
        try
        {
            var me = await GetSignedAsync(cfg, "/web/v1/wiki/me", "", cred, signToken, null, "zh-cn", token);
            if (!IsSuccess(me)) return null;
            return me?["data"]?["user"]?["userId"]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode? FindEndfieldRole(JsonNode? binding)
    {
        var list = binding?["data"]?["list"] as JsonArray;
        if (list is null) return null;
        foreach (var app in list)
        {
            if (!string.Equals(app?["appCode"]?.GetValue<string>(), "endfield", StringComparison.OrdinalIgnoreCase)) continue;
            var bindings = app?["bindingList"] as JsonArray;
            if (bindings is null || bindings.Count == 0) continue;
            var first = bindings[0];
            return first?["defaultRole"] ?? (first?["roles"] as JsonArray)?.FirstOrDefault();
        }

        return null;
    }

    private static bool IsSuccess(JsonNode? node, bool allowStatusField = false)
    {
        if (node is null) return false;
        if (allowStatusField && node["status"] is not null)
        {
            var status = node["status"]?.ToString();
            if (status != "0") return false;
        }

        return node["code"]?.ToString() == "0";
    }

    private static string ReadMessage(JsonNode? node)
    {
        if (node is null) return "没有收到响应";
        var msg = node["message"]?.GetValue<string>() ?? node["msg"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(msg)) return msg;
        var status = node["status"]?.ToString();
        return string.IsNullOrWhiteSpace(status) ? "未知错误" : "状态码 " + status;
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is null) return 0;
        if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        return 0;
    }

    private static string MapProfession(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return ProfessionMap.TryGetValue(raw.Trim(), out var mapped) ? mapped : raw.Trim();
    }

    private static string MapElement(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return ElementMap.TryGetValue(raw.Trim(), out var mapped) ? mapped.name : raw.Trim();
    }

    private static async Task<JsonNode?> PostJsonAsync(string url, JsonObject body, CancellationToken token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        req.Headers.TryAddWithoutValidation("User-Agent", "Skland/1.0.0 (com.skland.grass; Android; SDK_INT 34)");
        using var resp = await Http.SendAsync(req, token);
        var text = await resp.Content.ReadAsStringAsync(token);
        try
        {
            return JsonNode.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<JsonNode?> GetSignedAsync(
        ServerConfig cfg,
        string path,
        string query,
        string cred,
        string signToken,
        string? skGameRole,
        string language,
        CancellationToken token)
    {
        var (sign, timestamp) = GenerateSign(signToken, path, query);
        var url = cfg.ApiHost + path + (string.IsNullOrEmpty(query) ? "" : "?" + query);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("cred", cred);
        req.Headers.TryAddWithoutValidation("sign", sign);
        req.Headers.TryAddWithoutValidation("timestamp", timestamp);
        req.Headers.TryAddWithoutValidation("platform", "3");
        req.Headers.TryAddWithoutValidation("vname", "1.0.0");
        req.Headers.TryAddWithoutValidation("sk-language", language);
        if (!string.IsNullOrWhiteSpace(skGameRole))
            req.Headers.TryAddWithoutValidation("sk-game-role", skGameRole);
        req.Headers.TryAddWithoutValidation("Origin", cfg.Origin);
        req.Headers.TryAddWithoutValidation("Referer", cfg.Origin + "/");
        req.Headers.TryAddWithoutValidation("x-requested-with", "com.gryphline.skport");
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");

        using var resp = await Http.SendAsync(req, token);
        var text = await resp.Content.ReadAsStringAsync(token);
        try
        {
            return JsonNode.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 森空岛签名：HMAC-SHA256(signToken, path + body + timestamp + headerJson) -> hex -> md5。
    /// </summary>
    private static (string sign, string timestamp) GenerateSign(string token, string path, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var header = "{\"platform\":\"3\",\"timestamp\":\"" + timestamp + "\",\"dId\":\"\",\"vName\":\"1.0.0\"}";
        var payload = path + body + timestamp + header;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token));
        var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        using var md5 = MD5.Create();
        var sign = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(hex))).ToLowerInvariant();
        return (sign, timestamp);
    }
}
