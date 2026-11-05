using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Nexus.Service.Peripherals.JungleLeopard;

internal sealed record D215DeviceInfo(string Model, string Firmware, int Brightness, bool DisplayOn)
{
    private const string SupportedModel = "D215-NOR-JD9365-6.67inch-hor";
    private const string SupportedFirmware = "3.4";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowDuplicateProperties = false, MaxDepth = 16 };

    internal bool IsSupported => Model == SupportedModel && Firmware == SupportedFirmware;

    internal static bool TryParse(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out D215DeviceInfo? info)
    {
        info = null;
        if (payload.IsEmpty || payload.Length > D215Protocol.MaximumControlPayloadBytes)
            return false;

        try
        {
            // Validate bytes in every string, including fields the driver does not read.
            StrictUtf8.GetCharCount(payload);
            using var json = JsonDocument.Parse(payload.ToArray(), JsonOptions);
            return TryReadInfo(json.RootElement, out info);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            // Decoding a JSON string can also fail on an unpaired escaped surrogate.
            return false;
        }
    }

    private static bool TryReadInfo(JsonElement root, [NotNullWhen(true)] out D215DeviceInfo? info)
    {
        info = null;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("cmd", out var command)
            || command.ValueKind != JsonValueKind.String || !command.ValueEquals("info")
            || !HasSuccessStatus(root)
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !TryReadText(data, "model", out var model) || !TryReadText(data, "version", out var firmware)
            || !data.TryGetProperty("brightness", out var brightness)
            || brightness.ValueKind != JsonValueKind.Number || !brightness.TryGetInt32(out var level)
            || level is < 0 or > 100
            || !data.TryGetProperty("diplay_on", out var displayOn)
            || displayOn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        info = new D215DeviceInfo(model, firmware, level, displayOn.GetBoolean());
        return true;
    }

    private static bool HasSuccessStatus(JsonElement root) =>
        !root.TryGetProperty("status", out var status)
        || (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code == 200);

    private static bool TryReadText(JsonElement data, string name, [NotNullWhen(true)] out string? value)
    {
        value = data.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
        return !string.IsNullOrWhiteSpace(value);
    }
}
