using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class PreferenciaDashboardUsuarioConfiguration : IEntityTypeConfiguration<PreferenciaDashboardUsuario>
{
    public void Configure(EntityTypeBuilder<PreferenciaDashboardUsuario> builder)
    {
        builder.ToTable("PreferenciasDashboardUsuario");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.UsuarioId).IsRequired();
        builder.Property(p => p.CodigosKpiSeleccionados).HasColumnType("text[]").IsRequired();

        // Una fila por usuario.
        builder.HasIndex(p => p.UsuarioId).IsUnique();

        // Sin HasQueryFilter: catálogo global, mismo tratamiento que
        // AsignacionOperadorDelegado — la preferencia de un usuario aplica
        // sin importar qué tenant tenga activo.
    }
}
