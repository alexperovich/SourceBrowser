using System;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions.Internal;

namespace SourceIndexer.Contracts
{
    partial class HostServer
    {
        public static HostServerClient Connect(string serverUrl, string clientId)
        {
            var channel = GrpcChannel.ForAddress(serverUrl);
            var intercepted = channel.Intercept(headers =>
            {
                headers.Add("X-Client-Id", clientId);
                return headers;
            });
            var client = new HostServerClient(intercepted);
            return client;
        }

        partial class HostServerClient : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
            {
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                WriteLog(new LogMessage
                {
                    LogLevel = (int) logLevel,
                    Message = formatter(state, exception),
                    ExceptionInfo = exception.ToString(),
                });
            }
        }
    }
}