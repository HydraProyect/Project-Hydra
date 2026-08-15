using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class FirmaGuardadaUsuarioConfiguration : IEntityTypeConfiguration<FirmaGuardadaUsuario>
{
    public void Configure(EntityTypeBuilder<FirmaGuardadaUsuario> builder)
    {
        builder.ToTable("FirmasGuardadasUsuario");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.ImagenUrl).IsRequired().HasMaxLength(FirmaGuardadaUsuario.LongitudMaximaImagenUrl);

        // Un usuario, una firma guardada — se reemplaza al re-guardar.
        builder.HasIndex(f => new { f.TenantId, f.UsuarioId }).IsUnique();
    }
}
