using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CentroConfiguration : IEntityTypeConfiguration<Centro>
{
    public void Configure(EntityTypeBuilder<Centro> builder)
    {
        builder.ToTable("Centros");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Nombre)
            .IsRequired()
            .HasMaxLength(Centro.LongitudMaximaNombre);

        builder.Property(c => c.CodigoCentro).HasMaxLength(Centro.LongitudMaximaCodigo);
        builder.Property(c => c.Direccion).HasMaxLength(Centro.LongitudMaximaDireccion);
        builder.Property(c => c.Contacto).HasMaxLength(Centro.LongitudMaximaContacto);

        // Sin navigation property hacia Cliente/Empresa a propósito: cada agregado
        // se consulta por su propio repositorio/query (ver ARCHITECTURE.md). La FK
        // sí se declara (P0-1 de docs/business/MATURITY_REVIEW.md): protege la
        // integridad referencial en la base de datos aunque el código nunca
        // navegue la relación.
        builder.HasIndex(c => c.ClienteId);
        builder.HasIndex(c => c.EmpresaId);

        // F3 (verificación del modelo real) — ClienteId (titular) y EmpresaId
        // (operadora) apuntan ahora a la misma tabla Empresas unificada. Dos
        // HasOne<Empresa>() independientes, sin navigation property en
        // ninguno de los dos lados: es exactamente lo que evita la
        // ambigüedad de fixup que EF Core tendría si hubiera navegación que
        // desambiguar (ver f3-revision-adversaria-2026-08-25.md, punto 3).
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.ClienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Prerequisito de las FKs que Visita/Proyecto/... declaran
        // hacia Centro.
        builder.HasIndex(c => new { c.TenantId, c.Id }).IsUnique();

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
