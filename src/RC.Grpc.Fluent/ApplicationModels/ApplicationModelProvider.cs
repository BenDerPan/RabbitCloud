﻿using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rabbit.Cloud.Grpc.Abstractions;
using Rabbit.Cloud.Grpc.Fluent.Utilities;
using Rabbit.Cloud.Grpc.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Rabbit.Cloud.Grpc.Fluent.ApplicationModels
{
    public class ApplicationModelOptions
    {
        public ICollection<TypeInfo> Types { get; } = new List<TypeInfo>();
    }

    public class ApplicationModelHolder
    {
        private ApplicationModel _applicationModel;
        private readonly ApplicationModelOptions _options;
        private readonly IReadOnlyCollection<IApplicationModelProvider> _applicationModelProviders;

        public ApplicationModelHolder(IEnumerable<IApplicationModelProvider> applicationModelProviders, IOptions<ApplicationModelOptions> options, IServiceProvider services)
        {
            if (applicationModelProviders == null)
                throw new ArgumentNullException(nameof(applicationModelProviders));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            _applicationModelProviders = applicationModelProviders.OrderBy(i => i.Order).ToArray();
            _options = options.Value;
        }

        public ApplicationModel GetApplicationModel()
        {
            if (_applicationModel != null)
                return _applicationModel;

            return _applicationModel = BuildApplicationModel();
        }

        private ApplicationModel BuildApplicationModel()
        {
            var applicationModelProviderContext = new ApplicationModelProviderContext(_options.Types);

            foreach (var applicationModelProvider in _applicationModelProviders)
            {
                applicationModelProvider.OnProvidersExecuting(applicationModelProviderContext);
            }

            foreach (var applicationModelProvider in _applicationModelProviders)
            {
                applicationModelProvider.OnProvidersExecuted(applicationModelProviderContext);
            }

            var applicationModel = applicationModelProviderContext.Result;

            return applicationModel;
        }
    }

    public class MethodProvider : IMethodProvider
    {
        private readonly ApplicationModel _applicationModel;

        public MethodProvider(ApplicationModelHolder applicationModelHolder)
        {
            _applicationModel = applicationModelHolder.GetApplicationModel();
        }

        #region Implementation of IMethodProvider

        public int Order { get; } = 10;

        public void OnProvidersExecuting(MethodProviderContext context)
        {
            var applicationModel = _applicationModel;
            foreach (var serviceModel in applicationModel.Services)
            {
                foreach (var methodModel in serviceModel.Methods)
                {
                    var method = methodModel.CreateGenericMethod();
                    context.Results.Add(method);
                }
            }
        }

        public void OnProvidersExecuted(MethodProviderContext context)
        {
        }

        #endregion Implementation of IMethodProvider
    }

    public class ServerServiceDefinitionProvider : IServerServiceDefinitionProvider
    {
        private readonly IServiceProvider _services;
        private readonly ApplicationModel _applicationModel;
        private readonly IMethodTable _methodTable;

        public ServerServiceDefinitionProvider(ApplicationModelHolder applicationModelHolder, IMethodTableProvider methodTableProvider, IServiceProvider services)
        {
            _services = services;
            _applicationModel = applicationModelHolder.GetApplicationModel();
            _methodTable = methodTableProvider.MethodTable;
        }

        #region Implementation of IServerServiceDefinitionProvider

        int IServerServiceDefinitionProvider.Order { get; } = 10;

        //todo: 考虑优化实现
        public void OnProvidersExecuting(ServerServiceDefinitionProviderContext context)
        {
            var applicationModel = _applicationModel;

            var builder = new ServerServiceDefinition.Builder();
            foreach (var serverService in applicationModel.ServerServices)
            {
                foreach (var serverServiceMethod in serverService.ServerMethods)
                {
                    var serviceId = $"/{serverService.ServiceName}/{serverServiceMethod.Method.Name}";

                    var grpcMethod = _methodTable.Get(serviceId);
                    var requestType = serverServiceMethod.Method.RequestMarshaller.Type;
                    var responseType = serverServiceMethod.Method.ResponseMarshaller.Type;

                    switch (serverServiceMethod.Method.Type)
                    {
                        case MethodType.Unary:
                            var invoker = _services.GetRequiredService<IServerMethodInvokerFactory>().CreateInvoker(serverServiceMethod);

                            var delegateType = Cache.GetUnaryServerDelegateType(requestType, responseType);

                            var requestParameterExpression = Expression.Parameter(requestType);
                            var callContextParameterExpression = Expression.Parameter(typeof(ServerCallContext));

                            var methodCallExpression = Expression.Call(Expression.Constant(invoker),
                                nameof(IServerMethodInvoker.UnaryServerMethod), new[] { requestType, responseType }, requestParameterExpression, callContextParameterExpression);

                            var methodDelegate = Expression.Lambda(delegateType, Expression.Convert(methodCallExpression, serverServiceMethod.MethodInfo.ReturnType), requestParameterExpression, callContextParameterExpression).Compile();

                            var addMethodInvoker = Cache.GetAddMethod(delegateType, grpcMethod.GetType(), requestType, responseType);
                            addMethodInvoker(builder, grpcMethod, methodDelegate);
                            break;

                        case MethodType.ClientStreaming:
                            break;

                        case MethodType.ServerStreaming:
                            break;

                        case MethodType.DuplexStreaming:
                            break;

                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
            context.Results.Add(builder.Build());
        }

        public void OnProvidersExecuted(ServerServiceDefinitionProviderContext context)
        {
        }

        #endregion Implementation of IServerServiceDefinitionProvider

        #region Help Type

        private static class Cache
        {
            #region Field

            private static readonly IDictionary<object, object> Caches = new Dictionary<object, object>();

            #endregion Field

            public static Func<ServerServiceDefinition.Builder, object, object, ServerServiceDefinition.Builder> GetAddMethod(Type delegateType, Type methodType, Type requestType, Type responseType)
            {
                var key = ("AddMethod", delegateType);

                return GetCache(key, () =>
                {
                    var builderParameterExpression = Expression.Parameter(typeof(ServerServiceDefinition.Builder));
                    var methodParameterExpression = Expression.Parameter(typeof(object));
                    var delegateParameterExpression = Expression.Parameter(typeof(object));

                    var callExpression = Expression.Call(builderParameterExpression, "AddMethod",
                        new[] { requestType, responseType },
                        Expression.Convert(methodParameterExpression, methodType), Expression.Convert(delegateParameterExpression, delegateType));

                    return Expression.Lambda<Func<ServerServiceDefinition.Builder, object, object, ServerServiceDefinition.Builder>>(callExpression, builderParameterExpression, methodParameterExpression, delegateParameterExpression).Compile();
                });
            }

            public static Type GetUnaryServerDelegateType(Type requestType, Type responseType)
            {
                var key = ("UnaryServerDelegateType", requestType, responseType);
                return GetCache(key, () => typeof(UnaryServerMethod<,>).MakeGenericType(requestType, responseType));
            }

            #region Private Method

            private static T GetCache<T>(object key, Func<T> factory)
            {
                if (Caches.TryGetValue(key, out var cache))
                {
                    return (T)cache;
                }
                return (T)(Caches[key] = factory());
            }

            #endregion Private Method
        }

        #endregion Help Type
    }
}