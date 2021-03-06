﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using App.Metrics;
using App.Metrics.Extensions.Reporting.InfluxDB;
using App.Metrics.Extensions.Reporting.InfluxDB.Client;
using App.Metrics.Filtering;
using App.Metrics.Reporting.Interfaces;
using App.Metrics.Scheduling;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Api.Sample
{
    public class Startup
    {
        private static readonly Random Random = new Random();

        public Startup(IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", true, true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", true)
                .AddEnvironmentVariables();
            Configuration = builder.Build();
        }

        public IConfigurationRoot Configuration { get; set; }

        public void Configure(
            IApplicationBuilder app,
            IHostingEnvironment env,
            ILoggerFactory loggerFactory,
            IApplicationLifetime lifetime)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.RollingFile(Path.Combine($@"C:\logs\{env.ApplicationName}", "log-{Date}.txt"))
                .CreateLogger();

            //loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            //loggerFactory.AddDebug();
            //loggerFactory.AddSerilog(Log.Logger);

            app.UseMetrics();
            app.UseMetricsReporting(lifetime);

            // DEVNOTE: Fake a client being authorized to test oauth2 client request rate middleare
            app.Use(
                (context, func) =>
                {
                    context.User =
                        new ClaimsPrincipal(
                            new List<ClaimsIdentity>
                            {
                                new ClaimsIdentity(
                                    new[]
                                    {
                                        new Claim("client_id", "client" + Random.Next(1, 500))
                                    })
                            });
                    return func();
                });

            RunSampleRequests(lifetime.ApplicationStopping);

            app.UseMvc();
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services
                .AddLogging()
                .AddRouting(options => { options.LowercaseUrls = true; });

            services.AddMvc(options => options.AddMetricsResourceFilter());

            services
                .AddMetrics(Configuration.GetSection("AppMetrics"))
                //.AddGlobalFilter(new DefaultMetricsFilter().WhereMetricTaggedWithKeyValue(new TagKeyValueFilter { { "reporter", "influxdb" } }))
                .AddJsonSerialization()
                .AddReporting(
                    factory =>
                    {
                        var influxFilter = new DefaultMetricsFilter()
                            //.WhereMetricTaggedWithKeyValue(new TagKeyValueFilter { { "reporter", "influxdb" } })
                            .WithHealthChecks(true)
                            .WithEnvironmentInfo(true);

                        factory.AddInfluxDb(
                            new InfluxDBReporterSettings
                            {
                                HttpPolicy = new HttpPolicy
                                             {
                                                 FailuresBeforeBackoff = 3,
                                                 BackoffPeriod = TimeSpan.FromSeconds(30),
                                                 Timeout = TimeSpan.FromSeconds(3)
                                             },
                                InfluxDbSettings = new InfluxDBSettings("appmetricsapi", new Uri("http://127.0.0.1:8086")),
                                ReportInterval = TimeSpan.FromSeconds(5)
                            },
                            influxFilter);
                    })
                .AddHealthChecks(
                    factory =>
                    {
                        factory.RegisterProcessPrivateMemorySizeHealthCheck("Private Memory Size", 200);
                        factory.RegisterProcessVirtualMemorySizeHealthCheck("Virtual Memory Size", 200);
                        factory.RegisterProcessPhysicalMemoryHealthCheck("Working Set", 200);
                    })
                .AddMetricsMiddleware(Configuration.GetSection("AspNetMetrics"));
        }

        private static void RunSampleRequests(CancellationToken token)
        {
            var scheduler = new DefaultTaskScheduler();
            var httpClient = new HttpClient
                             {
                                 BaseAddress = new Uri("http://localhost:50202")
                             };

            Task.Run(
                () => scheduler.Interval(
                    TimeSpan.FromMilliseconds(200),
                    TaskCreationOptions.None,
                    async () =>
                    {
                        var satisfied = httpClient.GetAsync("api/values", token);
                        var bad = httpClient.GetAsync("api/bad", token);
                        var unauth = httpClient.GetAsync("api/unauth", token);
                        var error = httpClient.GetAsync("api/error", token);

                        await Task.WhenAll(satisfied, bad, unauth);
                    },
                    token),
                token);

            
        }
    }
}