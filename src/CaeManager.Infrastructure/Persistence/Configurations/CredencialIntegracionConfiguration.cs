using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class CredencialIntegracionConfiguration : IEntityTypeConfiguration<CredencialIntegracion>
{
    public void Configure(EntityTypeBuilder<CredencialIntegracion> builder)
    {
        builder.ToTable("CredencialesIntegracion");
        builder.HasKey(c => c.Id);

        // El cifrado de RefreshToken se configura en CaeManagerDbContext.OnModelCreating
        // (necesita el IDataProtector inyectado en el propio DbContext, mismo patrón que CredencialAccesoEmpresa)
        // — sin HasMaxLength aquí a propósito: el texto cifrado es más largo que el plano.

        builder.HasIndex(c => new { c.TenantId, c.ConexionIntegracionId }).IsUnique();
    }
}
