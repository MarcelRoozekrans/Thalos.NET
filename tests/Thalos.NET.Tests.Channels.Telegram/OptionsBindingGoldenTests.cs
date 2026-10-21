using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Thalos.Channels.Telegram;

namespace Thalos.Tests.Channels.Telegram;

/// <summary>
/// <see cref="TelegramOptions"/> as <c>AddTelegramChannel(IConfiguration)</c> binds them, captured before the bind moves
/// to the configuration binder source generator. The lists start with an entry added in code, so the dump shows whether
/// configuration appends to them or replaces them. The dump is written by the reflection-based serializer in test code,
/// which stays allowed as an oracle. A characterization test: it passes on today's code.
/// </summary>
public sealed class OptionsBindingGoldenTests
{
    private static readonly JsonSerializerOptions DumpOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Red: rename TelegramOptions.SectionName to "Thalos:Channels:TelegramX"; nothing binds and the dump shows the
    // in-code entries only.
    [Fact]
    public void Telegram_options_bind_as_before()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Thalos:Channels:Telegram:Enabled"] = "true",
            ["Thalos:Channels:Telegram:BotToken"] = "123:abc",
            ["Thalos:Channels:Telegram:AllowedUserIds:0"] = "42",
            ["Thalos:Channels:Telegram:AllowedUserIds:1"] = "9000000000",
            ["Thalos:Channels:Telegram:PrincipalId"] = "telegram-owner",
            ["Thalos:Channels:Telegram:Roles:0"] = "admin",
            ["Thalos:Channels:Telegram:PollTimeoutSeconds"] = "30",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<TelegramOptions>(o =>
        {
            o.AllowedUserIds.Add(7);
            o.Roles.Add("in-code");
        });
        services.AddThalos(t => t.AddTelegramChannel(configuration));
        using var provider = services.BuildServiceProvider();

        var dump = JsonSerializer.Serialize(provider.GetRequiredService<IOptions<TelegramOptions>>().Value, DumpOptions);

        dump.Should().Be("""
            {
              "Enabled": true,
              "BotToken": "123:abc",
              "AllowedUserIds": [
                7,
                42,
                9000000000
              ],
              "PrincipalId": "telegram-owner",
              "Roles": [
                "in-code",
                "admin"
              ],
              "PollTimeoutSeconds": 30
            }
            """);
    }
}
