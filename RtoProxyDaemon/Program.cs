using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Serilog;
using SocksSharp;
using SocksSharp.Proxy;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace RtoProxyDaemon
{
    internal enum ProxyType
    {
        Http,
        Socks
    }

    internal class RtoProxyConfiguration
    {
        public ushort Port { get; set; }
        public ProxyType ProxyType { get; set; }
    }

    internal class ProxyInfo
    {
        public string Host { get; }
        public ushort Port { get; }

        public ProxyInfo(string host, ushort port) => (Host, Port) = (host, port);
    }

    internal class Program
    {
        private static readonly object SyncRoot = new object();
        private static volatile ProxyInfo _curProxy;

        internal static async Task Main()
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile("config.json", false)
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(config)
                .CreateLogger();

            Log.Information("RtoProxyDaemon by Zawodskoj");

            var rtoConfig = config.GetSection("RtoProxy").Get<RtoProxyConfiguration>();
            var listener = TcpListener.Create(rtoConfig.Port);
            listener.Start();

            Log.Debug("Listener on port {Port} started", rtoConfig.Port);

            Listen(listener);

            while (true)
            {
                const int refreshDelaySec = 300;
                const int retryDelaySec = 5;

                var newProxy = await GetNewProxy(rtoConfig.ProxyType);

                if (newProxy != null)
                {
                    lock (SyncRoot)
                    {
                        _curProxy = newProxy;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(refreshDelaySec));
                }
                else await Task.Delay(TimeSpan.FromSeconds(retryDelaySec));
            }
        }

        private static async Task<ProxyInfo> GetNewProxy(ProxyType proxyType)
        {
            try
            {
                using (var hc = new HttpClient())
                {
                    var response = await hc.GetStringAsync(
                        "https://api.rufolder.net/JIkJnKmlsFIB/v2/" +
                        (proxyType == ProxyType.Http ? "proxies" : "socks"));

                    var splitted = response.Split(":");
                    if (splitted.Length != 2)
                    {
                        Log.Error("Invalid response format. Response: {Response}", response);
                        return null;
                    }

                    if (!ushort.TryParse(splitted[1], out var port))
                    {
                        Log.Error("Invalid port. Response: {Response}", response);
                        return null;
                    }

                    var info = new ProxyInfo(splitted[0], port);
                    Log.Information("Got new proxy: {Host}:{Port}", info.Host, info.Port);
                    return await CheckProxy(info, proxyType) ? info : null;
                }
            }
            catch (HttpRequestException e)
            {
                Log.Error(e, "Request to backend failed");
            }
            catch (Exception e)
            {
                Log.Error(e, "Unknown error");
            }

            return null;
        }

        private static async Task<bool> CheckProxy(ProxyInfo info, ProxyType proxyType)
        {
            var handler =
                proxyType == ProxyType.Socks
                    ? (HttpMessageHandler) new ProxyClientHandler<Socks5>(new ProxySettings
                    {
                        Port = info.Port,
                        Host = info.Host
                    })
                    : new HttpClientHandler
                    {
                        Proxy = new WebProxy(info.Host, info.Port),
                    };
            using (var hc = new HttpClient(handler))
            {
                hc.DefaultRequestHeaders.Add("User-Agent", "rto/proxy-app");

                string response = null;
                try
                {
                    response = await hc.GetStringAsync("http://bt2.rutracker.org/myip?json");
                    var proxy = JObject.Parse(response)["proxy"];
                    if (proxy.ToString() == info.Host)
                    {
                        Log.Information("Proxy OK");
                        return true;
                    }

                    Log.Error("Proxy mismatch. Response: {Response}", response);
                    return false;
                }
                catch (HttpRequestException)
                {
                    Log.Error("Proxy check failed");
                }
                catch (Exception e)
                {
                    Log.Error(e, "Unknown error during proxy check. Response: {Response}", response);
                }
            }

            return false;
        }

        private static async void Listen(TcpListener listener)
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();

                Log.Information("Client {Endpoint} connected", client.Client.RemoteEndPoint);

                ProxyInfo curProxy;
                lock (SyncRoot) curProxy = _curProxy;

                if (curProxy == null)
                {
                    Log.Warning("No proxy found yet, dropping client {Endpoint}", client.Client.RemoteEndPoint);
                    client.Client.DisconnectAsync(new SocketAsyncEventArgs
                    {
                        DisconnectReuseSocket = true
                    });
                    continue;
                }

                Tunnel(curProxy, client);
            }
        }

        private static async void Tunnel(ProxyInfo curProxy, TcpClient client)
        {
            using (var stream = client.GetStream())
            {
                try
                {
                    using (var proxyClient = new TcpClient(curProxy.Host, curProxy.Port))
                    using (var proxyStream = proxyClient.GetStream())
                    {
                        await Task.WhenAny(
                            proxyStream.CopyToAsync(stream),
                            stream.CopyToAsync(proxyStream));
                    }
                }
                catch (Exception e)
                {
                    Log.Debug(e, "Exception during tunneling proxy");
                }
            }
        }
    }
}
