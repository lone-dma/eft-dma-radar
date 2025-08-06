using eft_dma_radar.Misc.Workers;
using eft_dma_radar.Tarkov.Player;
using eft_dma_radar.Tarkov.WebRadar.Data;
using eft_dma_radar.Tarkov.WebRadar.MessagePack;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Open.Nat;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace eft_dma_radar.Tarkov.WebRadar
{
    internal static class WebRadarServer
    {
        private static readonly WebRadarUpdate _update = new();
        private static IHost _host;
        private static IHubContext<RadarServerHub> _hub;
        private static WorkerThread _webRadarWorker;

        /// <summary>
        /// Password for this Server.
        /// </summary>
        public static string Password { get; } = GetRandomPassword(10);

        #region Public API

        /// <summary>
        /// Startup web server for Web Radar.
        /// </summary>
        /// <param name="ip">IP to bind to.</param>
        /// <param name="port">TCP Port to bind to.</param>
        /// <param name="tickRate">How often radar updates should be broadcast.</param>
        /// <param name="upnp">True if Port Forwarding should be setup via UPnP.</param>
        public static async Task StartAsync(string ip, int port, TimeSpan tickRate, bool upnp)
        {
            if (_host is not null)
                throw new InvalidOperationException("Web Radar Server is already running!");
            try
            {
                ThrowIfInvalidBindParameters(ip, port);
                if (upnp)
                {
                    await ConfigureUPnPAsync(port);
                }
                _host = BuildWebRadarHost(ip, port);
                _host.Start();
                _hub = _host.Services.GetRequiredService<IHubContext<RadarServerHub>>();
            }
            catch
            {
                _host?.Dispose();
                _host = null;
                _hub = null;
                throw;
            }

            // Start the background worker
            _webRadarWorker = new()
            {
                Name = "Web Radar Worker",
                SleepDuration = tickRate
            };
            _webRadarWorker.PerformWork += WebRadarWorker_PerformWork;
            _webRadarWorker.Start();
        }

        /// <summary>
        /// Get the External IP of the user running the Server.
        /// </summary>
        /// <returns>External WAN IP.</returns>
        public static async Task<string> GetExternalIPAsync()
        {
            string ip = null;
            try
            {
                ip = await QueryUPnPForIPAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WARNING: Failed to get External IP via UPnP: {ex}");
            }
            ip ??= "REPLACE_WITH_IP";
            return ip;
        }

        #endregion

        #region Private API

        private static void WebRadarWorker_PerformWork(object sender, WorkerThreadArgs e)
        {
            if (Memory.InRaid && Memory.Players is IReadOnlyCollection<PlayerBase> players && players.Count > 0)
            {
                _update.InGame = true;
                _update.MapID = Memory.MapID;
                _update.Players = players.Select(p => WebRadarPlayer.CreateFromPlayer(p));
            }
            else
            {
                _update.InGame = false;
                _update.MapID = null;
                _update.Players = null;
            }
            _update.Version++;
            _hub.Clients.All.SendAsync("RadarUpdate", _update).GetAwaiter().GetResult();
        }

        private static IHost BuildWebRadarHost(string ip, int port)
        {
            return Host.CreateDefaultBuilder()
                    .ConfigureWebHostDefaults(webBuilder =>
                    {
                        webBuilder.UseKestrel()
                            .ConfigureServices(services =>
                            {
                                services.AddSignalR(options =>
                                {
                                    options.MaximumReceiveMessageSize = 1024 * 128; // Set the maximum message size to 128KB
                                })
                                .AddMessagePackProtocol(options =>
                                {
                                    options.SerializerOptions = MessagePackSerializerOptions.Standard
                                        .WithSecurity(MessagePackSecurity.TrustedData)
                                        .WithCompression(MessagePackCompression.Lz4BlockArray)
                                        .WithResolver(ResolverGenerator.Instance);
                                });
                                services.AddCors(options =>
                                {
                                    options.AddDefaultPolicy(builder =>
                                    {
                                        builder.AllowAnyOrigin()
                                               .AllowAnyHeader()
                                               .AllowAnyMethod()
                                               .SetIsOriginAllowedToAllowWildcardSubdomains();
                                    });
                                });
                            })
                            .Configure(app =>
                            {
                                app.UseCors();
                                app.UseRouting();
                                app.UseEndpoints(endpoints =>
                                {
                                    endpoints.MapHub<RadarServerHub>("/hub/5ed52f70-e714-4596-a8e2-e6a400a0802c");
                                });
                            })
                            .UseUrls($"http://{FormatIPForURL(ip)}:{port}");
                    })
                    .Build();
        }

        /// <summary>
        /// Checks if the specified IP Address / Port Number are valid, and throws an exception if they are invalid.
        /// Performs a TCP Bind Test.
        /// </summary>
        /// <param name="ip">IP to test bind.</param>
        /// <param name="port">Port to test bind.</param>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="Exception"></exception>
        private static void ThrowIfInvalidBindParameters(string ip, int port)
        {
            try
            {
                if (port is < 1024 or > 65535)
                    throw new ArgumentException("Invalid Port. We recommend using a Port between 50000-60000.");
                var ipObj = IPAddress.Parse(ip);
                using var socket = new Socket(ipObj.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(ipObj, port));
                socket.Close();
            }
            catch (SocketException ex)
            {
                throw new ArgumentException($"Invalid Bind Parameters. Use your Radar PC's Local LAN IP (example: 192.168.1.100), and a port number between 50000-60000.\n" +
                    $"SocketException: {ex.Message}");
            }
        }

        /// <summary>
        /// Formats an IP Host string for use in a URL.
        /// </summary>
        /// <param name="host">IP/Hostname to check/format.</param>
        /// <returns>Formatted IP, or original string if no formatting is needed.</returns>
        private static string FormatIPForURL(string host)
        {
            if (host is null)
                return null;
            if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily is AddressFamily.InterNetworkV6)
                return $"[{host}]";
            return host;
        }

        /// <summary>
        /// Get the Nat Device for the local UPnP Service.
        /// </summary>
        /// <returns>Task with NatDevice object.</returns>
        private async static Task<NatDevice> GetNatDeviceAsync()
        {
            var dsc = new NatDiscoverer();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await dsc.DiscoverDeviceAsync(PortMapper.Upnp, cts);
        }

        /// <summary>
        /// Attempts to setup UPnP Port Forwarding for the specified port.
        /// </summary>
        /// <param name="port">Port to forward.</param>
        /// <returns>Task with result of operation.</returns>
        /// <exception cref="Exception"></exception>
        private static async Task ConfigureUPnPAsync(int port)
        {
            try
            {
                var upnp = await GetNatDeviceAsync();

                // Create New Mapping
                await upnp.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port, 86400, "Eft Web Radar"));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"ERROR Setting up UPnP: {ex.Message}");
            }
        }

        /// <summary>
        /// Lookup the External IP Address via UPnP.
        /// </summary>
        /// <returns>External IP Address.</returns>
        private static async Task<string> QueryUPnPForIPAsync()
        {
            var upnp = await GetNatDeviceAsync();
            var ip = await upnp.GetExternalIPAsync();
            return ip.ToString();
        }

        private sealed class RadarServerHub : Hub
        {
            public override async Task OnConnectedAsync()
            {
                var httpContext = Context.GetHttpContext();

                string password = httpContext?.Request?.Query?["password"].ToString() ?? "";
                if (password != Password)
                {
                    Context.Abort();
                    return;
                }

                await base.OnConnectedAsync();
            }
        }

        /// <summary>
        /// Get a random password of a specified length.
        /// </summary>
        /// <param name="length">Password length.</param>
        /// <returns>Random alpha-numeric password.</returns>
        private static string GetRandomPassword(int length)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
            string pw = "";
            for (int i = 0; i < length; i++)
                pw += chars[RandomNumberGenerator.GetInt32(chars.Length)];
            return pw;
        }

        #endregion
    }
}
