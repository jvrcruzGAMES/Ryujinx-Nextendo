using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ryujinx.Appliance.Client
{
    public class IpcRequest
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public JsonElement? Params { get; set; }
    }

    public class IpcResponse
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("result")]
        public JsonElement? Result { get; set; }

        [JsonPropertyName("error")]
        public IpcError Error { get; set; }
    }

    public class IpcError
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("details")]
        public JsonElement? Details { get; set; }
    }

    public class IpcEvent
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("event")]
        public string Event { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public JsonElement? Data { get; set; }
    }
}
