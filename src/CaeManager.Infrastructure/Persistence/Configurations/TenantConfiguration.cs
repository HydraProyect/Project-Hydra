using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Nombre)
            .IsRequired()
            .HasMaxLength(Tenant.LongitudMaximaNombre);

        builder.Property(t => t.Estado)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(t => t.PerfilVocabulario)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        // Horizonte 1.7 ("Billing mínimo viable") — ver EstadoComercialTenant
        // sobre por qué es un campo aparte de Estado.
        builder.Property(t => t.EstadoComercial)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(t => t.StripeCustomerId).HasMaxLength(100);
        builder.Property(t => t.StripeSubscriptionId).HasMaxLength(100);

        // Búsqueda del tenant dueño de una Subscription al procesar un
        // webhook de Stripe (ver WebhookStripeEndpoints) — filtrado, no
        // único: la mayoría de tenants no tienen ninguna suscripción
        // vinculada (NULL, ver StripeSubscriptionId) y Postgres no exige
        // unicidad entre NULLs de todas formas, pero el filtro deja fuera
        // esas filas del índice sin necesidad de pensarlo.
        builder.HasIndex(t => t.StripeSubscriptionId)
            .HasFilter("\"StripeSubscriptionId\" IS NOT NULL");

        // Sin HasQueryFilter: Tenant no pertenece a ningún tenant (ver
        // docs/MULTITENANCY.md § 4.1) y no tiene soft delete (ver Tenant.cs).

        // El tenant #1 (la organización actual, ver Etapa 2 de
        // PLAN-MIGRACION-MULTITENANT.md) — fecha fija, no DateTime.UtcNow,
        // para que la migración generada sea reproducible.
        builder.HasData(new
        {
            Id = TenantSeedData.IdPorDefecto,
            Nombre = TenantSeedData.NombrePorDefecto,
            Estado = EstadoTenant.Activo,
            CreadoEnUtc = new DateTime(2026, 7, 23, 0, 0, 0, DateTimeKind.Utc),
            // El tenant #1 es desde el que se opera Hydra, así que es también
            // el de plataforma: el único que puede recibir delegaciones de
            // soporte sobre los demás (ver Tenant.EsPlataforma). Se marca aquí
            // y no se deduce del Id, que es público y determinista.
            EsPlataforma = true,
            // El tenant #1 es la Empresa contratista de MVP-1 (Escenario 2 de
            // docs/MULTITENANCY.md § 2) — DDL-072 lo declara ClienteDirecto
            // explícitamente, nunca se infiere.
            PerfilVocabulario = PerfilVocabularioTenant.ClienteDirecto,
            // El tenant de plataforma nunca es él mismo un suscriptor de
            // pago (es quien opera Hydra, no un cliente) — se queda en
            // SinSuscripcion para siempre, y GateComercialTenantBehavior lo
            // exime del gate igualmente por EsPlataforma.
            EstadoComercial = EstadoComercialTenant.SinSuscripcion,
        });
    }
}
