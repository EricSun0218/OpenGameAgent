using Newtonsoft.Json;

namespace OpenGameAgent.Unity.Protocol
{
    public static class RuntimeProtocolJson
    {
        public static string Serialize<T>(T value)
        {
            return JsonConvert.SerializeObject(value, Converter.Settings);
        }

        public static T Deserialize<T>(string json)
        {
            T value = JsonConvert.DeserializeObject<T>(json, Converter.Settings);
            if (value == null)
            {
                throw new JsonSerializationException("Runtime response contained JSON null.");
            }

            return value;
        }
    }
}
