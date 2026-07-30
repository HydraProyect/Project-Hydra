using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CredencialAccesoSubcontrataConfiguration : IEntityTypeConfiguration<CredencialAccesoSubcontrata>
{
    public void Configure(EntityTypeBuilder<CredencialAccesoSubcontrata> builder)
    {
        builder.ToTable("CredencialesAccesoSubcontrata");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.UrlAcceso).HasMaxLength(CredencialAccesoSubcontrata.LongitudMaximaUrlAcceso);
        builder.Property(c => c.CampoEmpresa).HasMaxLength(CredencialAccesoSubcontrata.LongitudMaximaCampoEmpresa);
        builder.Property(c => c.Notas).HasMaxLength(CredencialAccesoSubcontrata.LongitudMaximaNotas);

        // El cifrado de Usuario/Contrasena se configura en CaeManagerDbContext.OnModelCreating,
        // porque necesita el IDataProtector inyectado en el propio DbContext (mismo patrón que CredencialAccesoEmpresa).

        builder.HasIndex(c => new { c.TenantId, c.SubcontrataId }).IsUnique();
    }
}
