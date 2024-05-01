using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Flower.ViewModels;

using Microsoft.Extensions.DependencyInjection;

namespace Flower.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static void AddCommonServices(this IServiceCollection collection)
        {
            //collection.AddSingleton<IRepository, Repository>();
            //collection.AddTransient<BusinessService>();
            
        }
    }
}
