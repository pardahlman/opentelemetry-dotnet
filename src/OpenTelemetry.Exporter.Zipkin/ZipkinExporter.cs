// <copyright file="ZipkinExporter.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
#if NET452
using Newtonsoft.Json;
#else
using System.Text.Json;
#endif
using System.Threading;
using System.Threading.Tasks;
using OpenTelemetry.Exporter.Zipkin.Implementation;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Exporter.Zipkin
{
    /// <summary>
    /// Zipkin exporter.
    /// </summary>
    public class ZipkinExporter : ActivityExporter
    {
        private readonly ZipkinExporterOptions options;
#if !NET452
        private readonly int maxPayloadSizeInBytes;
#endif
        private readonly HttpClient httpClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="ZipkinExporter"/> class.
        /// </summary>
        /// <param name="options">Configuration options.</param>
        /// <param name="client">Http client to use to upload telemetry.</param>
        internal ZipkinExporter(ZipkinExporterOptions options, HttpClient client = null)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
#if !NET452
            this.maxPayloadSizeInBytes = (!options.MaxPayloadSizeInBytes.HasValue || options.MaxPayloadSizeInBytes <= 0) ? ZipkinExporterOptions.DefaultMaxPayloadSizeInBytes : options.MaxPayloadSizeInBytes.Value;
#endif
            this.LocalEndpoint = this.GetLocalZipkinEndpoint();
            this.httpClient = client ?? new HttpClient();
        }

        internal ZipkinEndpoint LocalEndpoint { get; }

        /// <inheritdoc/>
        public override ExportResult Export(in Batch<Activity> batch)
        {
            // Prevent Zipkin's HTTP operations from being instrumented.
            using var scope = SuppressInstrumentationScope.Begin();

            try
            {
                var requestUri = this.options.Endpoint;

                using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
                {
                    Content = new JsonContent(this, batch),
                };

                using var response = this.httpClient.SendAsync(request, CancellationToken.None).GetAwaiter().GetResult();

                response.EnsureSuccessStatusCode();

                return ExportResult.Success;
            }
            catch (Exception ex)
            {
                ZipkinExporterEventSource.Log.FailedExport(ex);

                return ExportResult.Failure;
            }
        }

        private static string ResolveHostAddress(string hostName, AddressFamily family)
        {
            string result = null;

            try
            {
                var results = Dns.GetHostAddresses(hostName);

                if (results != null && results.Length > 0)
                {
                    foreach (var addr in results)
                    {
                        if (addr.AddressFamily.Equals(family))
                        {
                            var sanitizedAddress = new IPAddress(addr.GetAddressBytes()); // Construct address sans ScopeID
                            result = sanitizedAddress.ToString();

                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore
            }

            return result;
        }

        private static string ResolveHostName()
        {
            string result = null;

            try
            {
                result = Dns.GetHostName();

                if (!string.IsNullOrEmpty(result))
                {
                    var response = Dns.GetHostEntry(result);

                    if (response != null)
                    {
                        return response.HostName;
                    }
                }
            }
            catch (Exception)
            {
                // Ignore
            }

            return result;
        }

        private ZipkinEndpoint GetLocalZipkinEndpoint()
        {
            var hostName = ResolveHostName();

            string ipv4 = null;
            string ipv6 = null;
            if (!string.IsNullOrEmpty(hostName))
            {
                ipv4 = ResolveHostAddress(hostName, AddressFamily.InterNetwork);
                ipv6 = ResolveHostAddress(hostName, AddressFamily.InterNetworkV6);
            }

            return new ZipkinEndpoint(
                this.options.ServiceName,
                ipv4,
                ipv6,
                null);
        }

        private class JsonContent : HttpContent
        {
            private static readonly MediaTypeHeaderValue JsonHeader = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };

            private readonly ZipkinExporter exporter;
            private readonly Batch<Activity> batch;

#if NET452
            private JsonWriter writer;
#else
            private Utf8JsonWriter writer;
#endif

            public JsonContent(ZipkinExporter exporter, in Batch<Activity> batch)
            {
                this.exporter = exporter;
                this.batch = batch;

                this.Headers.ContentType = JsonHeader;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
#if NET452
                StreamWriter streamWriter = new StreamWriter(stream);
                this.writer = new JsonTextWriter(streamWriter);
#else
                if (this.writer == null)
                {
                    this.writer = new Utf8JsonWriter(stream);
                }
                else
                {
                    this.writer.Reset(stream);
                }
#endif

                this.writer.WriteStartArray();

                foreach (var activity in this.batch)
                {
                    var zipkinSpan = activity.ToZipkinSpan(this.exporter.LocalEndpoint, this.exporter.options.UseShortTraceIds);

                    zipkinSpan.Write(this.writer);

                    zipkinSpan.Return();
#if !NET452
                    if (this.writer.BytesPending >= this.exporter.maxPayloadSizeInBytes)
                    {
                        this.writer.Flush();
                    }
#endif
                }

                this.writer.WriteEndArray();

                this.writer.Flush();

#if NET452
                return Task.FromResult(true);
#else
                return Task.CompletedTask;
#endif
            }

            protected override bool TryComputeLength(out long length)
            {
                // We can't know the length of the content being pushed to the output stream.
                length = -1;
                return false;
            }
        }
    }
}
