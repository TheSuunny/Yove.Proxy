# Yove.Proxy | Socks4/Socks5 for IWebProxy

This project is suitable for all WebProxy, HTTP Client, WebSocket and for others.

[![NuGet version](https://badge.fury.io/nu/Yove.Proxy.svg)](https://badge.fury.io/nu/Yove.Proxy)
[![Downloads](https://img.shields.io/nuget/dt/Yove.Proxy.svg)](https://www.nuget.org/packages/Yove.Proxy)
[![Target](https://img.shields.io/badge/.NET%20Standard-2.0-green.svg)](https://docs.microsoft.com/ru-ru/dotnet/standard/net-standard)

Nuget: https://www.nuget.org/packages/Yove.Proxy/

```sh
Install-Package Yove.Proxy
```

```sh
dotnet add package Yove.Proxy
```

# Example

```csharp
ProxyClient proxy = new ProxyClient("138.68.161.60", 1080, ProxyType.Socks5);
ProxyClient proxy = new ProxyClient("138.68.161.60:1080", ProxyType.Socks5);
ProxyClient proxy = new ProxyClient("138.68.161.60:1080", "UserID / Username", ProxyType.Socks4);
ProxyClient proxy = new ProxyClient("138.68.161.60:1080", "Username", "Password", ProxyType.Socks5);
```

### WebSocket

```csharp
using (ProxyClient proxy = new ProxyClient("36.67.195.34", 57456, ProxyType.Socks5)
{
    ReadWriteTimeOut = 10000
})
{
    ClientWebSocket webSocket = new ClientWebSocket
    {
        Options.Proxy = proxy
    };

    await webSocket.ConnectAsync(new Uri("wss://echo.websocket.org"), TokenSource.Token);
}
```

### HttpClient

```csharp
using (ProxyClient proxy = new ProxyClient("36.67.195.34", 57456, ProxyType.Socks4)
{
    ReadWriteTimeOut = 10000
})
{
    HttpClientHandler handler = new HttpClientHandler { Proxy = proxy };
    HttpClient client = new HttpClient(handler);

    try
    {
        string response = await client.GetStringAsync("https://api.ipify.org/?format=json");

        Console.WriteLine(response);
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
    }
    finally
    {
        handler.Dispose();
        client.Dispose();
    }
}
```
