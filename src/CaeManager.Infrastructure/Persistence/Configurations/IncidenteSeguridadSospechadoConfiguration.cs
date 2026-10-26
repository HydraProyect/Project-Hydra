using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class IncidenteSeguridadSospechadoConfiguration : IEntityTypeConfiguration<IncidenteSeguridadSospechado>
{
    public void Configure(EntityTypeBuilder<IncidenteSeguridadSospechado> builder)
    {
        builder.ToTable("IncidentesSeguridadSospechados", t =>
        {
            t.HasCheckConstraint("CK_IncidentesSeguridadSospechados_Senal",
                "\"Senal\" IN ('InicioSesionNoReconocido', 'ExtraccionMasiva', 'AccesoRepetidoAOtroTenant')");
            t.HasCheckConstraint("CK_IncidentesSeguridadSospechados_Estado",
                "\"Estado\" IN ('Sospechado', 'EnEvaluacion', 'Confirmado', 'Descartado')");
            t.HasCheckConstraint("CK_IncidentesSeguridadSospechados_ConfirmadoConHora",
                "(\"Estado\" <> 'Confirmado') OR (\"ConfirmadoEnUtc\" IS NOT NULL)");
            t.HasCheckConstraint("CK_IncidentesSeguridadSospechados_Detecciones", "\"Detecciones\" >= 1");
        });
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Senal).IsRequired().HasConversion<string>().HasMaxLength(40);
        builder.Property(i => i.SujetoClave).IsRequired().HasMaxLength(IncidenteSeguridadSospechado.LongitudMaximaSujeto);
        builder.Property(i => i.DetectadoEnUtc).IsRequired();
        builder.Property(i => i.UltimaDeteccionEnUtc).IsRequired();
        builder.Property(i => i.Detecciones).IsRequired();
        builder.Property(i => i.TenantsAfectados).IsRequired().HasColumnType("uuid[]");
        builder.Property(i => i.Evidencia).IsRequired().HasColumnType("jsonb");
        builder.Property(i => i.Estado).IsRequired().HasConversion<string>().HasMaxLength(20);

        builder.HasMany(i => i.Transiciones).WithOne()
            .HasForeignKey(t => t.IncidenteId)
            .OnDelete(DeleteBehavior.Restrict);

        // La búsqueda de la deduplicación: misma señal y sujeto, más reciente primero.
        builder.HasIndex(i => new { i.Senal, i.SujetoClave, i.DetectadoEnUtc });

        // La bandeja de la página de Plataforma: lo pendiente de decidir.
        builder.HasIndex(i => new { i.Estado, i.DetectadoEnUtc });

        // Sin HasQueryFilter: plano de Plataforma, no pertenece a un Tenant.
    }
}

public class TransicionIncidenteSeguridadConfiguration : IEntityTypeConfiguration<TransicionIncidenteSeguridad>
{
    public void Configure(EntityTypeBuilder<TransicionIncidenteSeguridad> builder)
    {
        builder.ToTable("TransicionesIncidenteSeguridad");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.IncidenteId).IsRequired();
        builder.Property(t => t.EstadoAnterior).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.EstadoNuevo).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.OcurridoEnUtc).IsRequired();
        builder.Property(t => t.Motivo).HasMaxLength(TransicionIncidenteSeguridad.LongitudMaximaMotivo);

        builder.HasIndex(t => new { t.IncidenteId, t.OcurridoEnUtc });

        // Sin HasQueryFilter: plano de Plataforma, como su incidente.
    }
}
