﻿namespace SlimMessageBus.Host.Interceptor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IRequestHandlerInterceptor<in TRequest, TResponse>
    {
        // ToDo: to result add headers and path
        Task<TResponse> OnHandle(TRequest request, CancellationToken cancellationToken, Func<Task<TResponse>> next, IMessageBus bus, string path, IReadOnlyDictionary<string, object> headers, object handler);
    }
}
