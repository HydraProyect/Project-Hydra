using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CredencialAccesoEmpresaConfiguration : IEntityTypeConfiguration<CredencialAccesoEmpresa>
{
    public void Configure(EntityTypeBuilder<CredencialAccesoEmpresa> builder)
    {
        builder.ToTable("CredencialesAccesoEmpresa");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.UrlAcceso).HasMaxLength(CredencialAccesoEmpresa.LongitudMaximaUrlAcceso);
        builder.Property(c => c.CampoEmpresa).HasMaxLength(CredencialAccesoEmpresa.LongitudMaximaCampoEmpresa);

        // El cifrado de Usuario/Contrasena se configura en CaeManagerDbContext.OnModelCreating,
        // porque necesita el IDataProtector inyectado en el propio DbContext (mismo patrón que PlataformaAcceso).

        builder.HasIndex(c => new { c.TenantId, c.EmpresaId }).IsUnique();
    }
}
