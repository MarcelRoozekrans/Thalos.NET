using System.Text.Json.Serialization;

namespace Thalos.Channels.Telegram;

/// <summary>Source-generated serialization for the Bot API payloads this package sends and receives.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TelegramResponse<TelegramUpdate[]>), TypeInfoPropertyName = "TelegramResponseUpdateArray")]
[JsonSerializable(typeof(TelegramResponse<TelegramMessage>), TypeInfoPropertyName = "TelegramResponseMessage")]
[JsonSerializable(typeof(TelegramResponse<bool>), TypeInfoPropertyName = "TelegramResponseBool")]
internal sealed partial class TelegramJsonContext : JsonSerializerContext;
