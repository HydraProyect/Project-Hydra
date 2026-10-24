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
public class EstadoEnPlataformaConfiguration : IEntityTypeConfiguration<EstadoEnPlataforma>
{
    public void Configure(EntityTypeBuilder<EstadoEnPlataforma> builder)
    {
        builder.ToTable("EstadosPlataforma");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.AmbitoTitular).HasConversion<short>();
        builder.Property(e => e.Estado).HasConversion<short>();
        builder.Property(e => e.EstadoVigencia).HasConversion<short>();

        builder.HasIndex(e => new { e.TenantId, e.CentroId, e.CanalGestionDocumentalId, e.AmbitoTitular, e.TitularId, e.TipoDocumentoId })
            .IsUnique()
            .HasDatabaseName("UX_EstadosPlataforma_Acreditacion");
        builder.HasIndex(e => new { e.TenantId, e.AmbitoTitular, e.TitularId })
            .HasDatabaseName("IX_EstadosPlataforma_Titular");
        builder.HasIndex(e => new { e.TenantId, e.ProximoCambioEl })
            .HasDatabaseName("IX_EstadosPlataforma_ProximoCambio");
    }
}
