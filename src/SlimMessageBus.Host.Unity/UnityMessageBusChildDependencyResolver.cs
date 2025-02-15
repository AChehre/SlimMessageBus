﻿namespace SlimMessageBus.Host.Unity
{
    using System;
    using SlimMessageBus.Host.DependencyResolver;
    using global::Unity;
    using Microsoft.Extensions.Logging;

    public class UnityMessageBusChildDependencyResolver : UnityMessageBusDependencyResolver, IChildDependencyResolver
    {
        private readonly ILogger<UnityMessageBusChildDependencyResolver> logger;
        private bool _disposedValue;

        public IDependencyResolver Parent { get; }

        public UnityMessageBusChildDependencyResolver(IDependencyResolver parent, IUnityContainer container, ILoggerFactory loggerFactory)
            : base(container, loggerFactory)
        {
            logger = loggerFactory.CreateLogger<UnityMessageBusChildDependencyResolver>();
            Parent = parent;
        }

        #region Dispose

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    logger.LogDebug("Disposing scope");
                    Container.Dispose();
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}

