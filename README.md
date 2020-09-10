# Yove.Proxy | Socks4/Socks5 for IWebProxy

This project is suitable for all WebProxy, HTTP Client, WebSocket and for others.

[![NuGet version](https://badge.fury.io/nu/Yove.Proxy.svg)](https://badge.fury.io/nu/Yove.Proxy)
[![Downloads](https://img.shields.io/nuget/dt/Yove.Proxy.svg)](https://www.nuget.org/packages/Yove.Proxy)
[![Target](https://img.shields.io/badge/.NET%20Standard-2.0-green.svg)](https://docs.microsoft.com/ru-ru/dotnet/standard/net-standard)

<a href="https://www.buymeacoffee.com/3ZEnINLSR" target="_blank"><img src="https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png" alt="Buy Me A Coffee" style="height: auto !important;width: auto !important;" ></a>

Nuget: https://www.nuget.org/packages/Yove.Proxy/

```sh
Install-Package Yove.Proxy
```

```sh
dotnet add package Yove.Proxy
```

# Example

```csharp
new ProxyClient("138.68.161.60", 1080, ProxyType.Socks5);
new ProxyClient("138.68.161.60:1080", ProxyType.Socks5);
new ProxyClient("138.68.161.60:1080", "UserID / Username", ProxyType.Socks4);
new ProxyClient("138.68.161.60:1080", "Username", "Password", ProxyType.Socks5);
```

### WebSocket

```csharp
using (ProxyClient Proxy = new ProxyClient("36.67.195.34", 57456, ProxyType.Socks5)
{
    ReadWriteTimeOut = 10000
})
{
    ClientWebSocket WebSocket = new ClientWebSocket
    {
        Options.Proxy = Proxy
    };

    await WebSocket.ConnectAsync(new Uri("wss://echo.websocket.org"), TokenSource.Token);
}
```

### HttpClient

```csharp
using (ProxyClient Proxy = new ProxyClient("36.67.195.34", 57456, ProxyType.Socks4)
{
    ReadWriteTimeOut = 10000
})
{
    HttpClientHandler Handler = new HttpClientHandler { Proxy = Proxy };
    HttpClient Client = new HttpClient(Handler);

    try
    {
        string Response = await Client.GetStringAsync("https://api.ipify.org/?format=json");

        Console.WriteLine(Response);
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
    }
    finally
    {
        Handler.Dispose();
        Client.Dispose();
    }
}
```

---

### Other

If you are missing something in the library, do not be afraid to write me :)

<thesunny@tuta.io>
