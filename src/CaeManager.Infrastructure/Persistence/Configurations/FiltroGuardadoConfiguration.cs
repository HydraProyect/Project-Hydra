using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class FiltroGuardadoConfiguration : IEntityTypeConfiguration<FiltroGuardado>
{
    public void Configure(EntityTypeBuilder<FiltroGuardado> builder)
    {
        builder.ToTable("FiltrosGuardados");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Pantalla).HasMaxLength(50).IsRequired();
        builder.Property(f => f.Nombre).HasMaxLength(100).IsRequired();
        builder.Property(f => f.ValoresJson).IsRequired();

        builder.HasIndex(f => new { f.UsuarioId, f.Pantalla });

        // Sin HasQueryFilter: catálogo global, mismo tratamiento que
        // PreferenciaDashboardUsuario — preferencia de un usuario, no un
        // dato del tenant.
    }
}
