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

        public Uri GetProxy(Uri destination) => InternalUri;
        public bool IsBypassed(Uri host) => false;

        #endregion

        #region Internal Server

        private Uri InternalUri { get; set; }
        private Socket InternalServer { get; set; }
        private int InternalPort { get; set; }

        #endregion

        #region ProxyClient

        private IPAddress Host { get; set; }
        private int Port { get; set; }
        private string Username { get; set; }
        private string Password { get; set; }
        private ProxyType Type { get; set; }
        private int SocksVersion { get; set; }

        public bool IsDisposed { get; set; }

        #endregion

        private const byte AddressTypeIPV4 = 0x01;
        private const byte AddressTypeIPV6 = 0x04;
        private const byte AddressTypeDomainName = 0x03;

        public ProxyClient(string Proxy, ProxyType Type)
            : this(Proxy, null, null, null, Type) { }

        public ProxyClient(string Proxy, string Username, ProxyType Type)
            : this(Proxy, null, Username, null, Type) { }

        public ProxyClient(string Proxy, string Username, string Password, ProxyType Type)
            : this(Proxy, null, Username, Password, Type) { }

        public ProxyClient(string Host, int Port, ProxyType Type)
            : this(Host, Port, null, null, Type) { }

        public ProxyClient(string Host, int Port, string Username, ProxyType Type)
            : this(Host, Port, Username, null, Type) { }

        public ProxyClient(string Host, int? Port, string Username, string Password, ProxyType Type)
        {
            if (Type == ProxyType.Http)
            {
                InternalUri = new Uri($"http://{Host}:{Port}");
                return;
            }

            if (string.IsNullOrEmpty(Host))
                throw new ArgumentNullException("Host null or empty");

            if (Port == null && Host.Contains(":"))
            {
                Port = Convert.ToInt32(Host.Split(':')[1].Trim());
                Host = Host.Split(':')[0];

                if (Port < 0 || Port > 65535)
                    throw new ArgumentOutOfRangeException("Port goes beyond");
            }
            else if (Port == null && !Host.Contains(":"))
            {
                throw new ArgumentNullException("Incorrect host");
            }

            if (!string.IsNullOrEmpty(Username))
            {
                if (Username.Length > 255)
                    throw new ArgumentNullException("Username null or long");

                this.Username = Username;
            }

            if (!string.IsNullOrEmpty(Password))
            {
                if (Password.Length > 255)
                    throw new ArgumentNullException("Password null or long");

                this.Password = Password;
            }

            this.Host = GetHost(Host);
            this.Port = Port.Value;
            this.Type = Type;
            this.SocksVersion = (Type == ProxyType.Socks4) ? 4 : 5;

            CreateInternalServer();
        }

        private async void CreateInternalServer()
        {
            InternalServer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = ReadWriteTimeOut,
                SendTimeout = ReadWriteTimeOut,
                ExclusiveAddressUse = true
            };

            InternalServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            InternalPort = ((IPEndPoint)(InternalServer.LocalEndPoint)).Port;
            InternalUri = new Uri($"http://127.0.0.1:{InternalPort}");

            InternalServer.Listen(512);

            while (!IsDisposed)
            {
                try
                {
                    using (Socket InternalClient = await InternalServer?.AcceptAsync())
                    {
                        if (InternalClient != null)
                            await HandleClient(InternalClient);
                    }
                }
                catch
                {
                    //? Ignore dispose intrnal server
                }
            }
        }

        private async Task HandleClient(Socket InternalClient)
        {
            if (IsDisposed)
                return;

            byte[] HeaderBuffer = new byte[8192];

            InternalClient.Receive(HeaderBuffer, HeaderBuffer.Length, 0);

            string Header = Encoding.ASCII.GetString(HeaderBuffer);

            string HttpVersion = Header.Split(' ')[2].Split('\r')[0]?.Trim();
            string TargetURL = Header.Split(' ')[1]?.Trim();

            if (string.IsNullOrEmpty(HttpVersion) || string.IsNullOrEmpty(TargetURL))
                return;

            string TargetHostname = string.Empty;
            int TargetPort = 0;

            if (TargetURL.Contains(":") && !TargetURL.Contains("http://"))
            {
                TargetHostname = TargetURL.Split(':')[0];
                TargetPort = int.Parse(TargetURL.Split(':')[1]);
            }
            else
            {
                Uri URL = new Uri(TargetURL);

                TargetHostname = URL.Host;
                TargetPort = URL.Port;
            }

            using (Socket TargetClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = ReadWriteTimeOut,
                SendTimeout = ReadWriteTimeOut,
                ExclusiveAddressUse = true
            })
            {
                try
                {
                    TargetClient.Connect(new IPEndPoint(Host, Port));

                    SocketError Connection = Type == ProxyType.Socks4 ?
                        await SendSocks4(TargetClient, TargetHostname, TargetPort) :
                        await SendSocks5(TargetClient, TargetHostname, TargetPort);

                    if (Connection != SocketError.Success)
                    {
                        if (Connection == SocketError.HostUnreachable || Connection == SocketError.ConnectionRefused || Connection == SocketError.ConnectionReset)
                            SendMessage(InternalClient, $"{HttpVersion} 502 Bad Gateway\r\n\r\n");
                        else if (Connection == SocketError.AccessDenied)
                            SendMessage(InternalClient, $"{HttpVersion} 401 Unauthorized\r\n\r\n");
                        else
                            SendMessage(InternalClient, $"{HttpVersion} 500 Internal Server Error\r\nX-Proxy-Error-Type: {Connection}\r\n\r\n");
                    }
                    else
                    {
                        SendMessage(InternalClient, $"{HttpVersion} 200 Connection established\r\n\r\n");

                        Relay(InternalClient, TargetClient, false);
                    }
                }
                catch (AuthenticationException)
                {
                    SendMessage(InternalClient, $"{HttpVersion} 511 Network Authentication Required\r\n\r\n");
                }
                catch
                {
                    SendMessage(InternalClient, $"{HttpVersion} 408 Request Timeout\r\n\r\n");
                }
            }
        }

        private void Relay(Socket Source, Socket Target, bool IsTarget)
        {
            try
            {
                if (!IsTarget)
                    Task.Run(() => Relay(Target, Source, true));

                while (true)
                {
                    byte[] Buffer = new byte[8192];

                    int Read = Source.Receive(Buffer, 0, Buffer.Length, SocketFlags.None);

                    if (Read == 0)
                        break;

                    Target.Send(Buffer, 0, Read, SocketFlags.None);
                }
            }
            catch
            {
                //? Ignore timeout exception
            }
        }

        private async Task<SocketError> SendSocks4(Socket Socket, string DestinationHost, int DestinationPort)
        {
            byte AddressType = GetAddressType(DestinationHost);

            if (AddressType == AddressTypeDomainName)
                DestinationHost = GetHost(DestinationHost).ToString();

            byte[] Address = GetIPAddressBytes(DestinationHost);
            byte[] Port = GetPortBytes(DestinationPort);
            byte[] UserId = string.IsNullOrEmpty(Username) ? new byte[0] : Encoding.ASCII.GetBytes(Username);

            byte[] Request = new byte[9 + UserId.Length];

            Request[0] = (byte)SocksVersion;
            Request[1] = 0x01;
            Address.CopyTo(Request, 4);
            Port.CopyTo(Request, 2);
            UserId.CopyTo(Request, 8);
            Request[8 + UserId.Length] = 0x00;

            byte[] Response = new byte[8];

            Socket.Send(Request);

            await WaitStream(Socket);

            Socket.Receive(Response);

            if (Response[1] != 0x5a)
                return SocketError.ConnectionRefused;

            return SocketError.Success;
        }

        private async Task<SocketError> SendSocks5(Socket Socket, string DestinationHost, int DestinationPort)
        {
            byte[] Response = new byte[255];

            byte[] Auth = new byte[3];
            Auth[0] = (byte)SocksVersion;
            Auth[1] = (byte)1;

            if (!string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password))
                Auth[2] = 0x02;
            else
                Auth[2] = (byte)0;

            Socket.Send(Auth);

            await WaitStream(Socket);

            Socket.Receive(Response);

            if (Response[1] == 0x02)
                await SendAuth(Socket);
            else if (Response[1] != 0x00)
                return SocketError.ConnectionRefused;

            byte AddressType = GetAddressType(DestinationHost);

            if (AddressType == AddressTypeDomainName)
                DestinationHost = GetHost(DestinationHost).ToString();

            byte[] Address = GetAddressBytes(AddressType, DestinationHost);
            byte[] Port = GetPortBytes(DestinationPort);

            byte[] Request = new byte[4 + Address.Length + 2];

            Request[0] = (byte)SocksVersion;
            Request[1] = 0x01;
            Request[2] = 0x00;
            Request[3] = AddressType;

            Address.CopyTo(Request, 4);
            Port.CopyTo(Request, 4 + Address.Length);

            Socket.Send(Request);

            await WaitStream(Socket);

            Socket.Receive(Response);

            if (Response[1] != 0x00)
                return SocketError.ConnectionRefused;

            return SocketError.Success;
        }

        private async Task SendAuth(Socket Socket)
        {
            byte[] Uname = Encoding.ASCII.GetBytes(Username);
            byte[] Passwd = Encoding.ASCII.GetBytes(Password);

            byte[] Request = new byte[Uname.Length + Passwd.Length + 3];

            Request[0] = 1;
            Request[1] = (byte)Uname.Length;
            Uname.CopyTo(Request, 2);
            Request[2 + Uname.Length] = (byte)Passwd.Length;
            Passwd.CopyTo(Request, 3 + Uname.Length);

            Socket.Send(Request);

            byte[] Response = new byte[2];

            await WaitStream(Socket);

            Socket.Receive(Response);

            if (Response[1] != 0x00)
                throw new AuthenticationException();
        }

        private async Task WaitStream(Socket Socket)
        {
            int Sleep = 0;
            int Delay = (Socket.ReceiveTimeout < 10) ? 10 : Socket.ReceiveTimeout;

            while (Socket.Available == 0)
            {
                if (Sleep < Delay)
                {
                    Sleep += 10;
                    await Task.Delay(10);

                    continue;
                }

                throw new TimeoutException();
            }
        }

        private void SendMessage(Socket Client, string Message)
        {
            Client.Send(Encoding.UTF8.GetBytes(Message));
        }

        private IPAddress GetHost(string Host)
        {
            if (IPAddress.TryParse(Host, out IPAddress Ip))
                return Ip;

            return Dns.GetHostAddresses(Host)[0];
        }

        private byte[] GetAddressBytes(byte AddressType, string Host)
        {
            switch (AddressType)
            {
                case AddressTypeIPV4:
                case AddressTypeIPV6:
                    return IPAddress.Parse(Host).GetAddressBytes();
                case AddressTypeDomainName:
                    byte[] Bytes = new byte[Host.Length + 1];

                    Bytes[0] = (byte)Host.Length;
                    Encoding.ASCII.GetBytes(Host).CopyTo(Bytes, 1);

                    return Bytes;
                default:
                    return null;
            }
        }

        private byte GetAddressType(string Host)
        {
            if (IPAddress.TryParse(Host, out IPAddress Ip))
            {
                if (Ip.AddressFamily == AddressFamily.InterNetwork)
                    return AddressTypeIPV4;

                return AddressTypeIPV6;
            }

            return AddressTypeDomainName;
        }

        private byte[] GetIPAddressBytes(string DestinationHost)
        {
            IPAddress Address = null;

            if (!IPAddress.TryParse(DestinationHost, out Address))
            {
                IPAddress[] IPs = Dns.GetHostAddresses(DestinationHost);

                if (IPs.Length > 0)
                    Address = IPs[0];
            }

            return Address.GetAddressBytes();
        }

        private byte[] GetPortBytes(int Port)
        {
            byte[] ArrayBytes = new byte[2];

            ArrayBytes[0] = (byte)(Port / 256);
            ArrayBytes[1] = (byte)(Port % 256);

            return ArrayBytes;
        }

        public void Dispose()
        {
            IsDisposed = true;

            if (InternalServer != null && InternalServer.Connected)
                InternalServer.Disconnect(false);

            InternalServer?.Dispose();

            InternalServer = null;
        }
    }
}
