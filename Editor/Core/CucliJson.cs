using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace CustomUnityCli.Editor
{
    /// <summary>
    /// One JSON policy for the whole worker: camelCase on the wire, indented for human-readable
    /// state files, UTF-8 without BOM so that the PowerShell CLI and <c>git diff</c> stay happy.
    /// </summary>
    public static class CucliJson
    {
        public static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateParseHandling = DateParseHandling.None
        };

        public static readonly JsonSerializerSettings CompactSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            DateParseHandling = DateParseHandling.None
        };

        public static string Serialize(object value)
        {
            return JsonConvert.SerializeObject(value, Settings);
        }

        public static string SerializeCompact(object value)
        {
            return JsonConvert.SerializeObject(value, CompactSettings);
        }

        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        public static JObject ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            JToken token = JToken.Parse(json);
            return token as JObject ?? new JObject();
        }

        /// <summary>Never throws: a malformed payload becomes an empty object plus a warning.</summary>
        public static JObject TryParseObject(string json)
        {
            try
            {
                return ParseObject(json);
            }
            catch (JsonException)
            {
                return new JObject();
            }
        }

        public static JToken ToToken(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is JToken token) return token;
            return JToken.FromObject(value, JsonSerializer.Create(CompactSettings));
        }
    }
}
