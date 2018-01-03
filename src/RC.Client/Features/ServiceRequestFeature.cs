﻿using Rabbit.Cloud.Application.Features;
using Rabbit.Cloud.Client.Abstractions;
using Rabbit.Cloud.Client.Abstractions.Features;
using Rabbit.Cloud.Discovery.Abstractions;
using System;

namespace Rabbit.Cloud.Client.Features
{
    public class ServiceRequestFeature : IServiceRequestFeature
    {
        public ServiceRequestFeature(IRequestFeature requestFeature)
        {
            ServiceProtocol = requestFeature.Scheme;
            if (requestFeature.Port < 0)
                ServiceName = requestFeature.Host;
        }

        public ServiceRequestFeature()
        {
        }

        #region Implementation of IServiceRequestFeature

        public string ServiceName { get; set; }
        public string ServiceProtocol { get; set; }
        public ServiceRequestOptions RequestOptions { get; set; }
        public Func<IServiceInstance> GetServiceInstance { get; set; }

        #endregion Implementation of IServiceRequestFeature
    }
}