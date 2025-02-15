﻿namespace SlimMessageBus.Host.Hybrid
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using SlimMessageBus.Host.Collections;
    using SlimMessageBus.Host.Config;

    public class HybridMessageBus : IMessageBus, IConsumerControl, IAsyncDisposable
    {
        private readonly ILogger _logger;

        public ILoggerFactory LoggerFactory { get; }

        public MessageBusSettings Settings { get; }
        public HybridMessageBusSettings ProviderSettings { get; }

        private readonly ProducerByMessageTypeCache<string> _busNameByMessageType;
        private readonly IDictionary<string, MessageBusBase> _busByName;

        public HybridMessageBus(MessageBusSettings settings, HybridMessageBusSettings providerSettings)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            ProviderSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));

            // Use the configured logger factory, if not provided try to resolve from DI, if also not available supress logging using the NullLoggerFactory
            LoggerFactory = settings.LoggerFactory
                ?? (ILoggerFactory)settings.DependencyResolver?.Resolve(typeof(ILoggerFactory))
                ?? NullLoggerFactory.Instance;

            _logger = LoggerFactory.CreateLogger<HybridMessageBus>();

            var busNameByBaseMessageType = new Dictionary<Type, string>();
            _busNameByMessageType = new ProducerByMessageTypeCache<string>(_logger, busNameByBaseMessageType);

            _busByName = new Dictionary<string, MessageBusBase>();
            foreach (var name in providerSettings.Keys)
            {
                var builderFunc = providerSettings[name];

                var bus = BuildBus(builderFunc);
                _busByName.Add(name, bus);

                // Register producer routes based on MessageType
                foreach (var producer in bus.Settings.Producers)
                {
                    busNameByBaseMessageType.Add(producer.MessageType, name);
                }
            }

            // ToDo: defer start of busses until here
        }

        protected virtual MessageBusBase BuildBus(Action<MessageBusBuilder> builderFunc)
        {
            var builder = MessageBusBuilder.Create();
            builder.MergeFrom(Settings);
            builderFunc(builder);

            var bus = builder.Build();

            return (MessageBusBase)bus;
        }

        public async Task Start()
        {
            foreach (var bus in _busByName.Values)
            {
                await bus.Start();
            }
        }

        public async Task Stop()
        {
            foreach (var bus in _busByName.Values)
            {
                await bus.Stop();
            }
        }

        #region Implementation of IDisposable and IAsyncDisposable

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeAsyncCore().ConfigureAwait(false).GetAwaiter().GetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore().ConfigureAwait(false);
            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Stops the consumers and disposes of internal bus objects.
        /// </summary>
        /// <returns></returns>
        protected virtual async ValueTask DisposeAsyncCore()
        {
            foreach (var (name, bus) in _busByName)
            {
                await ((IAsyncDisposable)bus).DisposeSilently(() => $"Error disposing bus: {name}", _logger);
            }
            _busByName.Clear();
        }

        #endregion

        protected virtual IMessageBus Route(object message, string path)
        {
            var messageType = message.GetType();

            var busName = _busNameByMessageType.GetProducer(messageType)
                ?? throw new ConfigurationMessageBusException($"Could not find any bus that produces the message type: {messageType} and path: {path}");

            _logger.LogDebug("Resolved bus {BusName} for message type: {MessageType} and path {Path}", busName, messageType, path);

            return _busByName[busName];
        }

        #region Implementation of IRequestResponseBus

        public Task<TResponseMessage> Send<TResponseMessage>(IRequestMessage<TResponseMessage> request, CancellationToken cancellationToken)
        {
            var bus = Route(request, null);
            return bus.Send(request, cancellationToken);
        }

        public Task<TResponseMessage> Send<TResponseMessage, TRequestMessage>(TRequestMessage request, CancellationToken cancellationToken)
        {
            var bus = Route(request, null);
            return bus.Send<TResponseMessage, TRequestMessage>(request, cancellationToken);
        }

        public Task<TResponseMessage> Send<TResponseMessage>(IRequestMessage<TResponseMessage> request, string path = null, IDictionary<string, object> headers = null, CancellationToken cancellationToken = default)
        {
            var bus = Route(request, path);
            return bus.Send(request, path, headers, cancellationToken);
        }

        public Task<TResponseMessage> Send<TResponseMessage, TRequestMessage>(TRequestMessage request, string path = null, IDictionary<string, object> headers = null, CancellationToken cancellationToken = default)
        {
            var bus = Route(request, path);
            return bus.Send<TResponseMessage, TRequestMessage>(request, path, headers, cancellationToken);
        }

        public Task<TResponseMessage> Send<TResponseMessage>(IRequestMessage<TResponseMessage> request, TimeSpan timeout, string path = null, IDictionary<string, object> headers = null, CancellationToken cancellationToken = default)
        {
            var bus = Route(request, path);
            return bus.Send(request, timeout, path, headers, cancellationToken);
        }

        #endregion

        #region Implementation of IPublishBus

        public Task Publish<TMessage>(TMessage message, string path = null, IDictionary<string, object> headers = null)
        {
            var bus = Route(message, path);
            return bus.Publish(message, path, headers);
        }

        #endregion
    }
}
