using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using ReciNaKlik.Application.Caching.Constants;
using ReciNaKlik.Application.Cookies;
using ReciNaKlik.Application.Cookies.Constants;
using ReciNaKlik.Application.Features.Audit;
using ReciNaKlik.Application.Features.Authentication.Dtos;
using ReciNaKlik.Application.Identity;
using ReciNaKlik.Application.Identity.Constants;
using ReciNaKlik.Application.Identity.Dtos;
using ReciNaKlik.Infrastructure.Features.Authentication.Models;
using ReciNaKlik.Infrastructure.Persistence;
using ReciNaKlik.Shared;

namespace ReciNaKlik.Infrastructure.Identity.Services;

/// <summary>
/// Identity-backed implementation of <see cref="IUserService"/> with HybridCache caching.
/// </summary>
internal sealed class UserService(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IUserContext userContext,
    HybridCache hybridCache,
    ReciNaKlikDbContext dbContext,
    ICookieService cookieService,
    IAuditService auditService,
    ILogger<UserService> logger) : IUserService
{
    private static readonly HybridCacheEntryOptions UserCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(1)
    };

    /// <inheritdoc />
    public async Task<Result<UserOutput>> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var userId = userContext.UserId;

        if (!userId.HasValue)
        {
            return Result<UserOutput>.Failure(ErrorMessages.User.NotAuthenticated, ErrorType.Unauthorized);
        }

        var cacheKey = CacheKeys.User(userId.Value);

        var output = await hybridCache.GetOrCreateAsync<UserOutput?>(
            cacheKey,
            async ct =>
            {
                var user = await userManager.FindByIdAsync(userId.Value.ToString());

                if (user is null)
                {
                    return null;
                }

                var roles = await userManager.GetRolesAsync(user);
                var permissions = await GetPermissionsForRolesAsync(roles);
                var logins = await userManager.GetLoginsAsync(user);
                var hasPassword = await userManager.HasPasswordAsync(user);

                return new UserOutput(
                    Id: user.Id,
                    UserName: user.UserName!,
                    FirstName: user.FirstName,
                    LastName: user.LastName,
                    PhoneNumber: user.PhoneNumber,
                    Bio: user.Bio,
                    HasAvatar: user.HasAvatar,
                    Roles: roles,
                    Permissions: permissions,
                    IsEmailConfirmed: user.EmailConfirmed,
                    IsTwoFactorEnabled: user.TwoFactorEnabled,
                    LinkedProviders: logins.Select(l => l.LoginProvider).ToList(),
                    HasPassword: hasPassword);
            },
            UserCacheOptions,
            cancellationToken: cancellationToken);

        return output is not null
            ? Result<UserOutput>.Success(output)
            : Result<UserOutput>.Failure(ErrorMessages.User.NotFound);
    }

    /// <inheritdoc />
    public async Task<IList<string>> GetUserRolesAsync(Guid userId)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return new List<string>();
        }
        return await userManager.GetRolesAsync(user);
    }

    /// <inheritdoc />
    public async Task<Result<UserOutput>> UpdateProfileAsync(UpdateProfileInput input, CancellationToken cancellationToken = default)
    {
        var userId = userContext.UserId;

        if (!userId.HasValue)
        {
            return Result<UserOutput>.Failure(ErrorMessages.User.NotAuthenticated, ErrorType.Unauthorized);
        }

        var user = await userManager.FindByIdAsync(userId.Value.ToString());

        if (user is null)
        {
            return Result<UserOutput>.Failure(ErrorMessages.User.NotFound);
        }

        var normalizedPhone = PhoneNumberHelper.Normalize(input.PhoneNumber);

        if (normalizedPhone is not null && await IsPhoneNumberTakenAsync(normalizedPhone, excludeUserId: userId.Value))
        {
            return Result<UserOutput>.Failure(ErrorMessages.User.PhoneNumberTaken);
        }

        user.FirstName = input.FirstName;
        user.LastName = input.LastName;
        user.PhoneNumber = normalizedPhone;
        user.Bio = input.Bio;

        var result = await userManager.UpdateAsync(user);

        if (!result.Succeeded)
        {
            logger.LogWarning("UpdateAsync failed for user '{UserId}': {Errors}",
                userId.Value, string.Join(", ", result.Errors.Select(e => e.Description)));
            return Result<UserOutput>.Failure(ErrorMessages.User.UpdateFailed);
        }

        // Invalidate cache after update
        var cacheKey = CacheKeys.User(userId.Value);
        await hybridCache.RemoveAsync(cacheKey, cancellationToken);

        var roles = await userManager.GetRolesAsync(user);
        var permissions = await GetPermissionsForRolesAsync(roles);
        var logins = await userManager.GetLoginsAsync(user);
        var hasPassword = await userManager.HasPasswordAsync(user);

        var output = new UserOutput(
            Id: user.Id,
            UserName: user.UserName!,
            FirstName: user.FirstName,
            LastName: user.LastName,
            PhoneNumber: user.PhoneNumber,
            Bio: user.Bio,
            HasAvatar: user.HasAvatar,
            Roles: roles,
            Permissions: permissions,
            IsEmailConfirmed: user.EmailConfirmed,
            IsTwoFactorEnabled: user.TwoFactorEnabled,
            LinkedProviders: logins.Select(l => l.LoginProvider).ToList(),
            HasPassword: hasPassword);

        await auditService.LogAsync(AuditActions.ProfileUpdate, userId: userId.Value, ct: cancellationToken);

        return Result<UserOutput>.Success(output);
    }


    /// <inheritdoc />
    public async Task<Result> DeleteAccountAsync(DeleteAccountInput input, CancellationToken cancellationToken = default)
    {
        var userId = userContext.UserId;

        if (!userId.HasValue)
        {
            return Result.Failure(ErrorMessages.User.NotAuthenticated, ErrorType.Unauthorized);
        }

        var user = await userManager.FindByIdAsync(userId.Value.ToString());

        if (user is null)
        {
            return Result.Failure(ErrorMessages.User.NotFound);
        }

        var passwordValid = await userManager.CheckPasswordAsync(user, input.Password);

        if (!passwordValid)
        {
            return Result.Failure(ErrorMessages.User.DeleteInvalidPassword);
        }

        var lastSuperuserResult = await EnforceLastSuperuserProtectionForDeletionAsync(user, cancellationToken);
        if (!lastSuperuserResult.IsSuccess)
        {
            return lastSuperuserResult;
        }

        await auditService.LogAsync(AuditActions.AccountDeletion, userId: userId.Value, ct: cancellationToken);


        await RevokeUserTokens(user, userId.Value, cancellationToken);
        await DeleteUser(user);
        ClearAuthCookies();
        await InvalidateUserCache(userId.Value);

        return Result.Success();
    }

    /// <summary>
    /// Prevents self-deletion if the user is the last Superuser.
    /// </summary>
    private async Task<Result> EnforceLastSuperuserProtectionForDeletionAsync(
        ApplicationUser user, CancellationToken cancellationToken)
    {
        var userRoles = await userManager.GetRolesAsync(user);

        foreach (var role in userRoles.Where(r => r is AppRoles.Superuser))
        {
            var roleEntity = await roleManager.FindByNameAsync(role);
            if (roleEntity is null) continue;

            var usersInRoleCount = await dbContext.UserRoles
                .CountAsync(ur => ur.RoleId == roleEntity.Id, cancellationToken);

            if (usersInRoleCount <= 1)
            {
                return Result.Failure(ErrorMessages.User.LastSuperuserCannotDelete);
            }
        }

        return Result.Success();
    }

    private async Task RevokeUserTokens(ApplicationUser user, Guid userId, CancellationToken cancellationToken)
    {
        var tokens = await dbContext.RefreshTokens
            .Where(rt => rt.UserId == userId && !rt.IsInvalidated)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.IsInvalidated = true;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await userManager.UpdateSecurityStampAsync(user);
        await hybridCache.RemoveAsync(CacheKeys.SecurityStamp(userId), cancellationToken);
    }

    private void ClearAuthCookies()
    {
        cookieService.DeleteCookie(CookieNames.AccessToken);
        cookieService.DeleteCookie(CookieNames.RefreshToken);
    }

    private async Task InvalidateUserCache(Guid userId)
    {
        var cacheKey = CacheKeys.User(userId);
        await hybridCache.RemoveAsync(cacheKey);
    }

    private async Task DeleteUser(ApplicationUser user)
    {
        var result = await userManager.DeleteAsync(user);

        if (!result.Succeeded)
        {
            logger.LogWarning("DeleteAsync failed for user '{UserId}': {Errors}",
                user.Id, string.Join(", ", result.Errors.Select(e => e.Description)));
            throw new InvalidOperationException(ErrorMessages.User.DeleteFailed);
        }
    }

    /// <summary>
    /// Collects deduplicated permission values for the given roles in a single query.
    /// Superuser receives all permissions implicitly.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetPermissionsForRolesAsync(IList<string> roleNames)
    {
        if (roleNames.Contains(AppRoles.Superuser))
        {
            return AppPermissions.All;
        }

        var normalizedNames = roleNames
            .Select(r => r.ToUpperInvariant())
            .ToList();

        return await dbContext.RoleClaims
            .Join(dbContext.Roles,
                rc => rc.RoleId,
                r => r.Id,
                (rc, r) => new { r.NormalizedName, rc.ClaimType, rc.ClaimValue })
            .Where(x => normalizedNames.Contains(x.NormalizedName!)
                        && x.ClaimType == AppPermissions.ClaimType)
            .Select(x => x.ClaimValue!)
            .Distinct()
            .ToListAsync();
    }

    /// <summary>
    /// Checks whether any existing user already has the given normalized phone number.
    /// </summary>
    private async Task<bool> IsPhoneNumberTakenAsync(string normalizedPhone, Guid excludeUserId)
    {
        return await userManager.Users
            .AnyAsync(u =>
                u.PhoneNumber != null
                && u.PhoneNumber == normalizedPhone
                && u.Id != excludeUserId);
    }
}
