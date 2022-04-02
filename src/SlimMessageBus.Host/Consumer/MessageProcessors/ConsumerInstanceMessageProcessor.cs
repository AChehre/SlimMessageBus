﻿namespace SlimMessageBus.Host
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using SlimMessageBus.Host.Collections;
    using SlimMessageBus.Host.Config;

    /// <summary>
    /// Implementation of <see cref="IMessageProcessor{TMessage}"/> that peforms orchestration around processing of a new message using an instance of the declared consumer (<see cref="IConsumer{TMessage}"/> or <see cref="IRequestHandler{TRequest, TResponse}"/> interface).
    /// </summary>
    /// <typeparam name="TMessage"></typeparam>
    public class ConsumerInstanceMessageProcessor<TMessage> : IMessageProcessor<TMessage> where TMessage : class
    {
        private readonly ILogger logger;

        private readonly MessageBusBase messageBus;
        private readonly ConsumerSettings consumerSettings;

        private readonly Func<TMessage, MessageWithHeaders> messageProvider;

        private readonly bool consumerWithContext;
        private readonly Action<TMessage, ConsumerContext> consumerContextInitializer;

        private readonly RuntimeTypeCache runtimeTypeCache;

        public AbstractConsumerSettings ConsumerSettings => consumerSettings;

        public ConsumerInstanceMessageProcessor(ConsumerSettings consumerSettings, MessageBusBase messageBus, Func<TMessage, MessageWithHeaders> messageProvider, Action<TMessage, ConsumerContext> consumerContextInitializer = null)
        {
            if (messageBus is null) throw new ArgumentNullException(nameof(messageBus));

            logger = messageBus.LoggerFactory.CreateLogger<ConsumerInstanceMessageProcessor<TMessage>>();
            this.consumerSettings = consumerSettings ?? throw new ArgumentNullException(nameof(consumerSettings));
            this.messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            this.messageProvider = messageProvider ?? throw new ArgumentNullException(nameof(messageProvider));

            this.consumerContextInitializer = consumerContextInitializer;
            consumerWithContext = typeof(IConsumerWithContext).IsAssignableFrom(consumerSettings.ConsumerType);

            runtimeTypeCache = messageBus.RuntimeTypeCache;
        }

        #region IAsyncDisposable

        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        protected virtual ValueTask DisposeAsyncCore() => new();

        #endregion

        public virtual async Task<Exception> ProcessMessage(TMessage msg, IMessageTypeConsumerInvokerSettings consumerInvoker)
        {
            Exception exceptionResult = null;
            try
            {
                var message = DeserializeMessage(msg, out var messageHeaders, consumerInvoker);
                var messageType = message.GetType();

                object response = null;
                string responseError = null;

                using (var messageScope = messageBus.GetMessageScope(consumerSettings, message))
                {
                    var consumerInterceptorType = runtimeTypeCache.ConsumerInterceptorType.Get(messageType);
                    var consumerInterceptors = runtimeTypeCache.ConsumerInterceptorType.ResolveAll(messageScope, messageType);

                    if (messageHeaders != null && messageHeaders.TryGetHeader(ReqRespMessageHeaders.Expires, out DateTimeOffset? expires) && expires != null)
                    {
                        // Verify if the request/message is already expired
                        var currentTime = messageBus.CurrentTime;
                        if (currentTime > expires.Value)
                        {
                            // ToDo: Call interceptor
                            OnMessageExpired(expires, message, currentTime, msg);

                            // Do not process the expired message
                            return null;
                        }
                    }

                    OnMessageArrived(message, msg);

                    // ToDo: Introduce CTs
                    var ct = new CancellationToken();

                    var consumerType = consumerInvoker?.ConsumerType ?? consumerSettings.ConsumerType;
                    var consumerInstance = messageScope.Resolve(consumerType)
                        ?? throw new ConfigurationMessageBusException($"Could not resolve consumer/handler type {consumerType} from the DI container. Please check that the configured type {consumerType} is registered within the DI container.");
                    try
                    {
                        if (consumerInterceptors != null)
                        {
                            // call with interceptors

                            var next = () => ExecuteConsumer(msg, message, messageHeaders, consumerInstance, consumerInvoker);

                            foreach (var consumerInterceptor in consumerInterceptors)
                            {
                                var interceptorParams = new object[] { message, ct, next, messageBus, consumerSettings.Path, messageHeaders, consumerInstance };
                                next = () => (Task<object>)consumerInterceptorType.Method.Invoke(consumerInterceptor, interceptorParams);
                            }

                            response = await next();
                        }
                        else
                        {
                            // call without interceptors
                            response = await ExecuteConsumer(msg, message, messageHeaders, consumerInstance, consumerInvoker).ConfigureAwait(false);
                        }
                    }
                    catch (Exception e)
                    {
                        responseError = OnMessageError(message, e, msg);
                        exceptionResult = e;
                    }
                    finally
                    {
                        if (consumerSettings.IsDisposeConsumerEnabled && consumerInstance is IDisposable consumerInstanceDisposable)
                        {
                            logger.LogDebug("Disposing consumer instance {Consumer} of type {ConsumerType}", consumerInstance, consumerType);
                            consumerInstanceDisposable.DisposeSilently("ConsumerInstance", logger);
                        }
                    }

                    OnMessageFinished(message, msg);
                }

                if (consumerSettings.ConsumerMode == ConsumerMode.RequestResponse)
                {
                    if (messageHeaders == null || !messageHeaders.TryGetHeader(ReqRespMessageHeaders.RequestId, out string requestId))
                    {
                        throw new MessageBusException($"The message header {ReqRespMessageHeaders.RequestId} was not present at this time");
                    }

                    await ProduceResponse(requestId, message, messageHeaders, response, responseError).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Processing of the message {Message} of type {MessageType} failed", msg, consumerSettings.MessageType);
                exceptionResult = e;
            }
            return exceptionResult;
        }

        private void OnMessageExpired(DateTimeOffset? expires, object message, DateTimeOffset currentTime, TMessage nativeMessage)
        {
            logger.LogWarning("The message {Message} arrived too late and is already expired (expires {ExpiresAt}, current {Time})", message, expires.Value, currentTime);

            try
            {
                // Execute the event hook
                consumerSettings.OnMessageExpired?.Invoke(messageBus, consumerSettings, message, nativeMessage);
                messageBus.Settings.OnMessageExpired?.Invoke(messageBus, consumerSettings, message, nativeMessage);
            }
            catch (Exception eh)
            {
                MessageBusBase.HookFailed(logger, eh, nameof(IConsumerEvents.OnMessageExpired));
            }
        }

        private string OnMessageError(object message, Exception e, TMessage nativeMessage)
        {
            string responseError = null;

            if (consumerSettings.ConsumerMode == ConsumerMode.RequestResponse)
            {
                logger.LogError(e, "Handler execution failed");
                // Save the exception
                responseError = e.ToString();
            }
            else
            {
                logger.LogError(e, "Consumer execution failed");
            }

            try
            {
                // Execute the event hook
                consumerSettings.OnMessageFault?.Invoke(messageBus, consumerSettings, message, e, nativeMessage);
                messageBus.Settings.OnMessageFault?.Invoke(messageBus, consumerSettings, message, e, nativeMessage);
            }
            catch (Exception eh)
            {
                MessageBusBase.HookFailed(logger, eh, nameof(IConsumerEvents.OnMessageFault));
            }

            return responseError;
        }

        private void OnMessageArrived(object message, TMessage nativeMessage)
        {
            try
            {
                // Execute the event hook
                consumerSettings.OnMessageArrived?.Invoke(messageBus, consumerSettings, message, consumerSettings.Path, nativeMessage);
                messageBus.Settings.OnMessageArrived?.Invoke(messageBus, consumerSettings, message, consumerSettings.Path, nativeMessage);
            }
            catch (Exception eh)
            {
                MessageBusBase.HookFailed(logger, eh, nameof(IConsumerEvents.OnMessageArrived));
            }
        }

        private void OnMessageFinished(object message, TMessage nativeMessage)
        {
            try
            {
                // Execute the event hook
                consumerSettings.OnMessageFinished?.Invoke(messageBus, consumerSettings, message, consumerSettings.Path, nativeMessage);
                messageBus.Settings.OnMessageFinished?.Invoke(messageBus, consumerSettings, message, consumerSettings.Path, nativeMessage);
            }
            catch (Exception eh)
            {
                MessageBusBase.HookFailed(logger, eh, nameof(IConsumerEvents.OnMessageFinished));
            }
        }

        private async Task<object> ExecuteConsumer(TMessage msg, object message, IDictionary<string, object> messageHeaders, object consumerInstance, IMessageTypeConsumerInvokerSettings consumerInvoker)
        {
            if (consumerWithContext)
            {
                var consumerContext = new ConsumerContext
                {
                    Headers = new ReadOnlyDictionary<string, object>(messageHeaders)
                };

                consumerContextInitializer?.Invoke(msg, consumerContext);

                var consumerWithContext = (IConsumerWithContext)consumerInstance;
                consumerWithContext.Context = consumerContext;
            }

            // the consumer just subscribes to the message
            var task = (consumerInvoker ?? consumerSettings).ConsumerMethod(consumerInstance, message, consumerSettings.Path);
            await task.ConfigureAwait(false);

            if (consumerSettings.ConsumerMode == ConsumerMode.RequestResponse)
            {
                // the consumer handles the request (and replies)
                var response = consumerSettings.ConsumerMethodResult(task);
                return response;
            }

            return null;
        }

        protected object DeserializeMessage(TMessage msg, out IDictionary<string, object> headers, IMessageTypeConsumerInvokerSettings invoker)
        {
            var messageWithHeaders = messageProvider(msg);

            headers = messageWithHeaders.Headers;

            logger.LogDebug("Deserializing message...");
            // ToDo: Take message type from header
            var messageType = invoker?.MessageType ?? consumerSettings.MessageType;
            var message = messageBus.Serializer.Deserialize(messageType, messageWithHeaders.Payload);

            return message;
        }

        private async Task ProduceResponse(string requestId, object request, IDictionary<string, object> requestHeaders, object response, string responseError)
        {
            // send the response (or error response)
            logger.LogDebug("Serializing the response {Response} of type {MessageType} for RequestId: {RequestId}...", response, consumerSettings.ResponseType, requestId);

            var responseHeaders = messageBus.CreateHeaders();
            responseHeaders.SetHeader(ReqRespMessageHeaders.RequestId, requestId);
            responseHeaders.SetHeader(ReqRespMessageHeaders.Error, responseError);

            await messageBus.ProduceResponse(request, requestHeaders, response, responseHeaders, consumerSettings).ConfigureAwait(false);
        }
    }
}