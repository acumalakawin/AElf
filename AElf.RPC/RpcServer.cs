﻿using System;
using System.Threading.Tasks;
using AElf.Kernel.Types.Common;
using AElf.Network;
using AElf.Network.Peers;
using AElf.RPC.Hubs.Net;
using Microsoft.AspNetCore.Hosting;
using Easy.MessageHub;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AElf.RPC
{
    public class RpcServer : IRpcServer
    {
        private IWebHost _host;
        public ILogger<RpcServer> Logger {get;set;}

        public RpcServer()
        {
            Logger = NullLogger<RpcServer>.Instance;
            
        }

        public async Task StopAsync()
        {
            await _host.StopAsync();
        }

        public bool Init(IServiceProvider scope, string rpcHost, int rpcPort)
        {
            try
            {
                var url = "http://" + rpcHost + ":" + rpcPort;

                Startup.Parent = scope;
                _host = new WebHostBuilder()
                    .UseKestrel(options =>
                        {
                            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(20);
                            options.Limits.MaxConcurrentConnections = 200;
                            //options.Limits.MaxConcurrentUpgradedConnections = 100;
                            //options.Limits.MaxRequestBodySize = 10 * 1024;
                        }
                    )
                    .UseStartup<Startup>()
                    .UseUrls(url)
                    .Build();

                _host.Services.GetService<NetContext>();
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Exception while RPC server init.");
                return false;
            }

            return true;
        }

        public async Task StartAsync()
        {
            try
            {
                Logger.LogInformation("RPC server start.");
                await _host.RunAsync();
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Exception while start RPC server.");
            }
        }

        
        public class Startup
        {
            public static IServiceProvider Parent { get; set; }
        
            public IServiceProvider ConfigureServices(IServiceCollection sc)
            {
                sc.AddCors();

                sc.AddSignalRCore();
                sc.AddSignalR();

                //sc.AddScoped<NetContext>();
                
                return new ChildServiceProvider(Parent,sc.BuildServiceProviderFromFactory());
            }
            
            public void Configure(IApplicationBuilder app)
            {
                app.UseCors(builder => { builder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod(); });
                app.UseSignalR(routes => { routes.MapHub<NetworkHub>("/events/net"); });

                RpcServerHelpers.Configure(app, Parent.GetRequiredService<IServiceCollection>());
            }

        }
    
        public class ChildServiceProvider : IServiceProvider
        {
            private readonly IServiceProvider _child;
            private readonly IServiceProvider _parent;

            public ChildServiceProvider(IServiceProvider parent, IServiceProvider child)
            {
                _parent = parent;
                _child = child;
            }

            public ChildServiceProvider(IServiceProvider parent, IServiceCollection services)
            {
                _parent = parent;
                _child = services.BuildServiceProvider();
            }

            public object GetService(Type serviceType)
            {
                return _child.GetService(serviceType) ?? _parent.GetService(serviceType);
            }
        }
    }
    
}