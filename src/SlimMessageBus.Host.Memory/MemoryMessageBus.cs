﻿namespace SlimMessageBus.Host.Memory
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using SlimMessageBus.Host.Config;
    using SlimMessageBus.Host.Serialization;

    /// <summary>
    /// In-memory message bus <see cref="IMessageBus"/> implementation to use for in process message passing.
    /// </summary>
    public class MemoryMessageBus : MessageBusBase
    {
        private readonly ILogger _logger;
        private IDictionary<string, List<ConsumerSettings>> _consumersByPath;
        private readonly IMessageSerializer _serializer;

        private MemoryMessageBusSettings ProviderSettings { get; }

        public override IMessageSerializer Serializer => _serializer;

        public MemoryMessageBus(MessageBusSettings settings, MemoryMessageBusSettings providerSettings) : base(settings)
        {
            _logger = LoggerFactory.CreateLogger<MemoryMessageBus>();
            ProviderSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));

            OnBuildProvider();

            _serializer = ProviderSettings.EnableMessageSerialization
                ? Settings.Serializer
                : new NullMessageSerializer();
        }

        #region Overrides of MessageBusBase

        protected override void AssertSerializerSettings()
        {
            if (ProviderSettings.EnableMessageSerialization)
            {
                base.AssertSerializerSettings();
            }
        }

        protected override void Build()
        {
            base.Build();

            _consumersByPath = Settings.Consumers
                .GroupBy(x => x.Path)
                .ToDictionary(x => x.Key, x => x.ToList());
        }

        public override async Task ProduceToTransport(Type messageType, object message, string path, byte[] messagePayload, IDictionary<string, object> messageHeaders = null)
        {
            if (!_consumersByPath.TryGetValue(path, out var consumers) || consumers.Count == 0)
            {
                _logger.LogDebug("No consumers interested in message type {MessageType} on path {Path}", messageType, path);
                return;
            }

            foreach (var consumer in consumers)
            {
                _logger.LogDebug("Executing consumer {ConsumerType} on {Message}...", consumer.ConsumerType, message);
                await OnMessageProduced(messageType, message, path, messagePayload, messageHeaders, consumer);
                _logger.LogTrace("Executed consumer {ConsumerType}", consumer.ConsumerType);
            }
        }

        private async Task OnMessageProduced(Type messageType, object message, string path, byte[] messagePayload, IDictionary<string, object> messageHeaders, ConsumerSettings consumer)
        {
            // ToDo: Extension: In case of IMessageBus.Publish do not wait for the consumer method see https://github.com/zarusz/SlimMessageBus/issues/37

            string responseError = null;
            object response = null;

            try
            {
                // will pass a deep copy of the message (if serialization enabled) or the original message if serialization not enabled
                var messageForConsumer = Serializer.Deserialize(messageType, messagePayload) ?? message;

                response = await ExecuteConsumer(messageForConsumer, consumer).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error occured while executing {ConsumerType} on {Message}", consumer.ConsumerType, message);

                try
                {
                    // Invoke fault handler.
                    (consumer.OnMessageFault ?? Settings.OnMessageFault)?.Invoke(this, consumer, message, e, null);
                }
                catch (Exception hookEx)
                {
                    HookFailed(_logger, hookEx, nameof(consumer.OnMessageFault));
                }

                if (consumer.ConsumerMode == ConsumerMode.RequestResponse)
                {
                    // When in request response mode then pass the error back to the sender
                    responseError = e.Message;
                }
                else
                {
                    // Otherwise rethrow the error
                    throw;
                }
            }

            if (consumer.ConsumerMode == ConsumerMode.RequestResponse)
            {
                if (messageHeaders == null || !messageHeaders.TryGetHeader(ReqRespMessageHeaders.RequestId, out string requestId))
                {
                    throw new MessageBusException($"The message header {ReqRespMessageHeaders.RequestId} was not present at this time");
                }

                // will be null when serialization is not enabled
                var responsePayload = Serializer.Serialize(consumer.ResponseType, response);

                await OnResponseArrived(responsePayload, path, requestId, responseError, response).ConfigureAwait(false);
            }
        }

        private async Task<object> ExecuteConsumer(object message, ConsumerSettings consumerSettings)
        {
            using var messageScope = GetMessageScope(consumerSettings, message);

            // obtain the consumer from chosen DI container (root or scope)
            _logger.LogDebug("Resolving consumer type {ConsumerType}", consumerSettings.ConsumerType);
            var consumerInstance = messageScope.Resolve(consumerSettings.ConsumerType)
                ?? throw new ConfigurationMessageBusException($"The dependency resolver does not know how to create an instance of {consumerSettings.ConsumerType}");

            try
            {
                _logger.LogDebug("Executing consumer instance {Consumer} of type {ConsumerType} for message {Message}", consumerInstance, consumerSettings.ConsumerType, message);
                var consumerTask = consumerSettings.ConsumerMethod(consumerInstance, message, consumerSettings.Path);

                await consumerTask.ConfigureAwait(false);

                if (consumerSettings.ConsumerMode == ConsumerMode.RequestResponse)
                {
                    var response = consumerSettings.ConsumerMethodResult(consumerTask);
                    _logger.LogDebug("Consumer instance {Consumer} of type {ConsumerType} for message {Message} has response {Response}", consumerInstance, consumerSettings.ConsumerType, message, response);
                    return response;
                }
                return null;
            }
            finally
            {
                if (consumerSettings.IsDisposeConsumerEnabled && consumerInstance is IDisposable consumerInstanceDisposable)
                {
                    _logger.LogDebug("Dosposing consumer instance {Consumer} of type {ConsumerType}", consumerInstance, consumerSettings.ConsumerType);
                    consumerInstanceDisposable.DisposeSilently("ConsumerInstance", _logger);
                }
            }
        }

        #endregion

        public override bool IsMessageScopeEnabled(ConsumerSettings consumerSettings)
            => (consumerSettings ?? throw new ArgumentNullException(nameof(consumerSettings))).IsMessageScopeEnabled ?? Settings.IsMessageScopeEnabled ?? false; // by default Memory Bus has scoped message disabled
    }
}
