using CaeManager.Domain.Exportacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SolicitudExportacionCompletaConfiguration : IEntityTypeConfiguration<SolicitudExportacionCompleta>
{
    /// <summary>Filtro del índice único parcial: una sola solicitud viva (Pendiente o EnCurso) por Tenant.</summary>
    public const string FiltroSolicitudViva = "\"Estado\" IN ('Pendiente', 'EnCurso')";

    public void Configure(EntityTypeBuilder<SolicitudExportacionCompleta> builder)
    {
        builder.ToTable("SolicitudesExportacionCompleta");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Estado).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.ContrasenaProtegida).HasMaxLength(1000);
        builder.Property(s => s.UbicacionFichero).HasMaxLength(SolicitudExportacionCompleta.LongitudMaximaUbicacion);
        builder.Property(s => s.MotivoFallo).HasMaxLength(SolicitudExportacionCompleta.LongitudMaximaMotivoFallo);

        // El límite de una cada 24 horas y el sondeo del servicio de fondo leen por Tenant, estado y fecha.
        builder.HasIndex(s => new { s.TenantId, s.Estado, s.SolicitadaEnUtc });

        // Respaldo del candado por Tenant de la solicitud: aunque un camino futuro registrara una solicitud sin
        // tomarlo, la base no admite dos vivas del mismo Tenant.
        builder.HasIndex(s => s.TenantId)
            .IsUnique()
            .HasFilter(FiltroSolicitudViva)
            .HasDatabaseName("IX_SolicitudesExportacionCompleta_TenantId_Viva");
    }
}
