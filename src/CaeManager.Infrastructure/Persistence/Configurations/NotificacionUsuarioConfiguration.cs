using CaeManager.Domain.Notificaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class NotificacionUsuarioConfiguration : IEntityTypeConfiguration<NotificacionUsuario>
{
    public void Configure(EntityTypeBuilder<NotificacionUsuario> builder)
    {
        builder.ToTable("NotificacionesUsuario");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Titulo).IsRequired().HasMaxLength(NotificacionUsuario.LongitudMaximaTitulo);
        builder.Property(n => n.Mensaje).IsRequired().HasMaxLength(NotificacionUsuario.LongitudMaximaMensaje);

        builder.HasIndex(n => new { n.UsuarioDestinatarioId, n.Leida });
    }
}
