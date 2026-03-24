using Microsoft.Extensions.DependencyInjection;
using ReciNaKlik.Application.Features.Admin;
using ReciNaKlik.Infrastructure.Features.Admin.Services;

namespace ReciNaKlik.Infrastructure.Features.Admin.Extensions;

/// <summary>
/// Extension methods for registering admin feature services.
/// </summary>
public static class ServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the admin services for user and role management.
        /// </summary>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddAdminServices()
        {
            services.AddScoped<IAdminService, AdminService>();
            services.AddScoped<IRoleManagementService, RoleManagementService>();
            return services;
        }
    }
}
