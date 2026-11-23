using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Lively.Common;
using Lively.Models.Ai;
using Newtonsoft.Json;

namespace Lively.Ai
{
    /// <summary>
    /// AiSettings.json 读写。密钥使用 DPAPI(CurrentUser) 加密，禁止明文落盘（07 契约 §5.4 / §11）。
    /// 环境变量 LIVELY_AI_BASE_URL / LIVELY_AI_API_KEY / LIVELY_AI_MODEL 可作为临时覆盖，便于联调。
    /// </summary>
    public class AiSettingsStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            // 未知字段忽略，缺失字段用默认值，保证旧配置可读。
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly string settingsPath;

        public AiSettingsStore(string settingsPath = null)
        {
            this.settingsPath = string.IsNullOrWhiteSpace(settingsPath)
                ? Constants.CommonPaths.AiSettingsPath
                : settingsPath;
        }

        public string SettingsPath => settingsPath;

        public AiSettings Load(bool applyEnvironmentOverrides = true)
        {
            AiSettings settings = null;
            if (File.Exists(settingsPath))
            {
                try
                {
                    settings = JsonConvert.DeserializeObject<AiSettings>(File.ReadAllText(settingsPath, Encoding.UTF8), JsonSettings);
                }
                catch (Exception)
                {
                    settings = null;
                }
            }

            if (settings == null)
                settings = new AiSettings();

            if (settings.TimeoutSeconds <= 0)
                settings.TimeoutSeconds = 120;
            if (settings.MaxRepairAttempts < 0 || settings.MaxRepairAttempts > 2)
                settings.MaxRepairAttempts = 2;

            var baseUrlOverride = applyEnvironmentOverrides
                ? Environment.GetEnvironmentVariable("LIVELY_AI_BASE_URL")
                : null;
            if (!string.IsNullOrWhiteSpace(baseUrlOverride))
                settings.BaseUrl = baseUrlOverride;
            var modelOverride = applyEnvironmentOverrides
                ? Environment.GetEnvironmentVariable("LIVELY_AI_MODEL")
                : null;
            if (!string.IsNullOrWhiteSpace(modelOverride))
                settings.Model = modelOverride;

            return settings;
        }

        public void Save(AiSettings settings)
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonConvert.SerializeObject(settings, Formatting.Indented, JsonSettings);
            var tempPath = settingsPath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            if (File.Exists(settingsPath))
                File.Replace(tempPath, settingsPath, null);
            else
                File.Move(tempPath, settingsPath);
        }

        /// <summary>
        /// 加密并写入密钥。
        /// </summary>
        public void SetApiKey(AiSettings settings, string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                settings.ApiKeyProtected = string.Empty;
                return;
            }

            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), null, DataProtectionScope.CurrentUser);
            settings.ApiKeyProtected = Convert.ToBase64String(cipher);
        }

        /// <summary>
        /// 读取密钥；环境变量优先，其次 DPAPI 密文。失败返回空字符串。
        /// </summary>
        public string GetApiKey(AiSettings settings)
        {
            var envKey = Environment.GetEnvironmentVariable("LIVELY_AI_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
                return envKey.Trim();

            if (settings == null || string.IsNullOrEmpty(settings.ApiKeyProtected))
                return string.Empty;

            try
            {
                var cipher = Convert.FromBase64String(settings.ApiKeyProtected);
                var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception)
            {
                // 换机器/换用户后 DPAPI 无法解密，按未配置处理。
                return string.Empty;
            }
        }

        public bool IsConfigured(AiSettings settings) =>
            settings != null &&
            !string.IsNullOrWhiteSpace(settings.BaseUrl) &&
            !string.IsNullOrWhiteSpace(settings.Model) &&
            !string.IsNullOrEmpty(GetApiKey(settings));
    }
}
