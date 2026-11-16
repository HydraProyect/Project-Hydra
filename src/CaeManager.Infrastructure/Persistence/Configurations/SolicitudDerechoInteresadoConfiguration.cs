using CaeManager.Domain.DerechosInteresado;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SolicitudDerechoInteresadoConfiguration : IEntityTypeConfiguration<SolicitudDerechoInteresado>
{
    public void Configure(EntityTypeBuilder<SolicitudDerechoInteresado> builder)
    {
        builder.ToTable("SolicitudesDerechoInteresado", t =>
        {
            t.HasCheckConstraint(
                "CK_SolicitudesDerechoInteresado_Tipo",
                "\"Tipo\" IN ('Acceso', 'Rectificacion', 'Supresion', 'Limitacion', 'Portabilidad', 'Oposicion', 'DecisionesAutomatizadas')");
            t.HasCheckConstraint(
                "CK_SolicitudesDerechoInteresado_Canal",
                "\"Canal\" IN ('CorreoElectronico', 'CorreoPostal', 'Presencial', 'Telefono', 'Otro')");
            t.HasCheckConstraint(
                "CK_SolicitudesDerechoInteresado_Estado",
                "\"Estado\" IN ('Recibida', 'IdentidadVerificada', 'EnCurso', 'Resuelta', 'Denegada', 'Reenviada')");
            // El plazo vigente nunca es anterior al ordinario y, prorrogado, no pasa de dos meses sobre él.
            t.HasCheckConstraint(
                "CK_SolicitudesDerechoInteresado_Plazo",
                "\"PlazoVencimiento\" >= \"PlazoOrdinario\" AND \"PlazoOrdinario\" > \"FechaRecepcion\""
                + " AND \"PlazoVencimiento\" <= (\"FechaRecepcion\" + INTERVAL '3 months')::date");
            // La prórroga es una sola y deja motivo, actor e instante juntos; sin prórroga, el plazo es el ordinario.
            t.HasCheckConstraint(
                "CK_SolicitudesDerechoInteresado_ProrrogaCoherente",
                "(\"ProrrogadaEnUtc\" IS NULL) = (\"ProrrogadaPorUsuarioId\" IS NULL)"
                + " AND (\"ProrrogadaEnUtc\" IS NULL) = (\"MotivoProrroga\" IS NULL)"
                + " AND (\"ProrrogadaEnUtc\" IS NOT NULL OR \"PlazoVencimiento\" = \"PlazoOrdinario\")");
            // Resuelta y Denegada dejan quién la cerró y cuándo; ningún otro estado lo hace.
            t.HasCheckConstraint(
                "CK_SolicitudesDerechoInteresado_CierreCoherente",
                "(\"Estado\" IN ('Resuelta', 'Denegada')) = (\"CerradaPorUsuarioId\" IS NOT NULL AND \"CerradaEnUtc\" IS NOT NULL)"
                + " AND (\"CerradaPorUsuarioId\" IS NULL) = (\"CerradaEnUtc\" IS NULL)");
            t.HasCheckConstraint(
                "CK_SolicitudesDerechoInteresado_MotivoSoloAlDenegar",
                "(\"Estado\" = 'Denegada') = (\"MotivoDenegacion\" IS NOT NULL)");
            t.HasCheckConstraint(
                "CK_SolicitudesDerechoInteresado_ResultadoSoloAlResolver",
                "\"Resultado\" IS NULL OR \"Estado\" = 'Resuelta'");
            // Nada se resuelve sin identidad verificada, tampoco escribiendo la fila a mano.
            t.HasCheckConstraint(
                "CK_SolicitudesDerechoInteresado_ResueltaConIdentidadVerificada",
                "\"Estado\" <> 'Resuelta' OR (\"IdentidadVerificadaEnUtc\" IS NOT NULL AND \"TramitacionIniciadaEnUtc\" IS NOT NULL)");
        });
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Tipo).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(s => s.Canal).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(s => s.Estado).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(s => s.FechaRecepcion).IsRequired();
        builder.Property(s => s.NombreInteresado).IsRequired()
            .HasMaxLength(SolicitudDerechoInteresado.LongitudMaximaNombreInteresado);
        builder.Property(s => s.Descripcion).HasMaxLength(SolicitudDerechoInteresado.LongitudMaximaDescripcion);
        builder.Property(s => s.MotivoProrroga).HasMaxLength(SolicitudDerechoInteresado.LongitudMaximaMotivo);
        builder.Property(s => s.Resultado).HasMaxLength(SolicitudDerechoInteresado.LongitudMaximaMotivo);
        builder.Property(s => s.MotivoDenegacion).HasMaxLength(SolicitudDerechoInteresado.LongitudMaximaMotivo);
        builder.Property(s => s.PlazoOrdinario).IsRequired();
        builder.Property(s => s.PlazoVencimiento).IsRequired();
        builder.Property(s => s.RegistradaPorUsuarioId).IsRequired();
        builder.Property(s => s.RegistradaEnUtc).IsRequired();

        // TrabajadorId sin clave ajena a propósito: la solicitud tiene que sobrevivir a la supresión del Trabajador
        // al que se refiere (#11), que es justo uno de sus desenlaces posibles.

        // El listado del registro, por el plazo que vence antes.
        builder.HasIndex(s => new { s.TenantId, s.PlazoVencimiento });

        builder.Ignore(s => s.EstaProrrogada);
        builder.Ignore(s => s.EstaCerrada);
        builder.Ignore(s => s.PlazoMaximoProrrogable);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
