# pYove Socks4/Socks5 for IWebProxy

This project is suitable for all WebProxy, HTTP Client, WebSocket and for others.

[![NuGet version](https://badge.fury.io/nu/pYove.svg)](https://badge.fury.io/nu/pYove)

Nuget: https://www.nuget.org/packages/pYove/1.0.0

```sh
Install-Package pYove -Version 1.0.0
```
```sh
dotnet add package pYove --version 1.0.0
```

# Example

```csharp

new ProxyClient("138.68.161.60", 1080, ProxyType.Socks5);

```

### WebSocket

```csharp

ClientWebSocket WebSocket = new ClientWebSocket();

WebSocket.Options.Proxy = new ProxyClient("138.68.161.60", 1080, ProxyType.Socks4);

await WebSocket.ConnectAsync(new Uri("wss://echo.websocket.org"), TokenSource.Token);

```

### HttpClient

```csharp

HttpClientHandler Handler = new HttpClientHandler()
{
    Proxy = new ProxyClient("138.68.161.60", 1080, ProxyType.Socks4)
};

using (HttpClient Client = new HttpClient(Handler))
{
    var Response = await Client.GetStringAsync("https://api.ipify.org/?format=json");

    Console.WriteLine(Response);
}

```