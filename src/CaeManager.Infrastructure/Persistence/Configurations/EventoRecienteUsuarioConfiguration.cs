using CaeManager.Domain.BusquedaGlobal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EventoRecienteUsuarioConfiguration : IEntityTypeConfiguration<EventoRecienteUsuario>
{
    public void Configure(EntityTypeBuilder<EventoRecienteUsuario> builder)
    {
        builder.ToTable("EventosRecientesUsuario");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Tipo).IsRequired().HasMaxLength(30);
        builder.Property(e => e.Titulo).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Subtitulo).HasMaxLength(200);
        builder.Property(e => e.UrlDestino).IsRequired().HasMaxLength(500);

        // El acceso que sirve este índice es siempre
        // WHERE TenantId = :t AND UsuarioId = :u ORDER BY OcurridoEnUtc DESC
        // (ObtenerRecientesQuery y la purga de excedentes) — orden
        // descendente explícito para que Postgres no tenga que invertir un
        // índice ascendente en cada lectura.
        builder.HasIndex(e => new { e.TenantId, e.UsuarioId, e.OcurridoEnUtc })
            .IsDescending(false, false, true);
    }
}
