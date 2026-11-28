using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lively.Player.WebView2.Host
{
    /// <summary>window.contactableEnv.time 的宿主快照。</summary>
    public sealed class HostTimeSnapshot
    {
        [JsonProperty("iso")]
        public string Iso { get; set; }

        [JsonProperty("hour")]
        public int Hour { get; set; }

        [JsonProperty("minute")]
        public int Minute { get; set; }

        [JsonProperty("dayOfWeek")]
        public int DayOfWeek { get; set; }

        [JsonProperty("isDay")]
        public bool IsDay { get; set; }

        [JsonProperty("timezoneOffsetMinutes")]
        public int TimezoneOffsetMinutes { get; set; }
    }

    /// <summary>window.contactableEnv.weather 的宿主快照；取数失败时整个对象为 null。</summary>
    public sealed class HostWeatherSnapshot
    {
        [JsonProperty("condition")]
        public string Condition { get; set; }

        [JsonProperty("conditionCode")]
        public int ConditionCode { get; set; }

        [JsonProperty("temperatureC")]
        public double TemperatureC { get; set; }

        [JsonProperty("windKph")]
        public double WindKph { get; set; }

        [JsonProperty("humidity")]
        public int Humidity { get; set; }

        [JsonProperty("isDay")]
        public bool IsDay { get; set; }

        [JsonProperty("updatedAt")]
        public string UpdatedAt { get; set; }
    }

    /// <summary>
    /// 宿主侧时间/天气取数与缓存：页面自己不发任何请求，只在脚本执行前读到 window.contactableEnv。
    /// 天气为免密钥方案（ipwho.is 定位 + open-meteo 实时天气），失败一律降级为 null，不阻塞页面渲染。
    /// </summary>
    public sealed class HostEnvironmentService
    {
        private const string CacheDirectoryName = "Lively Wallpaper";
        private const string CacheFileName = "HostWeatherCache.json";

        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

        private static readonly HttpClient HttpClientInstance = new HttpClient { Timeout = RequestTimeout };

        private readonly Action<string> log;

        public HostEnvironmentService(Action<string> log)
        {
            this.log = log;
        }

        /// <summary>%LOCALAPPDATA%\Lively Wallpaper\HostWeatherCache.json</summary>
        public static string CachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CacheDirectoryName,
            CacheFileName);

        /// <summary>
        /// 页面脚本执行前注入的初始化脚本：时间立即生成，天气使用缓存（可能已过期）或 null。
        /// </summary>
        public string BuildInitialScript(bool allowExternalNetwork)
        {
            var cached = TryReadCachedWeather();
            var payload = new JObject
            {
                ["time"] = JObject.FromObject(CreateTimeSnapshot()),
                ["weather"] = cached == null ? (JToken)JValue.CreateNull() : JObject.FromObject(cached),
                ["allowExternalNetwork"] = allowExternalNetwork
            };

            return "(function(){try{window.contactableEnv=" + payload.ToString(Formatting.None) +
                   ";}catch(e){}})();";
        }

        /// <summary>
        /// 导航完成后刷新天气：缓存仍新鲜时返回 null（无需更新），否则取数并返回更新脚本让页面就地刷新。
        /// </summary>
        public async Task<string> RefreshScriptAsync()
        {
            if (IsCacheFresh())
                return null;

            HostWeatherSnapshot weather;
            try
            {
                weather = await FetchWeatherAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.Invoke("Host weather fetch failed: " + ex.Message);
                return null;
            }

            if (weather == null)
                return null;

            WriteCache(weather);
            var payload = JObject.FromObject(weather).ToString(Formatting.None);
            return "(function(){try{" +
                   "var env=window.contactableEnv||(window.contactableEnv={});" +
                   "env.weather=" + payload + ";" +
                   "window.dispatchEvent(new CustomEvent('contactable:env',{detail:env}));" +
                   "}catch(e){}})();";
        }

        private static HostTimeSnapshot CreateTimeSnapshot()
        {
            var now = DateTime.Now;
            var offset = TimeZoneInfo.Local.GetUtcOffset(now);
            return new HostTimeSnapshot
            {
                Iso = now.ToString("o", CultureInfo.InvariantCulture),
                Hour = now.Hour,
                Minute = now.Minute,
                DayOfWeek = (int)now.DayOfWeek,
                // 天气未知时按 06:00–18:00 判定白天，保证首帧就有可用的明暗判断。
                IsDay = now.Hour >= 6 && now.Hour < 18,
                TimezoneOffsetMinutes = (int)offset.TotalMinutes
            };
        }

        private async Task<HostWeatherSnapshot> FetchWeatherAsync()
        {
            var coordinates = await GetCoordinatesAsync().ConfigureAwait(false);
            if (coordinates == null)
                return null;

            var url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
                "&current=temperature_2m,weather_code,wind_speed_10m,relative_humidity_2m,is_day&timezone=auto",
                coordinates.Value.Latitude, coordinates.Value.Longitude);

            var body = await GetStringAsync(url).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            var current = JObject.Parse(body)["current"] as JObject;
            if (current == null)
                return null;

            var codeToken = current["weather_code"];
            var temperatureToken = current["temperature_2m"];
            if (codeToken == null || temperatureToken == null)
                return null;

            var code = codeToken.Value<int>();
            return new HostWeatherSnapshot
            {
                Condition = MapCondition(code),
                ConditionCode = code,
                TemperatureC = Math.Round(temperatureToken.Value<double>(), 1),
                WindKph = current["wind_speed_10m"] != null ? Math.Round(current["wind_speed_10m"].Value<double>(), 1) : 0,
                Humidity = current["relative_humidity_2m"] != null ? current["relative_humidity_2m"].Value<int>() : 0,
                IsDay = current["is_day"] != null && current["is_day"].Value<int>() == 1,
                UpdatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)
            };
        }

        private async Task<(double Latitude, double Longitude)?> GetCoordinatesAsync()
        {
            var body = await GetStringAsync("https://ipwho.is/").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            var root = JObject.Parse(body);
            if (root["success"] != null && !root["success"].Value<bool>())
                return null;

            var latitude = root["latitude"];
            var longitude = root["longitude"];
            if (latitude == null || longitude == null)
                return null;

            return (latitude.Value<double>(), longitude.Value<double>());
        }

        private async Task<string> GetStringAsync(string url)
        {
            using (var response = await HttpClientInstance.GetAsync(url).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    log?.Invoke($"Host weather request failed: {(int)response.StatusCode} {url}");
                    return null;
                }
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        /// <summary>WMO weather_code → 简短 condition（保留原始 code 在 conditionCode）。</summary>
        private static string MapCondition(int code)
        {
            if (code == 0)
                return "clear";
            if (code == 1 || code == 2)
                return "partly cloudy";
            if (code == 3)
                return "cloudy";
            if (code == 45 || code == 48)
                return "fog";
            if (code >= 51 && code <= 57)
                return "drizzle";
            if (code >= 61 && code <= 67)
                return "rain";
            if (code >= 71 && code <= 77)
                return "snow";
            if (code >= 80 && code <= 82)
                return "rain";
            if (code == 85 || code == 86)
                return "snow";
            if (code >= 95 && code <= 99)
                return "thunder";
            return "unknown";
        }

        private HostWeatherSnapshot TryReadCachedWeather()
        {
            try
            {
                var path = CachePath;
                if (!File.Exists(path))
                    return null;

                var weather = JsonConvert.DeserializeObject<HostWeatherSnapshot>(File.ReadAllText(path));
                if (weather == null || string.IsNullOrWhiteSpace(weather.UpdatedAt))
                    return null;

                return weather;
            }
            catch (Exception ex)
            {
                log?.Invoke("Host weather cache read failed: " + ex.Message);
                return null;
            }
        }

        private bool IsCacheFresh()
        {
            try
            {
                var path = CachePath;
                return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < CacheLifetime;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void WriteCache(HostWeatherSnapshot weather)
        {
            try
            {
                var path = CachePath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(path, JsonConvert.SerializeObject(weather, Formatting.None));
            }
            catch (Exception ex)
            {
                log?.Invoke("Host weather cache write failed: " + ex.Message);
            }
        }
    }
}
