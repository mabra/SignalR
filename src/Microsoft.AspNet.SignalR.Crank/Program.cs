﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.md in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Client;
using Microsoft.AspNet.SignalR.Client.Transports;
using Microsoft.AspNet.SignalR.Client.Http;
using CmdLine;

namespace Microsoft.AspNet.SignalR.Crank
{

    class Program
    {
        private static volatile bool _running = true;
        private static readonly SemaphoreSlim _batchLock = new SemaphoreSlim(1);
        private static PerformanceCounter[] _counters;
        private static Dictionary<PerformanceCounter, List<CounterSample>> _samples;

        static void Main(string[] args)
        {
            var arguments = ParseArguments();

            ServicePointManager.DefaultConnectionLimit = Int32.MaxValue;

            // Increase the number of min threads in the threadpool
            ThreadPool.SetMinThreads(arguments.NumClients, 2);

            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            var connections = new ConcurrentBag<Connection>();
            var endTime = TimeSpan.MaxValue;
            var timeoutTime = TimeSpan.FromSeconds(arguments.Timeout);
            Stopwatch stopwatch = null;

            Task.Run(async () =>
            {
                Console.WriteLine("Ramping up connections. Batch size {0}.", arguments.BatchSize);

                stopwatch = Stopwatch.StartNew();
                await ConnectBatches(arguments.Url, arguments.Transport, arguments.NumClients, arguments.BatchSize, arguments.BatchInterval, connections);

                var rampupElapsed = stopwatch.Elapsed;
                endTime = rampupElapsed.Add(TimeSpan.FromSeconds(arguments.Duration));
                Console.WriteLine("Ramp up complete in {0}.", rampupElapsed);

            });

            while (true)
            {
                if (stopwatch != null)
                {
                    if ((stopwatch.Elapsed > endTime) || (stopwatch.Elapsed > timeoutTime))
                    {
                        _running = false;
                        break;
                    }
                    Sample(arguments, connections, stopwatch.Elapsed);
                }
                Thread.Sleep(arguments.BatchInterval);
            }

            stopwatch.Stop();

            Console.WriteLine("Total Running time: {0}", stopwatch.Elapsed);
            Record(arguments);

            Parallel.ForEach(connections, connection => connection.Stop());
        }

        private static void Mark(CrankArguments arguments, ulong value, string metric)
        {
#if PERFRUN
            Microsoft.VisualStudio.Diagnostics.Measurement.MeasurementBlock.Mark(value, String.Format("{0}-{1};{2}", arguments.Transport, arguments.NumClients, metric));
#endif
        }

        private static void Sample(CrankArguments arguments, ConcurrentBag<Connection> connections, TimeSpan elapsed)
        {
            if (connections.Count == 0)
            {
                return;
            }

            _batchLock.Wait();
            try
            {
                SampleConnections(arguments, connections, elapsed);
                SampleCounters(arguments, connections, elapsed);
            }
            finally
            {
                _batchLock.Release();
            }
        }

        private static void SampleConnections(CrankArguments arguments, ConcurrentBag<Connection> connections, TimeSpan elapsed)
        {
            var connecting = connections.Where(c => c.State == ConnectionState.Connecting).Count();
            var connected = connections.Where(c => c.State == ConnectionState.Connected).Count();
            var reconnecting = connections.Where(c => c.State == ConnectionState.Reconnecting).Count();
            var disconnected = connections.Where(c => c.State == ConnectionState.Disconnected).Count();

            Mark(arguments, (ulong)connecting, "Connections Connecting");
            Mark(arguments, (ulong)connected, "Connections Connected");
            Mark(arguments, (ulong)reconnecting, "Connections Reconnecting");
            Mark(arguments, (ulong)disconnected, "Connections Disconnected");

            var transportState = "";
            if (connections.First().Transport.GetType() == typeof(AutoTransport))
            {
                transportState = String.Format(", Transport={0}ws|{1}ss|{2}lp",
                    connections.Where(c => c.Transport.Name.Equals("webSockets", StringComparison.InvariantCultureIgnoreCase)).Count(),
                    connections.Where(c => c.Transport.Name.Equals("serverSentEvents", StringComparison.InvariantCultureIgnoreCase)).Count(),
                    connections.Where(c => c.Transport.Name.Equals("longPolling", StringComparison.InvariantCultureIgnoreCase)).Count());
            }
            Console.WriteLine(String.Format("[{0}] Connections: {1}/{2}, State={3}|{4}c|{5}r|{6}d",
                    elapsed,
                    connections.Count(),
                    arguments.NumClients,
                    connecting,
                    connected,
                    reconnecting,
                    disconnected)
                    + transportState);
        }

        private static void InitializeCounters(CrankArguments arguments)
        {
            var instance = Process.GetCurrentProcess().ProcessName;
            _counters = new[]
            {
                new PerformanceCounter("Memory", "Available MBytes", null, readOnly: true),
                new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly:true),
                new PerformanceCounter("Process", "Private Bytes", instance, readOnly:true),
                new PerformanceCounter("Process", "Virtual Bytes", instance, readOnly:true),
                new PerformanceCounter("Process", "Working Set", instance, readOnly:true),
                new PerformanceCounter("Process", "Thread Count", instance, readOnly:true),
                new PerformanceCounter(".NET CLR Memory", "% Time in GC", instance, readOnly:true),
                new PerformanceCounter(".NET CLR Memory", "Allocated Bytes/sec", instance, readOnly:true)
            };

            var serverInstance = "w3wp";
            var server = GetServerName(arguments.Url);
            if (!String.IsNullOrEmpty(server) && !String.IsNullOrEmpty(arguments.SiteName))
            {
                _counters = _counters.Concat(new[]
                {
                    new PerformanceCounter("SignalR", "Connections Connected", arguments.SiteName, machineName: server),
                    new PerformanceCounter("SignalR", "Connections Current", arguments.SiteName, machineName: server),
                    new PerformanceCounter("SignalR", "Connections Reconnected", arguments.SiteName, machineName: server),
                    new PerformanceCounter("SignalR", "Connections Disconnected", arguments.SiteName, machineName: server),
                    new PerformanceCounter("Memory", "Available MBytes", null, machineName: server),
                    new PerformanceCounter("Processor", "% Processor Time", "_Total", machineName: server),
                    new PerformanceCounter("Process", "Private Bytes", serverInstance, machineName: server),
                    new PerformanceCounter("Process", "Virtual Bytes", serverInstance, machineName: server),
                    new PerformanceCounter("Process", "Working Set", serverInstance, machineName: server),
                    new PerformanceCounter("Process", "Thread Count", serverInstance, machineName: server),
                    new PerformanceCounter(".NET CLR Memory", "% Time in GC", serverInstance, machineName: server),
                    new PerformanceCounter(".NET CLR Memory", "Allocated Bytes/sec", serverInstance, machineName: server)
                }).ToArray();
            }

            _samples = new Dictionary<PerformanceCounter, List<CounterSample>>(_counters.Length);
            Parallel.ForEach(_counters, c =>
            {
                try
                {
                    _samples[c] = new List<CounterSample> { c.NextSample() };
                }
                catch (Exception e)
                {
                    Console.WriteLine("Failed to initilize counter '{0}\\{1}({2})': {3}", c.CategoryName, c.CounterName, c.InstanceName ?? c.MachineName, e.Message);
                    throw;
                }
            });
        }

        private static void SampleCounters(CrankArguments arguments, ConcurrentBag<Connection> connections, TimeSpan elapsedd)
        {
            if (_counters == null)
            {
                InitializeCounters(arguments);
            }
            else
            {
                Parallel.ForEach(_counters, c => _samples[c].Add(c.NextSample()));
            }
        }

        private static void Record(CrankArguments arguments)
        {
            var maxConnections = 0;
            foreach (var sample in _samples)
            {
                var instance = String.IsNullOrEmpty(sample.Key.InstanceName) ? sample.Key.MachineName : sample.Key.InstanceName;
                var key = String.Format("{0}({1})", sample.Key.CounterName, instance);
                var samples = sample.Value;

                var values = new long[samples.Count - 1];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = (long)Math.Round(CounterSample.Calculate(samples[i], samples[i + 1]));
                    Mark(arguments, (ulong)values[i], key);
                }

                if (key.StartsWith("Connections Connected"))
                {
                    maxConnections = (int)values.Max();
                }

                RecordAggregates(key, values);
            }
            Console.WriteLine("Max Connections Connected: " + maxConnections);
        }

        private static void RecordAggregates(string key, long[] values)
        {
            Array.Sort(values);
            double median = values[values.Length / 2];
            if (values.Length % 2 == 0)
            {
                median = median + values[(values.Length / 2) - 1] / 2;
            }
            Console.WriteLine("{0} (MEDIAN):  {1}", key, Math.Round(median));

            var average = values.Average();
            Console.WriteLine("{0} (AVERAGE): {1}", key, Math.Round(average));

            if (average != 0)
            {
                var sumOfSquaresDiffs = values.Select(v => (v - average) * (v - average)).Sum();
                var stdDevP = Math.Sqrt(sumOfSquaresDiffs / values.Length) / average * 100;
                Console.WriteLine("{0} (STDDEV%): {1}%", key, Math.Round(stdDevP));
            }
        }

        private static string GetServerName(string url)
        {
            var match = Regex.Match(url, @"^\w+://([^/]+):\d+?/");
            if (match.Success && match.Groups.Count >= 2)
            {
                return match.Groups[1].Value;
            }
            return null;
        }

        private static async Task ConnectBatches(string url, string transport, int clients, int batchSize, int batchInterval, ConcurrentBag<Connection> connections)
        {
            while (true)
            {
                int processed = Math.Min(clients, batchSize);

                await _batchLock.WaitAsync();
                try
                {
                    await ConnectBatch(url, transport, processed, connections);
                }
                finally
                {
                    _batchLock.Release();
                }

                int remaining = clients - processed;

                if (remaining <= 0)
                {
                    break;
                }

                clients = remaining;

                await Task.Delay(batchInterval);
            }
        }

        private static Task ConnectBatch(string url, string transport, int batchSize, ConcurrentBag<Connection> connections)
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = batchSize
            };

            var batchTcs = new TaskCompletionSource<object>();

            long remaining = batchSize;
            Parallel.For(0, batchSize, options, async i =>
            {
                var connection = new Connection(url);

                if (!_running)
                {
                    batchTcs.TrySetResult(null);
                    return;
                }

                try
                {
                    var clientTransport = GetTransport(transport);
                    await (clientTransport == null ? connection.Start() : connection.Start(clientTransport));

                    if (_running)
                    {
                        connections.Add(connection);

                        var clientId = connection.ConnectionId;

                        connection.Error += e =>
                        {
                            Debug.WriteLine(String.Format("SIGNALR: Client {0} ERROR: {1}", clientId, e));
                        };

                        connection.Closed += () =>
                        {
                            Debug.WriteLine(String.Format("SIGNALR: Client {0} CLOSED", clientId));

                            // Remove it from the list on close
                            connections.TryTake(out connection);
                        };
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to start client. {0}", ex.GetBaseException());
                }
                finally
                {
                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        // When all connections are connected, mark the task as complete
                        batchTcs.TrySetResult(null);
                    }
                }
            });

            return batchTcs.Task;
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Console.WriteLine(e.Exception.GetBaseException());
            e.SetObserved();
        }

        private static IClientTransport GetTransport(string transport)
        {
            if (!String.IsNullOrEmpty(transport))
            {
                var httpClient = new DefaultHttpClient();
                if (transport.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
                {
                    return new WebSocketTransport(httpClient);
                }
                else if (transport.Equals("ServerSentEvents", StringComparison.InvariantCultureIgnoreCase))
                {
                    return new ServerSentEventsTransport(httpClient);
                }
                else if (transport.Equals("LongPolling", StringComparison.InvariantCultureIgnoreCase))
                {
                    return new LongPollingTransport(httpClient);
                }
                else if (transport.Equals("Auto", StringComparison.InvariantCultureIgnoreCase))
                {
                    return new AutoTransport(httpClient);
                }
            }
            return null;
        }

        private static CrankArguments ParseArguments()
        {
            CrankArguments args = null;
            try
            {
                args = CommandLine.Parse<CrankArguments>();
            }
            catch (CommandLineException e)
            {
                Console.WriteLine(e.ArgumentHelp.Message);
                Console.WriteLine(e.ArgumentHelp.GetHelpText(Console.BufferWidth));
                Environment.Exit(1);
            }
            return args;
        }

        [CommandLineArguments(Program = "Crank")]
        private class CrankArguments
        {
            [CommandLineParameter(Command = "?", Name = "Help", Default = false, Description = "Show Help", IsHelp = true)]
            public bool Help { get; set; }

            [CommandLineParameter(Command = "Url", Required = true, Description = "URL for SignalR connections.")]
            public string Url { get; set; }

            [CommandLineParameter(Command = "Clients", Required = true, Description = "Number of clients.")]
            public int NumClients { get; set; }

            [CommandLineParameter(Command = "BatchSize", Required = false, Default = 50, Description = "Batch size for adding connections. Default: 50")]
            public int BatchSize { get; set; }

            [CommandLineParameter(Command = "BatchInterval", Required = false, Default = 500, Description = "Batch interval in milliseconds for adding connections. Default: 500")]
            public int BatchInterval { get; set; }

            [CommandLineParameter(Command = "Transport", Required = false, Default = "auto", Description = "Transport name. Default: auto")]
            public string Transport { get; set; }

            [CommandLineParameter(Command = "Duration", Required = false, Default = 30, Description = "Duration in seconds to persist connections after warmup completes. Default: 30")]
            public int Duration { get; set; }

            [CommandLineParameter(Command = "Timeout", Required = false, Default = 300, Description = "Timeout in seconds. Default: 300")]
            public int Timeout { get; set; }

            [CommandLineParameter(Command = "SiteName", Required = false, Default = "", Description = "Site name, used as instance for SignalR counters. Defaults to not collecting server data.")]
            public string SiteName { get; set; }
        }

    }
}
