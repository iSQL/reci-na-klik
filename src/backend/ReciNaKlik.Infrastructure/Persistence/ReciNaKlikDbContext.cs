using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ReciNaKlik.Infrastructure.Features.Audit.Models;
using ReciNaKlik.Infrastructure.Features.Authentication.Models;
using ReciNaKlik.Infrastructure.Features.Jobs.Models;
using ReciNaKlik.Infrastructure.Persistence.Extensions;

namespace ReciNaKlik.Infrastructure.Persistence;

/// <summary>
/// Application database context.
/// </summary>
internal class ReciNaKlikDbContext(DbContextOptions<ReciNaKlikDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    /// <summary>
    /// Gets or sets the refresh tokens table for JWT token rotation.
    /// </summary>
    public DbSet<RefreshToken> RefreshTokens { get; set; }

    /// <summary>
    /// Gets or sets the email tokens table for opaque password-reset and email-verification links.
    /// </summary>
    public DbSet<EmailToken> EmailTokens { get; set; }

    /// <summary>
    /// Gets or sets the paused jobs table for persisting pause state across restarts.
    /// </summary>
    public DbSet<PausedJob> PausedJobs { get; set; }



    /// <summary>
    /// Gets or sets the audit events table for the append-only audit log.
    /// </summary>
    public DbSet<AuditEvent> AuditEvents { get; set; }


    /// <summary>
    /// Configures the model by applying all entity configurations from this assembly
    /// and fuzzy search extensions.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReciNaKlikDbContext).Assembly);
        modelBuilder.ApplyAuthSchema();
        modelBuilder.ApplyFuzzySearch();
    }
}
