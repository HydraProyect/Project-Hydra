using CaeManager.Domain.Operaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class PropuestaApoyoCarteraConfiguration : IEntityTypeConfiguration<PropuestaApoyoCartera>
{
    /// <summary>Nombre del índice único de pendientes: el Command traduce su 23505 a «ya tiene una propuesta pendiente».</summary>
    public const string IndicePendienteUnica = "IX_PropuestasApoyoCartera_PendienteUnica";

    public void Configure(EntityTypeBuilder<PropuestaApoyoCartera> builder)
    {
        builder.ToTable("PropuestasApoyoCartera");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.OperadorTenantId).IsRequired();
        builder.Property(p => p.PropietarioTenantId).IsRequired();
        builder.Property(p => p.AsignacionOperacionId).IsRequired();
        builder.Property(p => p.ProponenteUsuarioId).IsRequired();
        builder.Property(p => p.DestinatarioUsuarioId).IsRequired();
        builder.Property(p => p.Estado).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.MotivoAnulacion).HasConversion<string>().HasMaxLength(30);
        builder.Property(p => p.CreadaEnUtc).IsRequired();

        // La campana del destinatario y la lista de quien propuso: «¿qué tengo pendiente?».
        builder.HasIndex(p => new { p.OperadorTenantId, p.DestinatarioUsuarioId, p.Estado });
        builder.HasIndex(p => new { p.OperadorTenantId, p.ProponenteUsuarioId, p.Estado });

        // Una sola propuesta pendiente por destinatario y operación. Sin el filtro, una
        // propuesta ya rechazada o retirada impediría volver a proponer nunca; sin el índice,
        // dos propuestas a la vez dejarían dos avisos y aceptar una dejaría la otra colgando.
        builder.HasIndex(p => new { p.AsignacionOperacionId, p.DestinatarioUsuarioId }, IndicePendienteUnica)
            .IsUnique()
            .HasFilter($"\"{nameof(PropuestaApoyoCartera.Estado)}\" = 'Pendiente'");

        // Misma FK compuesta que la cartera y que la solicitud de incorporación: una propuesta
        // cuyo propietario no sea el de su operación no se puede ni escribir.
        builder.HasOne<AsignacionOperacion>().WithMany()
            .HasForeignKey(p => new { p.AsignacionOperacionId, p.PropietarioTenantId })
            .HasPrincipalKey(o => new { o.Id, o.PropietarioTenantId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AsignacionCartera>().WithMany()
            .HasForeignKey(p => p.AsignacionCarteraId)
            .OnDelete(DeleteBehavior.Restrict);

        // AsignacionOperadorDelegadoId, sin FK: esa fila se borra físicamente al retirar a un
        // operador y la propuesta tiene que sobrevivirle como histórico.

        // Sin HasQueryFilter: catálogo del Operador CAE, como la solicitud de incorporación.
        // Lo acota la política RLS operador_de_la_propuesta (migración PropuestasApoyoCartera).
    }
}
