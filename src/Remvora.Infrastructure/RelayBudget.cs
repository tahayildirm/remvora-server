using System.Text.Json;
using Remvora.Domain;
namespace Remvora.Infrastructure;

/// <summary>Limits relay traffic separately from low-volume signaling. Ordered TLS transport requires increasing sequence numbers.</summary>
public sealed class RelayBudget
{
    private long sequence = -1;
    private long window = Environment.TickCount64;
    private int count;
    private int bytes;
    public void Check(JsonElement payload, bool fromBrowser)
    {
        if (!payload.TryGetProperty("sequence", out var seq) || !seq.TryGetInt64(out var value) || value <= sequence ||
            !payload.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            throw new DomainException("RELAY_INVALID");
        var name = channel.GetString();
        if (name is not ("input" or "files" or "video" or "audio") || (fromBrowser && name is "video" or "audio"))
            throw new DomainException("RELAY_INVALID");
        var length = data.GetString()!.Length;
        if (length > 44000 || !payload.TryGetProperty("part", out var part) || !part.TryGetInt32(out var index) || index < 0 || index >= 64 ||
            !payload.TryGetProperty("last", out var last) || last.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            (fromBrowser && (index != 0 || !last.GetBoolean()))) throw new DomainException("RELAY_INVALID");
        if (Environment.TickCount64 - window >= 1000) { window = Environment.TickCount64; count = 0; bytes = 0; }
        sequence = value; bytes += length;
        if (++count > 300 || bytes > 4 * 1024 * 1024) throw new DomainException("RELAY_RATE_LIMIT");
    }
}
