using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork
{
    public static class DependencyInjectionHelper
    {
        private static IServiceProvider? _provider;
        public static void Set(IServiceProvider provider)
        {
            Helper.CheckArgumentNull(provider,nameof(provider));
            _provider = provider;
        }

        public static T Get<T>(bool checkNull = true)
        {
            return (T)Get(typeof(T), checkNull);
        }

        public static object Get(Type type,bool checkNull=true)
        {
            Helper.CheckArgumentNull(_provider, nameof(_provider));
            var obj = _provider?.GetService(type);
            if (checkNull)
            {
                Helper.CheckArgumentNull(obj, type.Name);
            }
            return obj;
        }
    }
}
