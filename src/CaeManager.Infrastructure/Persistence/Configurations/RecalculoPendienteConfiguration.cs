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
public class RecalculoPendienteConfiguration : IEntityTypeConfiguration<RecalculoPendiente>
{
    public void Configure(EntityTypeBuilder<RecalculoPendiente> builder)
    {
        builder.ToTable("RecalculosPendientes");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Ambito).HasConversion<short>();
        builder.Property(r => r.Motivo).IsRequired().HasMaxLength(RecalculoPendiente.LongitudMaximaMotivo);

        // La cola se consume por Tenant y en orden de llegada.
        builder.HasIndex(r => new { r.TenantId, r.CreadoEnUtc })
            .HasDatabaseName("IX_RecalculosPendientes_Cola");
    }
}
