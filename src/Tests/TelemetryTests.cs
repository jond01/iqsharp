﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#if !TELEMETRY
using Microsoft.Quantum.IQSharp;
using System;

namespace Tests.IQSharp
{
    public static class TelemetryTests
    {
        public static readonly Type TelemetryServiceType = typeof(NullTelemetryService);
    }
}
#endif

#if TELEMETRY

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Applications.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Jupyter.Core;
using Microsoft.Quantum.IQSharp;
using Microsoft.Quantum.IQSharp.Common;
using Microsoft.Quantum.IQSharp.Kernel;
using Microsoft.Quantum.QsCompiler.CompilationBuilder;
using Microsoft.Quantum.Simulation.Simulators;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Quantum.IQSharp.AzureClient;
using System.Linq;

namespace Tests.IQSharp
{
    internal static class TelemetryTestExtensions
    {
        /// Note that this assertion will ignore telemetry events
        /// with name <c>Quantum.IQSharp.KernelPerformance<c>, as these
        /// events are nondeterministic.
        internal static void AssertEventCount(
            this MockTelemetryService.MockAppLogger logger,
            int count,
            params Action<EventProperties>[] eventAssertions 
        )
        {
            var msg = $"Expected {count} telemetry events, got:\n";
            var events = logger.Events.Where(
                             evt => evt.Name != "Quantum.IQSharp.KernelPerformance"
                         )
                         .ToList();
            foreach (var evt in events)
            {
                var evtMsg = $"\tName = {evt.Name}, Type = {evt.Type}, EventId = {evt.EventId}\n";
                foreach (var prop in evt.Properties)
                {
                    evtMsg += $"\t\t{prop.Key} = {prop.Value}\n";
                }
                msg += evtMsg;
            }
            Assert.AreEqual(count, events.Count, msg);

            var failures = new List<Exception>();
            foreach ((var evt, var assertion) in Enumerable.Zip(events, eventAssertions))
            {
                try
                {
                    assertion(evt);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
            if (failures.Any())
            {
                throw new AggregateException(failures);
            }
        }
    }

    [TestClass]
    public class TelemetryTests
    {
        public static readonly Type TelemetryServiceType = typeof(MockTelemetryService);

        public async Task<IQSharpEngine> InitAsync(IServiceProvider services)
        {
            var engine = services.GetService<IExecutionEngine>();
            if (engine is IQSharpEngine iqsEngine)
            {
                iqsEngine.Start();
                await iqsEngine.Initialized;
                Assert.IsNotNull(iqsEngine.Workspace);
                await Task.WhenAll(
                    iqsEngine.Workspace!.Initialization,
                    // Wait for any other required services. This keeps the
                    // service started telemetry events from causing problems
                    // for later assertions.
                    services.GetRequiredServiceInBackground<IAzureClient>(),
                    services.GetRequiredServiceInBackground<IEntryPointGenerator>()
                );
                return iqsEngine;
            }
            else throw new Exception($"Expected engine to be an IQSharpEngine, but got a {engine?.GetType().ToString() ?? "<null>"} instead.");
        }

        public IQSharpEngine Init(IServiceProvider services) =>
            InitAsync(services).Result;

        [TestMethod]
        public void MockTelemetryService()
        {
            var workspace = "Workspace";
            var services = Startup.CreateServiceProvider(workspace);
            GetAppLogger(services);
        }

        private static MockTelemetryService.MockAppLogger GetAppLogger(ServiceProvider services)
        {
            var telemetryService = services.GetService<ITelemetryService>();
            Assert.IsNotNull(telemetryService, "TelemetryService must not be null. It should be added in Startup.cs.");
            Assert.IsInstanceOfType(telemetryService, typeof(MockTelemetryService), "TelemetryService should be of type MockTelemetryService as set in Startup.cs");
            var mockTelemetryService = telemetryService as MockTelemetryService;
            Assert.IsInstanceOfType(mockTelemetryService.TelemetryLogger, typeof(MockTelemetryService.MockAppLogger), "TelemetryService.TelemetryLogger should be of type MockTelemetryService.MockAppLogger, set by MockTelemetryService");
            var mockAppLogger = mockTelemetryService.TelemetryLogger as MockTelemetryService.MockAppLogger;
            return mockAppLogger;
        }

        [TestMethod]
        public void WorkspaceReload()
        {
            var workspace = "Workspace";
            var services = Startup.CreateServiceProvider(workspace);
            var logger = GetAppLogger(services);

            var ws = services.GetService<IWorkspace>();

            logger.Events.Clear();
            Assert.AreEqual(0, logger.Events.Count);

            ws.Reload();
            Assert.AreEqual(1, logger.Events.Count);
            Assert.AreEqual("Quantum.IQSharp.WorkspaceReload", logger.Events[0].Name);
            Assert.AreEqual(PiiKind.GenericData, logger.Events[0].PiiProperties["Quantum.IQSharp.Workspace"]);
            Assert.AreEqual("Workspace", logger.Events[0].Properties["Quantum.IQSharp.Workspace"]);
            Assert.AreEqual("ok", logger.Events[0].Properties["Quantum.IQSharp.Status"]);
            Assert.AreEqual("", logger.Events[0].Properties["Quantum.IQSharp.Errors"]);
            Assert.AreEqual(2L, logger.Events[0].Properties["Quantum.IQSharp.FileCount"]);
            Assert.AreEqual(0L, logger.Events[0].Properties["Quantum.IQSharp.ProjectCount"]);
            AssertDuration(logger.Events[0]);
        }

        [TestMethod]
        public void InvalidWorkspaceReload()
        {
            var workspace = "Workspace.Broken";
            var services = Startup.CreateServiceProvider(workspace);
            var logger = GetAppLogger(services);

            var ws = services.GetService<IWorkspace>();

            logger.Events.Clear();
            Assert.AreEqual(0, logger.Events.Count);

            ws.Reload();
            Assert.AreEqual(1, logger.Events.Count);
            Assert.AreEqual("Quantum.IQSharp.WorkspaceReload", logger.Events[0].Name);
            Assert.AreEqual(PiiKind.GenericData, logger.Events[0].PiiProperties["Quantum.IQSharp.Workspace"]);
            Assert.AreEqual("Workspace.Broken", logger.Events[0].Properties["Quantum.IQSharp.Workspace"]);
            Assert.AreEqual("error", logger.Events[0].Properties["Quantum.IQSharp.Status"]);
            Assert.IsTrue(logger.Events[0].Properties["Quantum.IQSharp.Errors"].ToString().StartsWith("QS"));
            Assert.AreEqual(2L, logger.Events[0].Properties["Quantum.IQSharp.FileCount"]);
            Assert.AreEqual(0L, logger.Events[0].Properties["Quantum.IQSharp.ProjectCount"]);
            AssertDuration(logger.Events[0]);
        }

        [TestMethod]
        public void CompileCode()
        {
            var workspace = "Workspace";
            var services = Startup.CreateServiceProvider(workspace);
            var logger = GetAppLogger(services);

            var snippets = services.GetService<ISnippets>();

            logger.Events.Clear();
            Assert.AreEqual(0, logger.Events.Count);

            var count = 0;
            snippets.Compile(SNIPPETS.HelloQ);
            Assert.AreEqual(count + 1, logger.Events.Count);
            Assert.AreEqual("Quantum.IQSharp.Compile", logger.Events[count].Name);
            Assert.AreEqual("ok", logger.Events[count].Properties["Quantum.IQSharp.Status"]);
            Assert.AreEqual("", logger.Events[0].Properties["Quantum.IQSharp.Errors"]);
            AssertDuration(logger.Events[0]);

            count++;
            snippets.Compile(SNIPPETS.HelloQ);
            Assert.AreEqual(count + 1, logger.Events.Count);
            Assert.AreEqual("Quantum.IQSharp.Compile", logger.Events[count].Name);
            Assert.AreEqual("ok", logger.Events[count].Properties["Quantum.IQSharp.Status"]);
            Assert.AreEqual("", logger.Events[0].Properties["Quantum.IQSharp.Errors"]);

            count++;
            snippets.Compile(SNIPPETS.DependsOnHelloQ);
            Assert.AreEqual(count + 1, logger.Events.Count);
            Assert.AreEqual("Quantum.IQSharp.Compile", logger.Events[count].Name);
            Assert.AreEqual("ok", logger.Events[count].Properties["Quantum.IQSharp.Status"]);
            Assert.AreEqual("", logger.Events[0].Properties["Quantum.IQSharp.Errors"]);

            count++;
            Assert.ThrowsException<CompilationErrorsException>(() => snippets.Compile(SNIPPETS.TwoErrors));
            Assert.AreEqual(count + 1, logger.Events.Count);
            Assert.AreEqual("Quantum.IQSharp.Compile", logger.Events[count].Name);
            Assert.AreEqual("error", logger.Events[count].Properties["Quantum.IQSharp.Status"]);
            Assert.AreEqual("", logger.Events[0].Properties["Quantum.IQSharp.Errors"]);

            count++;
            snippets.Compile(SNIPPETS.OneWarning);
            Assert.AreEqual(count + 1, logger.Events.Count);
            Assert.AreEqual("Quantum.IQSharp.Compile", logger.Events[count].Name);
            Assert.AreEqual("ok", logger.Events[count].Properties["Quantum.IQSharp.Status"]);
            Assert.AreEqual("", logger.Events[0].Properties["Quantum.IQSharp.Errors"]);
            Assert.AreEqual(
                "Microsoft.Quantum.Canon,Microsoft.Quantum.Intrinsic",
                logger.Events[count].Properties["Quantum.IQSharp.Namespaces"]);

            count++;
            snippets.Compile(SNIPPETS.OpenNamespaces2);
            Assert.AreEqual(count + 1, logger.Events.Count);
            Assert.AreEqual("Quantum.IQSharp.Compile", logger.Events[count].Name);
            Assert.AreEqual("ok", logger.Events[count].Properties["Quantum.IQSharp.Status"]);
            Assert.AreEqual("", logger.Events[0].Properties["Quantum.IQSharp.Errors"]);
            Assert.AreEqual(
                "Microsoft.Quantum.Canon,Microsoft.Quantum.Diagnostics,Microsoft.Quantum.Intrinsic",
                logger.Events[count].Properties["Quantum.IQSharp.Namespaces"]);
        }

        [TestMethod]
        public void LoadPackage()
        {
            var workspace = "Workspace";
            var services = Startup.CreateServiceProvider(workspace);
            var logger = GetAppLogger(services);

            var mgr = services.GetService<IReferences>();

            logger.Events.Clear();
            Assert.AreEqual(0, logger.Events.Count);

            mgr.AddPackage("Microsoft.Quantum.Standard");
            Assert.AreEqual(1, logger.Events.Count);
            Assert.AreEqual("Quantum.IQSharp.PackageLoad", logger.Events[0].Name);
            Assert.AreEqual("Microsoft.Quantum.Standard", logger.Events[0].Properties["Quantum.IQSharp.PackageId"]);
            Assert.IsTrue(!string.IsNullOrWhiteSpace(logger.Events[0].Properties["Quantum.IQSharp.PackageVersion"]?.ToString()));
            AssertDuration(logger.Events[0]);

            mgr.AddPackage("Microsoft.Quantum.Standard");
            Assert.AreEqual(2, logger.Events.Count);
            Assert.AreEqual("Quantum.IQSharp.PackageLoad", logger.Events[0].Name);
            Assert.AreEqual("Microsoft.Quantum.Standard", logger.Events[0].Properties["Quantum.IQSharp.PackageId"]);
            Assert.IsTrue(!string.IsNullOrWhiteSpace(logger.Events[0].Properties["Quantum.IQSharp.PackageVersion"]?.ToString()));
        }

        [TestMethod]
        public void LoadProjects()
        {
            var workspace = "Workspace.ProjectReferences";
            var services = Startup.CreateServiceProvider(workspace);
            var logger = GetAppLogger(services);

            var ws = services.GetService<IWorkspace>();

            logger.Events.Clear();
            Assert.AreEqual(0, logger.Events.Count);

            ws.Reload();
            Assert.AreEqual(5, logger.Events.Count);

            Assert.AreEqual("Quantum.IQSharp.PackageLoad", logger.Events[0].Name);
            Assert.IsTrue(logger.Events[0].Properties["Quantum.IQSharp.PackageId"].ToString().StartsWith("Microsoft.Quantum.Xunit"));
            Assert.IsTrue(!string.IsNullOrWhiteSpace(logger.Events[0].Properties["Quantum.IQSharp.PackageVersion"]?.ToString()));
            AssertDuration(logger.Events[0]);

            Assert.AreEqual("Quantum.IQSharp.ProjectLoad", logger.Events[1].Name);
            Assert.AreEqual(PiiKind.Uri, logger.Events[1].PiiProperties["Quantum.IQSharp.ProjectUri"]);
            Assert.IsTrue(logger.Events[1].Properties["Quantum.IQSharp.ProjectUri"].ToString().Contains("ProjectB.csproj"));
            Assert.AreEqual(1L, logger.Events[1].Properties["Quantum.IQSharp.SourceFileCount"]);
            Assert.AreEqual(0L, logger.Events[1].Properties["Quantum.IQSharp.ProjectReferenceCount"]);
            Assert.AreEqual(0L, logger.Events[1].Properties["Quantum.IQSharp.PackageReferenceCount"]);
            Assert.AreEqual(false, logger.Events[1].Properties["Quantum.IQSharp.UserAdded"]);

            Assert.AreEqual("Quantum.IQSharp.ProjectLoad", logger.Events[2].Name);
            Assert.AreEqual(PiiKind.Uri, logger.Events[2].PiiProperties["Quantum.IQSharp.ProjectUri"]);
            Assert.IsTrue(logger.Events[2].Properties["Quantum.IQSharp.ProjectUri"].ToString().Contains("ProjectA.csproj"));
            Assert.AreEqual(1L, logger.Events[2].Properties["Quantum.IQSharp.SourceFileCount"]);
            Assert.AreEqual(1L, logger.Events[2].Properties["Quantum.IQSharp.ProjectReferenceCount"]);
            Assert.AreEqual(0L, logger.Events[2].Properties["Quantum.IQSharp.PackageReferenceCount"]);
            Assert.AreEqual(false, logger.Events[2].Properties["Quantum.IQSharp.UserAdded"]);

            Assert.AreEqual("Quantum.IQSharp.ProjectLoad", logger.Events[3].Name);
            Assert.AreEqual(PiiKind.Uri, logger.Events[3].PiiProperties["Quantum.IQSharp.ProjectUri"]);
            Assert.IsTrue(logger.Events[3].Properties["Quantum.IQSharp.ProjectUri"].ToString().Contains("Workspace.ProjectReferences.csproj"));
            Assert.AreEqual(1L, logger.Events[3].Properties["Quantum.IQSharp.SourceFileCount"]);
            Assert.AreEqual(3L, logger.Events[3].Properties["Quantum.IQSharp.ProjectReferenceCount"]);
            Assert.AreEqual(1L, logger.Events[3].Properties["Quantum.IQSharp.PackageReferenceCount"]);
            Assert.AreEqual(false, logger.Events[3].Properties["Quantum.IQSharp.UserAdded"]);

            Assert.AreEqual("Quantum.IQSharp.WorkspaceReload", logger.Events[4].Name);
            Assert.AreEqual(PiiKind.GenericData, logger.Events[4].PiiProperties["Quantum.IQSharp.Workspace"]);
            Assert.AreEqual("Workspace.ProjectReferences", logger.Events[4].Properties["Quantum.IQSharp.Workspace"]);
            Assert.AreEqual("ok", logger.Events[4].Properties["Quantum.IQSharp.Status"]);
            Assert.AreEqual("", logger.Events[4].Properties["Quantum.IQSharp.Errors"]);
            Assert.AreEqual(3L, logger.Events[4].Properties["Quantum.IQSharp.FileCount"]);
            Assert.AreEqual(3L, logger.Events[4].Properties["Quantum.IQSharp.ProjectCount"]);
        }

        [TestMethod]
        public void JupyterActions()
        {
            var workspace = "Workspace";
            var services = Startup.CreateServiceProvider(workspace);
            var logger = GetAppLogger(services);

            var performanceMonitor = services.GetService<IPerformanceMonitor>();

            // Disable background logging so as to enable a determinstic test.
            // performanceMonitor.EnableBackgroundReporting = false;

            var engine = Init(services);
            var channel = new MockChannel();

            logger.Events.Clear();
            Assert.AreEqual(0, logger.Events.Count);

            engine.ExecuteMundane(SNIPPETS.HelloQ, channel).Wait();
            logger.AssertEventCount(
                1,
                evt =>
                {
                    Assert.AreEqual("Quantum.IQSharp.Compile", evt.Name);
                    Assert.AreEqual("ok", evt.Properties["Quantum.IQSharp.Status"]);
                    Assert.AreEqual("", evt.Properties["Quantum.IQSharp.Errors"]);
                    AssertDuration(evt);
                }
            );

            logger.Events.Clear();
            engine.Execute("%simulate HelloQ", channel).Wait();
            // We expect both an Action and a SimulatorPerformance event from
            // running %simulate.
            logger.AssertEventCount(
                2,
                evt =>
                {
                    Assert.AreEqual("Quantum.IQSharp.SimulatorPerformance", evt.Name);
                    Assert.AreEqual(typeof(QuantumSimulator).FullName, evt.Properties["Quantum.IQSharp.SimulatorName"]);
                    Assert.AreEqual(0L, evt.Properties["Quantum.IQSharp.NQubits"]);
                    AssertDuration(evt);
                },
                // Since the Quantum.IQSharp.Action event is only raised when a
                // magic command completes, we expect for that event to come
                // after the simulator performance event.
                evt =>
                {
                    Assert.AreEqual("Quantum.IQSharp.Action", evt.Name);
                    Assert.AreEqual("%simulate", evt.Properties["Quantum.IQSharp.Command"]);
                    Assert.AreEqual("Ok", evt.Properties["Quantum.IQSharp.Status"]);
                    AssertDuration(evt);
                }
            );

            logger.Events.Clear();
            engine.Execute("%package Microsoft.Quantum.Standard", channel).Wait();
            logger.AssertEventCount(2,
                evt =>
                {
                    Assert.AreEqual("Quantum.IQSharp.PackageLoad", evt.Name);
                    Assert.AreEqual("Microsoft.Quantum.Standard", evt.Properties["Quantum.IQSharp.PackageId"]);
                    Assert.IsTrue(!string.IsNullOrWhiteSpace(evt.Properties["Quantum.IQSharp.PackageVersion"]?.ToString()));
                    AssertDuration(evt);
                },
                evt =>
                {
                    Assert.AreEqual("Quantum.IQSharp.Action", evt.Name);
                    Assert.AreEqual("%package", evt.Properties["Quantum.IQSharp.Command"]);
                    Assert.AreEqual("Ok", evt.Properties["Quantum.IQSharp.Status"]);
                    AssertDuration(evt);
                }
            );
        }

        private static void AssertDuration(EventProperties evt)
        {
            // NB: Not testing the value of Duration, since that is
            //     non-determinstic. We just need to make sure that
            //     it's there and is greater than zero.
            Assert.IsTrue(evt.Properties.ContainsKey("Quantum.IQSharp.Duration"));
            Assert.IsNotNull(evt.Properties["Quantum.IQSharp.Duration"]);
            Assert.IsTrue(TimeSpan.Parse(evt.Properties["Quantum.IQSharp.Duration"]?.ToString() ?? "") > TimeSpan.Zero, "Duration must be > 0");
        }

        [TestMethod]
        public void ContextVariables()
        {
            var dict = new Dictionary<string, string> 
            {
                { "UserAgent", "TestUserAgent" },
                { "HostingEnvironment", "TestHostingEnvironment" }
            };

            Program.Configuration ??= new ConfigurationBuilder()
                .AddInMemoryCollection(dict)
                .Build();

            var workspace = "Workspace";
            var services = Startup.CreateServiceProvider(workspace);
            var logger = GetAppLogger(services);

            logger.Context.TryGetValue("AppInfo.Id", out var value);
            Assert.AreEqual("iq#", value);

            logger.Context.TryGetValue("AppInfo.Version", out value);
            Assert.AreEqual(Constants.IQSharpKernelProperties.KernelVersion, value);

            logger.Context.TryGetValue("Quantum.IQSharp.CompilerVersion", out value);
            Assert.AreEqual(typeof(CompilationUnitManager).Assembly.GetName().Version.ToString(), value);

            logger.Context.TryGetValue("Quantum.IQSharp.SimulationVersion", out value);
            Assert.AreEqual(typeof(QuantumSimulator).Assembly.GetName().Version.ToString(), value);

            logger.Context.TryGetValue("Quantum.IQSharp.Root", out value);
            Assert.AreEqual(Path.GetFileName(Directory.GetCurrentDirectory()), value);

            logger.Context.TryGetValue("Quantum.IQSharp.DeviceId", out value);
            Assert.AreEqual(TelemetryService.GetDeviceId(), value);

            logger.Context.TryGetValue("Quantum.IQSharp.UserAgent", out value);
            Assert.AreEqual(Program.Configuration["UserAgent"], value);

            logger.Context.TryGetValue("Quantum.IQSharp.HostingEnvironment", out value);
            Assert.AreEqual(Program.Configuration["HostingEnvironment"], value);
        }
    }

    public class MockTelemetryService : TelemetryService
    {
        public class MockAppLogger : Microsoft.Applications.Events.ILogger
        {
            public List<EventProperties> Events { get; } = new List<EventProperties>();
            public Dictionary<string, object> Context { get; } = new Dictionary<string, object>();

            public EVTStatus LogEvent(EventProperties properties)
            {
                Events.Add(properties);
                return EVTStatus.OK;
            }

            public Task<SendResult> LogEventAsync(EventProperties properties)
            {
                Events.Add(properties);
                return Task.FromResult(new SendResult(ResultStatus.Send));
            }

            public EVTStatus SetContext(string name, string value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }

            public EVTStatus SetContext(string name, double value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }

            public EVTStatus SetContext(string name, long value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }

            public EVTStatus SetContext(string name, sbyte value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }

            public EVTStatus SetContext(string name, short value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }

            public EVTStatus SetContext(string name, int value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }

            public EVTStatus SetContext(string name, byte value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }

            public EVTStatus SetContext(string name, ushort value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }

            public EVTStatus SetContext(string name, uint value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }

            public EVTStatus SetContext(string name, bool value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }

            public EVTStatus SetContext(string name, DateTime value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }

            public EVTStatus SetContext(string name, Guid value, PiiKind piiKind = PiiKind.None)
            {
                Context[name] = value;
                return EVTStatus.OK;
            }
        }

        public MockTelemetryService(ILogger<TelemetryService> logger, IEventService eventService)
            : base(logger, eventService)
        {
        }

        public override Microsoft.Applications.Events.ILogger CreateLogManager(IConfiguration config)
        {
            return new MockAppLogger();
        }
    }
}

#endif
