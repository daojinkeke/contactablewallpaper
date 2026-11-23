using System;
using System.IO;
using System.Text;
using Lively.Common;
using Lively.Models.Ai;
using Newtonsoft.Json;

namespace Lively.Ai
{
    /// <summary>
    /// 会话式修改的磁盘存储：每个会话一个目录，包含 session.json 与 v1.html、v2.html…
    /// </summary>
    public sealed class WallpaperSessionStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        public WallpaperSessionStore(string rootDir = null)
        {
            RootDir = string.IsNullOrWhiteSpace(rootDir) ? Constants.CommonPaths.ProjectsDir : rootDir;
        }

        public string RootDir { get; }

        public static bool IsValidSessionId(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 40)
                return false;

            foreach (var c in sessionId)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                         (c >= '0' && c <= '9') || c == '-';
                if (!ok)
                    return false;
            }
            return true;
        }

        public WallpaperSession CreateSession(string prompt, string sessionId = null, bool eco = false)
        {
            var id = string.IsNullOrWhiteSpace(sessionId)
                ? "wp-" + Guid.NewGuid().ToString("N").Substring(0, 12)
                : sessionId.Trim();
            if (!IsValidSessionId(id))
                throw new ArgumentException("会话标识只能包含字母、数字与 '-'，最长 40 个字符。", nameof(sessionId));

            var now = DateTimeOffset.UtcNow.ToString("o");
            var session = new WallpaperSession
            {
                SessionId = id,
                Prompt = prompt ?? string.Empty,
                Eco = eco,
                CreatedAt = now,
                UpdatedAt = now
            };
            Save(session);
            return session;
        }

        public bool TryLoad(string sessionId, out WallpaperSession session)
        {
            session = null;
            if (!IsValidSessionId(sessionId))
                return false;

            var path = Path.Combine(RootDir, sessionId, "session.json");
            if (!File.Exists(path))
                return false;

            try
            {
                session = JsonConvert.DeserializeObject<WallpaperSession>(File.ReadAllText(path), JsonSettings);
            }
            catch (Exception)
            {
                return false;
            }

            if (session == null || string.IsNullOrWhiteSpace(session.SessionId))
                return false;

            session.Versions ??= new System.Collections.Generic.List<WallpaperSessionVersion>();
            return true;
        }

        public string GetVersionPath(string sessionId, int version)
        {
            return Path.Combine(RootDir, sessionId, "v" + version + ".html");
        }

        public string ReadVersionHtml(string sessionId, int version)
        {
            var path = GetVersionPath(sessionId, version);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>把一版已通过校验的 HTML 落盘并写入会话历史。</summary>
        public WallpaperSessionVersion AppendVersion(
            WallpaperSession session,
            string html,
            string instruction,
            string summary,
            string plan,
            double? score,
            long elapsedMs,
            int promptTokens,
            int completionTokens,
            int totalTokens)
        {
            var version = (session.Versions.Count == 0 ? 0 : session.Versions[^1].Version) + 1;
            var fileName = "v" + version + ".html";
            var directory = Path.Combine(RootDir, session.SessionId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), html ?? string.Empty, new UTF8Encoding(false));

            var entry = new WallpaperSessionVersion
            {
                Version = version,
                FileName = fileName,
                Instruction = instruction ?? string.Empty,
                Summary = summary ?? string.Empty,
                Plan = plan ?? string.Empty,
                Score = score,
                ElapsedMs = elapsedMs,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                CreatedAt = DateTimeOffset.UtcNow.ToString("o")
            };

            session.Versions.Add(entry);
            session.UpdatedAt = entry.CreatedAt;
            Save(session);
            return entry;
        }

        public void Save(WallpaperSession session)
        {
            var directory = Path.Combine(RootDir, session.SessionId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "session.json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(session, JsonSettings), new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
    }
}
