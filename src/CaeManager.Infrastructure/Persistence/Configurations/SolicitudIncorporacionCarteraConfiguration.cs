using CaeManager.Domain.Operaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SolicitudIncorporacionCarteraConfiguration : IEntityTypeConfiguration<SolicitudIncorporacionCartera>
{
    /// <summary>Nombre del índice único de pendientes: el Command traduce su 23505 a «ya la pediste».</summary>
    public const string IndicePendienteUnica = "IX_SolicitudesIncorporacionCartera_PendienteUnica";

    public void Configure(EntityTypeBuilder<SolicitudIncorporacionCartera> builder)
    {
        builder.ToTable("SolicitudesIncorporacionCartera");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.OperadorTenantId).IsRequired();
        builder.Property(s => s.PropietarioTenantId).IsRequired();
        builder.Property(s => s.AsignacionOperacionId).IsRequired();
        builder.Property(s => s.SolicitanteUsuarioId).IsRequired();
        builder.Property(s => s.Mensaje).IsRequired().HasMaxLength(SolicitudIncorporacionCartera.LongitudMaximaMensaje);
        builder.Property(s => s.Estado).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.MotivoAnulacion).HasConversion<string>().HasMaxLength(30);
        builder.Property(s => s.CreadaEnUtc).IsRequired();

        // La bandeja del Coordinador CAE: «¿qué tiene pendiente mi Operador CAE?».
        builder.HasIndex(s => new { s.OperadorTenantId, s.Estado });
        builder.HasIndex(s => s.SolicitanteUsuarioId);

        // Una sola solicitud pendiente por Gestor CAE y operación. Sin él, dos
        // clics seguidos (o dos pestañas) dejarían dos avisos iguales a los
        // Coordinadores CAE, y aceptar uno dejaría el otro colgando.
        builder.HasIndex(s => new { s.AsignacionOperacionId, s.SolicitanteUsuarioId }, IndicePendienteUnica)
            .IsUnique()
            .HasFilter($"\"{nameof(SolicitudIncorporacionCartera.Estado)}\" = 'Pendiente'");

        // Misma FK compuesta que la cartera: una solicitud cuyo propietario no
        // sea el de su operación no se puede ni escribir.
        builder.HasOne<AsignacionOperacion>().WithMany()
            .HasForeignKey(s => new { s.AsignacionOperacionId, s.PropietarioTenantId })
            .HasPrincipalKey(o => new { o.Id, o.PropietarioTenantId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AsignacionCartera>().WithMany()
            .HasForeignKey(s => s.AsignacionCarteraId)
            .OnDelete(DeleteBehavior.Restrict);

        // AsignacionOperadorDelegadoId, sin FK: esa fila se borra físicamente
        // al retirar a un operador (ver IAsignacionOperadorDelegadoRepository.Eliminar)
        // y la solicitud tiene que sobrevivirle como histórico.

        // Sin HasQueryFilter: catálogo como las asignaciones. Lo acota la
        // política RLS operador_de_la_solicitud (migración SolicitudesIncorporacionCartera).
    }
}
