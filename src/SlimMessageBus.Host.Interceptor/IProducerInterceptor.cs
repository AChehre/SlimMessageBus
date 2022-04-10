namespace SlimMessageBus.Host.Interceptor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IProducerInterceptor<in TMessage>
    {
        Task<object> OnHandle(TMessage message, CancellationToken cancellationToken, Func<Task<object>> next, IMessageBus bus, string path, IDictionary<string, object> headers);
    }
}
