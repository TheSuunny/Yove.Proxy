using System;
using System.Net;
using System.Text;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Security.Authentication;

namespace Yove.Proxy
{
    public class ProxyClient : IDisposable, IWebProxy
    {
        #region IWebProxy

        public ICredentials Credentials { get; set; }

        public int ReadWriteTimeOut { get; set; } = 30000;

        public Uri GetProxy(Uri destination) => _internalUri;
        public bool IsBypassed(Uri host) => false;

        #endregion

        #region Internal Server

        private Uri _internalUri { get; set; }
        private Socket _internalServer { get; set; }
        private int _internalPort { get; set; }

        #endregion

        #region ProxyClient

        private IPAddress _host { get; set; }
        private int _port { get; set; }
        private string _username { get; set; }
        private string _password { get; set; }
        private ProxyType _type { get; set; }
        private int _socksVersion { get; set; }

        public bool IsDisposed { get; set; }

        #endregion

        private const byte AddressTypeIPV4 = 0x01;
        private const byte AddressTypeIPV6 = 0x04;
        private const byte AddressTypeDomainName = 0x03;

        public ProxyClient(string proxy, ProxyType type)
            : this(proxy, null, null, null, type) { }

        public ProxyClient(string proxy, string username, ProxyType type)
            : this(proxy, null, username, null, type) { }

        public ProxyClient(string proxy, string username, string password, ProxyType type)
            : this(proxy, null, username, password, type) { }

        public ProxyClient(string host, int port, ProxyType type)
            : this(host, port, null, null, type) { }

        public ProxyClient(string host, int port, string username, ProxyType type)
            : this(host, port, username, null, type) { }

        public ProxyClient(string host, int? port, string username, string password, ProxyType type)
        {
            if (type == ProxyType.Http)
            {
                _internalUri = new Uri($"http://{host}:{port}");
                return;
            }

            if (string.IsNullOrEmpty(host))
                throw new ArgumentNullException("Host null or empty");

            if (port == null && host.Contains(":"))
            {
                port = Convert.ToInt32(host.Split(':')[1].Trim());
                host = host.Split(':')[0];

                if (port < 0 || port > 65535)
                    throw new ArgumentOutOfRangeException("Port goes beyond");
            }
            else if (port == null && !host.Contains(":"))
            {
                throw new ArgumentNullException("Incorrect host");
            }

            if (!string.IsNullOrEmpty(username))
            {
                if (username.Length > 255)
                    throw new ArgumentNullException("Username null or long");

                _username = username;
            }

            if (!string.IsNullOrEmpty(password))
            {
                if (password.Length > 255)
                    throw new ArgumentNullException("Password null or long");

                _password = password;
            }

            _host = GetHost(host);
            _port = port.Value;
            _type = type;
            _socksVersion = (type == ProxyType.Socks4) ? 4 : 5;

            CreateInternalServer();
        }

        private async void CreateInternalServer()
        {
            _internalServer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = ReadWriteTimeOut,
                SendTimeout = ReadWriteTimeOut,
                ExclusiveAddressUse = true
            };

            _internalServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            _internalPort = ((IPEndPoint)(_internalServer.LocalEndPoint)).Port;
            _internalUri = new Uri($"http://127.0.0.1:{_internalPort}");

            _internalServer.Listen(512);

            while (!IsDisposed)
            {
                try
                {
                    using (Socket internalClient = await _internalServer?.AcceptAsync())
                    {
                        if (internalClient != null)
                            await HandleClient(internalClient);
                    }
                }
                catch
                {
                    //? Ignore dispose internal server
                }
            }
        }

        private async Task HandleClient(Socket internalClient)
        {
            if (IsDisposed)
                return;

            byte[] headerBuffer = new byte[8192];

            internalClient.Receive(headerBuffer, headerBuffer.Length, 0);

            string header = Encoding.ASCII.GetString(headerBuffer);

            string httpVersion = header.Split(' ')[2].Split('\r')[0]?.Trim();
            string targetURL = header.Split(' ')[1]?.Trim();

            if (string.IsNullOrEmpty(httpVersion) || string.IsNullOrEmpty(targetURL))
                return;

            string targetHostname = string.Empty;
            int targetPort = 0;

            if (targetURL.Contains(":") && !targetURL.Contains("http://"))
            {
                targetHostname = targetURL.Split(':')[0];
                targetPort = int.Parse(targetURL.Split(':')[1]);
            }
            else
            {
                Uri uri = new Uri(targetURL);

                targetHostname = uri.Host;
                targetPort = uri.Port;
            }

            using (Socket targetClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = ReadWriteTimeOut,
                SendTimeout = ReadWriteTimeOut,
                ExclusiveAddressUse = true
            })
            {
                try
                {
                    if (!targetClient.ConnectAsync(_host, _port).Wait(ReadWriteTimeOut) || !targetClient.Connected)
                    {
                        SendMessage(internalClient, $"{httpVersion} 408 Request Timeout\r\n\r\n");
                        return;
                    }

                    SocketError connection = _type == ProxyType.Socks4 ?
                        await SendSocks4(targetClient, targetHostname, targetPort) :
                        await SendSocks5(targetClient, targetHostname, targetPort);

                    if (connection != SocketError.Success)
                    {
                        if (connection == SocketError.HostUnreachable || connection == SocketError.ConnectionRefused || connection == SocketError.ConnectionReset)
                            SendMessage(internalClient, $"{httpVersion} 502 Bad Gateway\r\n\r\n");
                        else if (connection == SocketError.AccessDenied)
                            SendMessage(internalClient, $"{httpVersion} 401 Unauthorized\r\n\r\n");
                        else
                            SendMessage(internalClient, $"{httpVersion} 500 Internal Server Error\r\nX-Proxy-Error-Type: {connection}\r\n\r\n");
                    }
                    else
                    {
                        SendMessage(internalClient, $"{httpVersion} 200 Connection established\r\n\r\n");

                        Relay(internalClient, targetClient, false);
                    }
                }
                catch (AuthenticationException)
                {
                    SendMessage(internalClient, $"{httpVersion} 511 Network Authentication Required\r\n\r\n");
                }
                catch
                {
                    SendMessage(internalClient, $"{httpVersion} 408 Request Timeout\r\n\r\n");
                }
            }
        }

        private void Relay(Socket source, Socket target, bool isTarget)
        {
            try
            {
                if (!isTarget)
                    Task.Run(() => Relay(target, source, true));

                while (true)
                {
                    byte[] buffer = new byte[8192];

                    int read = source.Receive(buffer, 0, buffer.Length, SocketFlags.None);

                    if (read == 0)
                        break;

                    target.Send(buffer, 0, read, SocketFlags.None);
                }
            }
            catch
            {
                //? Ignore timeout exception
            }
        }

        private async Task<SocketError> SendSocks4(Socket socket, string destinationHost, int destinationPort)
        {
            byte addressType = GetAddressType(destinationHost);

            if (addressType == AddressTypeDomainName)
                destinationHost = GetHost(destinationHost).ToString();

            byte[] address = GetIPAddressBytes(destinationHost);
            byte[] port = GetPortBytes(destinationPort);
            byte[] userId = string.IsNullOrEmpty(_username) ? new byte[0] : Encoding.ASCII.GetBytes(_username);

            byte[] request = new byte[9 + userId.Length];

            request[0] = (byte)_socksVersion;
            request[1] = 0x01;
            address.CopyTo(request, 4);
            port.CopyTo(request, 2);
            userId.CopyTo(request, 8);
            request[8 + userId.Length] = 0x00;

            byte[] response = new byte[8];

            socket.Send(request);

            await WaitStream(socket);

            socket.Receive(response);

            if (response[1] != 0x5a)
                return SocketError.ConnectionRefused;

            return SocketError.Success;
        }

        private async Task<SocketError> SendSocks5(Socket socket, string destinationHost, int destinationPort)
        {
            byte[] response = new byte[255];

            byte[] auth = new byte[3];
            auth[0] = (byte)_socksVersion;
            auth[1] = (byte)1;

            if (!string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password))
                auth[2] = 0x02;
            else
                auth[2] = (byte)0;

            socket.Send(auth);

            await WaitStream(socket);

            socket.Receive(response);

            if (response[1] == 0x02)
                await SendAuth(socket);
            else if (response[1] != 0x00)
                return SocketError.ConnectionRefused;

            byte addressType = GetAddressType(destinationHost);

            if (addressType == AddressTypeDomainName)
                destinationHost = GetHost(destinationHost).ToString();

            byte[] address = GetAddressBytes(addressType, destinationHost);
            byte[] port = GetPortBytes(destinationPort);

            byte[] request = new byte[4 + address.Length + 2];

            request[0] = (byte)_socksVersion;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = addressType;

            address.CopyTo(request, 4);
            port.CopyTo(request, 4 + address.Length);

            socket.Send(request);

            await WaitStream(socket);

            socket.Receive(response);

            if (response[1] != 0x00)
                return SocketError.ConnectionRefused;

            return SocketError.Success;
        }

        private async Task SendAuth(Socket socket)
        {
            byte[] username = Encoding.ASCII.GetBytes(_username);
            byte[] password = Encoding.ASCII.GetBytes(_password);

            byte[] request = new byte[username.Length + password.Length + 3];

            request[0] = 1;
            request[1] = (byte)username.Length;
            username.CopyTo(request, 2);
            request[2 + username.Length] = (byte)password.Length;
            password.CopyTo(request, 3 + username.Length);

            socket.Send(request);

            byte[] response = new byte[2];

            await WaitStream(socket);

            socket.Receive(response);

            if (response[1] != 0x00)
                throw new AuthenticationException();
        }

        private async Task WaitStream(Socket socket)
        {
            int sleep = 0;
            int delay = (socket.ReceiveTimeout < 10) ? 10 : socket.ReceiveTimeout;

            while (socket.Available == 0)
            {
                if (sleep < delay)
                {
                    sleep += 10;
                    await Task.Delay(10);

                    continue;
                }

                throw new TimeoutException();
            }
        }

        private void SendMessage(Socket client, string message)
        {
            client.Send(Encoding.UTF8.GetBytes(message));
        }

        private IPAddress GetHost(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress ip))
                return ip;

            return Dns.GetHostAddresses(host)[0];
        }

        private byte[] GetAddressBytes(byte addressType, string host)
        {
            switch (addressType)
            {
                case AddressTypeIPV4:
                case AddressTypeIPV6:
                    return IPAddress.Parse(host).GetAddressBytes();
                case AddressTypeDomainName:
                    byte[] bytes = new byte[host.Length + 1];

                    bytes[0] = (byte)host.Length;
                    Encoding.ASCII.GetBytes(host).CopyTo(bytes, 1);

                    return bytes;
                default:
                    return null;
            }
        }

        private byte GetAddressType(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress ip))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    return AddressTypeIPV4;

                return AddressTypeIPV6;
            }

            return AddressTypeDomainName;
        }

        private byte[] GetIPAddressBytes(string destinationHost)
        {
            IPAddress address = null;

            if (!IPAddress.TryParse(destinationHost, out address))
            {
                IPAddress[] ips = Dns.GetHostAddresses(destinationHost);

                if (ips.Length > 0)
                    address = ips[0];
            }

            return address.GetAddressBytes();
        }

        private byte[] GetPortBytes(int port)
        {
            byte[] arrayBytes = new byte[2];

            arrayBytes[0] = (byte)(port / 256);
            arrayBytes[1] = (byte)(port % 256);

            return arrayBytes;
        }

        public void Dispose()
        {
            if (!IsDisposed)
            {
                IsDisposed = true;

                if (_internalServer != null && _internalServer.Connected)
                    _internalServer.Disconnect(false);

                _internalServer?.Dispose();

                _internalServer = null;
            }
        }

        ~ProxyClient()
        {
            Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
