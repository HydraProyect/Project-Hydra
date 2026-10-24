using CaeManager.Domain.EstadoMaterializado;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

// Estado materializado (PR 2 del diseño de 2026-10-24): las cinco tablas del Tenant propietario. Todas heredan de
// EntidadConTenant, así que el filtro global de Tenant (CaeManagerDbContext.OnModelCreating), el sellado de TenantId y la
// cobertura de RLS por reflexión las alcanzan sin listarlas. Clave primaria Id, como el resto del modelo; la unicidad
// del par, de la entidad o del Tenant la da un índice único. Los enums se guardan como smallint con valores congelados.
// Sin FK: son datos derivados que el recálculo rehace enteros, y el titular es polimórfico (Trabajador o Empresa).
// Nombres de índice explícitos: los que genera EF pasan de 63 caracteres y PostgreSQL los truncaría.
public class EstadoEntidadConfiguration : IEntityTypeConfiguration<EstadoEntidad>
{
    public void Configure(EntityTypeBuilder<EstadoEntidad> builder)
    {
        builder.ToTable("EstadosEntidad");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Ambito).HasConversion<short>();
        builder.Property(e => e.EstadoPlataforma).HasConversion<short?>();

        builder.HasIndex(e => new { e.TenantId, e.Ambito, e.EntidadId })
            .IsUnique()
            .HasDatabaseName("UX_EstadosEntidad_Entidad");
        // Filtros y recuentos por estado.
        builder.HasIndex(e => new { e.TenantId, e.Ambito, e.Estado })
            .HasDatabaseName("IX_EstadosEntidad_Estado");
        builder.HasIndex(e => new { e.TenantId, e.ProximoCambioEl })
            .HasDatabaseName("IX_EstadosEntidad_ProximoCambio");
    }
}
