// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Ignitor;
using Microsoft.AspNetCore.Components.E2ETest.Infrastructure.ServerFixtures;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Microsoft.AspNetCore.Components.E2ETest.ServerExecutionTests
{
    public class ComponentHubReliabilityTest : IClassFixture<AspNetSiteServerFixture>, IDisposable
    {
        private static readonly TimeSpan DefaultLatencyTimeout = TimeSpan.FromMilliseconds(500);
        private readonly AspNetSiteServerFixture _serverFixture;

        public ComponentHubReliabilityTest(AspNetSiteServerFixture serverFixture)
        {
            serverFixture.BuildWebHostMethod = TestServer.Program.BuildWebHost;
            _serverFixture = serverFixture;
            CreateDefaultConfiguration();
        }

        public BlazorClient Client { get; set; }

        private IList<Batch> Batches { get; set; } = new List<Batch>();
        private IList<string> Errors { get; set; } = new List<string>();
        private IList<LogMessage> Logs { get; set; } = new List<LogMessage>();

        public TestSink TestSink { get; set; }

        private void CreateDefaultConfiguration()
        {
            Client = new BlazorClient() { DefaultLatencyTimeout = DefaultLatencyTimeout };
            Client.RenderBatchReceived += (id, rendererId, data) => Batches.Add(new Batch(id, rendererId, data));
            Client.OnCircuitError += (error) => Errors.Add(error);

            _  = _serverFixture.RootUri; // this is needed for the side-effects of getting the URI.
            TestSink = _serverFixture.Host.Services.GetRequiredService<TestSink>();
            TestSink.MessageLogged += LogMessages;
        }

        private void LogMessages(WriteContext context) => Logs.Add(new LogMessage(context.LogLevel, context.Message, context.Exception));

        [Fact]
        public async Task CannotStartMultipleCircuits()
        {
            // Arrange
            var expectedError = "The circuit host '.*?' has already been initialized.";
            var rootUri = _serverFixture.RootUri;
            var baseUri = new Uri(rootUri, "/subdir");
            Assert.True(await Client.ConnectAsync(baseUri, prerendered: false), "Couldn't connect to the app");
            Assert.Single(Batches);

            // Act
            await Client.ExpectCircuitErrorAndDisconnect(() => Client.HubConnection.SendAsync(
                "StartCircuit",
                baseUri,
                baseUri + "/home"));

            // Assert
            var actualError = Assert.Single(Errors);
            Assert.Matches(expectedError, actualError);
            Assert.DoesNotContain(Logs, l => l.LogLevel > LogLevel.Information);
        }

        [Fact]
        public async Task CannotStartCircuitWithNullData()
        {
            // Arrange
            var expectedError = "The uris provided are invalid.";
            var rootUri = _serverFixture.RootUri;
            var uri = new Uri(rootUri, "/subdir");
            Assert.True(await Client.ConnectAsync(uri, prerendered: false, connectAutomatically: false), "Couldn't connect to the app");

            // Act
            await Client.ExpectCircuitErrorAndDisconnect(() => Client.HubConnection.SendAsync("StartCircuit", null, null));

            // Assert
            var actualError = Assert.Single(Errors);
            Assert.Matches(expectedError, actualError);
            Assert.DoesNotContain(Logs, l => l.LogLevel > LogLevel.Information);
        }

        // This is a hand-chosen example of something that will cause an exception in creating the circuit host.
        // We want to test this case so that we know what happens when creating the circuit host blows up.
        [Fact]
        public async Task StartCircuitCausesInitializationError()
        {
            // Arrange
            var expectedError = "The circuit failed to initialize.";
            var rootUri = _serverFixture.RootUri;
            var uri = new Uri(rootUri, "/subdir");
            Assert.True(await Client.ConnectAsync(uri, prerendered: false, connectAutomatically: false), "Couldn't connect to the app");

            // Act
            //
            // These are valid URIs by the BaseUri doesn't contain the Uri - so it fails to initialize.
            await Client.ExpectCircuitErrorAndDisconnect(() => Client.HubConnection.SendAsync("StartCircuit", uri, "http://example.com"), TimeSpan.FromHours(1));

            // Assert
            var actualError = Assert.Single(Errors);
            Assert.Matches(expectedError, actualError);
            Assert.DoesNotContain(Logs, l => l.LogLevel > LogLevel.Information);
        }

        [Fact]
        public async Task CannotInvokeJSInteropBeforeInitialization()
        {
            // Arrange
            var expectedError = "Circuit not initialized.";
            var rootUri = _serverFixture.RootUri;
            var baseUri = new Uri(rootUri, "/subdir");
            Assert.True(await Client.ConnectAsync(baseUri, prerendered: false, connectAutomatically: false));
            Assert.Empty(Batches);

            // Act
            await Client.ExpectCircuitErrorAndDisconnect(() => Client.HubConnection.SendAsync(
                "BeginInvokeDotNetFromJS",
                "",
                "",
                "",
                0,
                ""));

            // Assert
            var actualError = Assert.Single(Errors);
            Assert.Equal(expectedError, actualError);
            Assert.DoesNotContain(Logs, l => l.LogLevel > LogLevel.Information);
            Assert.Contains(Logs, l => (l.LogLevel, l.Message) == (LogLevel.Debug, "Call to 'BeginInvokeDotNetFromJS' received before the circuit host initialization"));
        }

        [Fact]
        public async Task CannotInvokeJSInteropCallbackCompletionsBeforeInitialization()
        {
            // Arrange
            var expectedError = "Circuit not initialized.";
            var rootUri = _serverFixture.RootUri;
            var baseUri = new Uri(rootUri, "/subdir");
            Assert.True(await Client.ConnectAsync(baseUri, prerendered: false, connectAutomatically: false));
            Assert.Empty(Batches);

            // Act
            await Client.ExpectCircuitErrorAndDisconnect(() => Client.HubConnection.SendAsync(
                "EndInvokeJSFromDotNet",
                3,
                true,
                "[]"));

            // Assert
            var actualError = Assert.Single(Errors);
            Assert.Equal(expectedError, actualError);
            Assert.DoesNotContain(Logs, l => l.LogLevel > LogLevel.Information);
            Assert.Contains(Logs, l => (l.LogLevel, l.Message) == (LogLevel.Debug, "Call to 'EndInvokeJSFromDotNet' received before the circuit host initialization"));
        }

        [Fact]
        public async Task CannotDispatchBrowserEventsBeforeInitialization()
        {
            // Arrange
            var expectedError = "Circuit not initialized.";
            var rootUri = _serverFixture.RootUri;
            var baseUri = new Uri(rootUri, "/subdir");
            Assert.True(await Client.ConnectAsync(baseUri, prerendered: false, connectAutomatically: false));
            Assert.Empty(Batches);

            // Act
            await Client.ExpectCircuitErrorAndDisconnect(() => Client.HubConnection.SendAsync(
                "DispatchBrowserEvent",
                "",
                ""));

            // Assert
            var actualError = Assert.Single(Errors);
            Assert.Equal(expectedError, actualError);
            Assert.DoesNotContain(Logs, l => l.LogLevel > LogLevel.Information);
            Assert.Contains(Logs, l => (l.LogLevel, l.Message) == (LogLevel.Debug, "Call to 'DispatchBrowserEvent' received before the circuit host initialization"));
        }

        [Fact]
        public async Task CannotInvokeOnRenderCompletedBeforeInitialization()
        {
            // Arrange
            var expectedError = "Circuit not initialized.";
            var rootUri = _serverFixture.RootUri;
            var baseUri = new Uri(rootUri, "/subdir");
            Assert.True(await Client.ConnectAsync(baseUri, prerendered: false, connectAutomatically: false));
            Assert.Empty(Batches);

            // Act
            await Client.ExpectCircuitErrorAndDisconnect(() => Client.HubConnection.SendAsync(
                "OnRenderCompleted",
                5,
                null));

            // Assert
            var actualError = Assert.Single(Errors);
            Assert.Equal(expectedError, actualError);
            Assert.DoesNotContain(Logs, l => l.LogLevel > LogLevel.Information);
            Assert.Contains(Logs, l => (l.LogLevel, l.Message) == (LogLevel.Debug, "Call to 'OnRenderCompleted' received before the circuit host initialization"));
        }

        [Fact]
        public async Task CannotInvokeOnLocationChangedBeforeInitialization()
        {
            // Arrange
            var expectedError = "Circuit not initialized.";
            var rootUri = _serverFixture.RootUri;
            var baseUri = new Uri(rootUri, "/subdir");
            Assert.True(await Client.ConnectAsync(baseUri, prerendered: false, connectAutomatically: false));
            Assert.Empty(Batches);

            // Act
            await Client.ExpectCircuitErrorAndDisconnect(() => Client.HubConnection.SendAsync(
                "OnLocationChanged",
                baseUri.AbsoluteUri,
                false));

            // Assert
            var actualError = Assert.Single(Errors);
            Assert.Equal(expectedError, actualError);
            Assert.DoesNotContain(Logs, l => l.LogLevel > LogLevel.Information);
            Assert.Contains(Logs, l => (l.LogLevel, l.Message) == (LogLevel.Debug, "Call to 'OnLocationChanged' received before the circuit host initialization"));
        }

        [Fact]
        public async Task OnLocationChanged_ReportsDebugForExceptionInValidation()
        {
            // Arrange
            var expectedError = "Location change to http://example.com failed.";
            var rootUri = _serverFixture.RootUri;
            var baseUri = new Uri(rootUri, "/subdir");
            Assert.True(await Client.ConnectAsync(baseUri, prerendered: false), "Couldn't connect to the app");
            Assert.Single(Batches);

            // Act
            await Client.ExpectCircuitError(() => Client.HubConnection.SendAsync(
                "OnLocationChanged",
                "http://example.com",
                false));

            // Assert
            var actualError = Assert.Single(Errors);
            Assert.Equal(expectedError, actualError);
            Assert.DoesNotContain(Logs, l => l.LogLevel > LogLevel.Information);
            Assert.Contains(Logs, l =>
            {
                return l.LogLevel == LogLevel.Debug && Regex.IsMatch(l.Message, "Location change to http://example.com in circuit .* failed.");
            });
        }

        [Fact]
        public async Task OnLocationChanged_ReportsErrorForExceptionInUserCode()
        {
            // Arrange
            var expectedError = "There was an unhandled exception .?";
            var rootUri = _serverFixture.RootUri;
            var baseUri = new Uri(rootUri, "/subdir");
            Assert.True(await Client.ConnectAsync(baseUri, prerendered: false), "Couldn't connect to the app");
            Assert.Single(Batches);

            await Client.SelectAsync("test-selector-select", "BasicTestApp.NavigationFailureComponent");

            // Act
            await Client.ExpectCircuitError(() => Client.HubConnection.SendAsync(
                "OnLocationChanged",
                new Uri(baseUri, "/test").AbsoluteUri,
                false)); 

            // Assert
            var actualError = Assert.Single(Errors);
            Assert.Matches(expectedError, actualError);
            Assert.Contains(Logs, l =>
            {
                return l.LogLevel == LogLevel.Error && Regex.IsMatch(l.Message, "Unhandled exception in circuit .*");
            });
        }

        [Theory]
        [InlineData("constructor-throw")]
        [InlineData("attach-throw")]
        [InlineData("setparameters-sync-throw")]
        [InlineData("setparameters-async-throw")]
        [InlineData("render-throw")]
        [InlineData("afterrender-sync-throw")]
        [InlineData("afterrender-async-throw")]
        [InlineData("dispose-throw")]
        public async Task ComponentLifecycleMethodThrowsExceptionTerminatesTheCircuit(string id)
        {
            // Arrange
            var expectedError = "Unhandled exception in circuit .*";
            var rootUri = _serverFixture.RootUri;
            var baseUri = new Uri(rootUri, "/subdir");
            Assert.True(await Client.ConnectAsync(baseUri, prerendered: false), "Couldn't connect to the app");
            Assert.Single(Batches);

            await Client.SelectAsync("test-selector-select", "BasicTestApp.ReliabilityComponent");

            // Act
            await Client.ExpectCircuitError(async () =>
            {
                // We want to click the element, but don't expect a DOM update.
                Assert.True(Client.Hive.TryFindElementById(id, out var elementNode), "failed to find element");
                await elementNode.ClickAsync(Client.HubConnection);
            });

            Assert.Contains(
                Logs,
                e => LogLevel.Error == e.LogLevel && Regex.IsMatch(e.Message, expectedError));

            // Now if you try to click again, you will get *forcibly* disconnected for trying to talk to
            // a circuit that's gone.
            await Client.ExpectCircuitErrorAndDisconnect(async () =>
            {
                Assert.True(Client.Hive.TryFindElementById(id, out var elementNode), "failed to find element");
                await Assert.ThrowsAsync<TaskCanceledException>(async () => await elementNode.ClickAsync(Client.HubConnection));
            });
        }

        public void Dispose()
        {
            TestSink.MessageLogged -= LogMessages;
        }

        private class LogMessage
        {
            public LogMessage(LogLevel logLevel, string message, Exception exception)
            {
                LogLevel = logLevel;
                Message = message;
                Exception = exception;
            }

            public LogLevel LogLevel { get; set; }
            public string Message { get; set; }
            public Exception Exception { get; set; }
        }

        private class Batch
        {
            public Batch(int id, int rendererId, byte [] data)
            {
                Id = id;
                RendererId = rendererId;
                Data = data;
            }

            public int Id { get; }
            public int RendererId { get; }
            public byte[] Data { get; }
        }
    }
}
