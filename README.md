# Sockudo.Client

Sockudo C# client SDK for .NET.

## Features

- Protocol V2 by default, with V1 compatibility
- Public, private, presence, and encrypted channel types
- Tag filters and per-subscription event filters
- Connection recovery serial tracking
- Message deduplication
- User sign-in and watchlist event handling
- JSON, MessagePack, and Protobuf codecs
- Fossil and Xdelta3/VCDIFF delta support
- Encrypted channel shared-secret payload decryption

## Build

```bash
dotnet test client-sdks/sockudo-csharp/tests/Sockudo.Client.Tests/Sockudo.Client.Tests.csproj
```
