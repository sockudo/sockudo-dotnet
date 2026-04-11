using Sodium;
using System.Text.Json;
using System.Text;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;
using VCDiff.Shared;
using Xunit;

namespace Sockudo.Client.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void EncodesWebSocketUrlWithV2FormatQuery()
    {
        var client = new SockudoClient(
            "app-key",
            new SockudoOptions(
                Cluster: "local",
                ForceTls: false,
                EnabledTransports: new[] { SockudoTransport.Ws },
                WsHost: "ws.example.com",
                WsPort: 6001,
                WssPort: 6002,
                WireFormat: SockudoWireFormat.MessagePack
            )
        );

        var url = client.SocketUrl(SockudoTransport.Ws);

        Assert.Contains("protocol=2", url);
        Assert.Contains("format=messagepack", url);
    }

    [Fact]
    public void RoundTripsMessagePack()
    {
        var payload = (byte[])ProtocolCodec.EncodeEnvelope(
            new Dictionary<string, object?>
            {
                ["event"] = "sockudo:test",
                ["channel"] = "chat:room-1",
                ["data"] = new Dictionary<string, object?> { ["hello"] = "world", ["count"] = 3 },
                ["stream_id"] = "stream-1",
                ["serial"] = 7,
                ["__delta_seq"] = 7,
                ["__conflation_key"] = "room",
            },
            SockudoWireFormat.MessagePack
        );

        var decoded = ProtocolCodec.DecodeEvent(payload, SockudoWireFormat.MessagePack);

        Assert.Equal("sockudo:test", decoded.Event);
        Assert.Equal("chat:room-1", decoded.Channel);
        var data = Assert.IsType<Dictionary<string, object?>>(decoded.Data);
        Assert.Equal("world", data["hello"]);
        Assert.Equal(3L, data["count"]);
        Assert.Equal("stream-1", decoded.StreamId);
        Assert.Equal(7, decoded.Serial);
        Assert.Equal(7, decoded.Sequence);
        Assert.Equal("room", decoded.ConflationKey);
    }

    [Fact]
    public void RoundTripsProtobuf()
    {
        var payload = (byte[])ProtocolCodec.EncodeEnvelope(
            new Dictionary<string, object?>
            {
                ["event"] = "sockudo:test",
                ["channel"] = "chat:room-1",
                ["data"] = new Dictionary<string, object?> { ["hello"] = "world" },
                ["stream_id"] = "stream-2",
                ["serial"] = 9,
                ["__delta_seq"] = 11,
                ["__conflation_key"] = "btc",
                ["extras"] = new Dictionary<string, object?>
                {
                    ["headers"] = new Dictionary<string, object> { ["region"] = "eu", ["ttl"] = 5, ["replay"] = true },
                    ["echo"] = false,
                },
            },
            SockudoWireFormat.Protobuf
        );

        var decoded = ProtocolCodec.DecodeEvent(payload, SockudoWireFormat.Protobuf);

        Assert.Equal("sockudo:test", decoded.Event);
        Assert.Equal("chat:room-1", decoded.Channel);
        var data = Assert.IsType<Dictionary<string, object?>>(decoded.Data);
        Assert.Equal("world", data["hello"]);
        Assert.Equal("stream-2", decoded.StreamId);
        Assert.Equal(9, decoded.Serial);
        Assert.Equal(11, decoded.Sequence);
        Assert.Equal("btc", decoded.ConflationKey);
        Assert.NotNull(decoded.Extras);
        Assert.Equal("eu", decoded.Extras!.Headers!["region"]);
        Assert.Equal(5.0, decoded.Extras.Headers["ttl"]);
        Assert.Equal(true, decoded.Extras.Headers["replay"]);
        Assert.False(decoded.Extras.Echo ?? true);
    }

    [Fact]
    public void AppliesInsertOnlyFossilDelta()
    {
        Assert.Equal("hello", Encoding.UTF8.GetString(FossilDelta.Apply([], Encoding.UTF8.GetBytes("5\n5:hello3NPMmh;"))));
    }

    [Fact]
    public async Task DecryptsEncryptedChannelPayload()
    {
        var secret = SecretBox.GenerateKey();
        var client = new SockudoClient(
            "app-key",
            new SockudoOptions(
                Cluster: "local",
                ForceTls: false,
                ChannelAuthorization: new ChannelAuthorizationOptions(
                    CustomHandler: _ => Task.FromResult(
                        new ChannelAuthorizationData(
                            "token",
                            SharedSecret: Convert.ToBase64String(secret)
                        )
                    )
                )
            )
        );

        var channel = Assert.IsType<EncryptedChannel>(client.Subscribe("private-encrypted-room"));
        await channel.AuthorizeAsync("123.456");

        var nonce = SecretBox.GenerateNonce();
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["hello"] = "world" });
        var ciphertext = SecretBox.Create(Encoding.UTF8.GetBytes(payload), nonce, secret);
        var decrypted = channel.Decrypt(new Dictionary<string, object?>
        {
            ["ciphertext"] = Convert.ToBase64String(ciphertext),
            ["nonce"] = Convert.ToBase64String(nonce),
        });

        var decoded = Assert.IsType<Dictionary<string, object?>>(decrypted);
        Assert.Equal("world", decoded["hello"]);
    }

    [Fact]
    public void AppliesXdelta3ViaVcdiffDecoder()
    {
        var original = "{\"data\":{\"price\":100,\"volume\":5}}";
        var updated = "{\"data\":{\"price\":101,\"volume\":7}}";

        byte[] deltaBytes;
        using (var source = new MemoryStream(Encoding.UTF8.GetBytes(original)))
        using (var target = new MemoryStream(Encoding.UTF8.GetBytes(updated)))
        using (var output = new MemoryStream())
        using (var encoder = new VcEncoder(source, target, output))
        {
            Assert.Equal(VCDiffResult.SUCCESS, encoder.Encode(false, ChecksumFormat.Xdelta3));
            deltaBytes = output.ToArray();
        }

        using var sourceStream = new MemoryStream(Encoding.UTF8.GetBytes(original));
        using var deltaStream = new MemoryStream(deltaBytes);
        using var result = new MemoryStream();
        using var decoder = new VcDecoder(sourceStream, deltaStream, result);
        decoder.Decode(out _);

        Assert.Equal(updated, Encoding.UTF8.GetString(result.ToArray()));
    }

    [Fact]
    public void PresenceHistoryParamsPreferNormalizedTimeBounds()
    {
        var payload = new PresenceHistoryParams(
            Direction: "newest_first",
            Limit: 50,
            Start: 1000,
            End: 2000
        ).ToPayload();

        Assert.Equal("newest_first", payload["direction"]);
        Assert.Equal(50, payload["limit"]);
        Assert.Equal(1000L, payload["start_time_ms"]);
        Assert.Equal(2000L, payload["end_time_ms"]);
        Assert.DoesNotContain("start", payload.Keys);
        Assert.DoesNotContain("end", payload.Keys);
    }

    [Fact]
    public async Task PresenceHistoryPageNextUsesNextCursor()
    {
        string? capturedCursor = null;
        var page = new PresenceHistoryPage(
            Array.Empty<PresenceHistoryItem>(),
            "newest_first",
            50,
            true,
            "cursor-2",
            new PresenceHistoryBounds(null, null, null, null),
            new PresenceHistoryContinuity(null, null, null, null, null, 0, 0, false, true, false),
            cursor =>
            {
                capturedCursor = cursor;
                return Task.FromResult(
                    new PresenceHistoryPage(
                        Array.Empty<PresenceHistoryItem>(),
                        "newest_first",
                        50,
                        false,
                        null,
                        new PresenceHistoryBounds(null, null, null, null),
                        new PresenceHistoryContinuity(null, null, null, null, null, 0, 0, false, true, false)
                    )
                );
            }
        );

        Assert.True(page.HasNext());
        await page.NextAsync();
        Assert.Equal("cursor-2", capturedCursor);
    }
}
