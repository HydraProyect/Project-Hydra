using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AccesoPlataformaCaeEmpresaConfiguration : IEntityTypeConfiguration<AccesoPlataformaCaeEmpresa>
{
    public void Configure(EntityTypeBuilder<AccesoPlataformaCaeEmpresa> builder)
    {
        builder.ToTable("AccesosPlataformaCaeEmpresa");
        builder.HasKey(a => a.Id);

        // Usuario y Contrasena se cifran en CaeManagerDbContext.OnModelCreating (necesita el
        // IDataProtector del propio DbContext). Sin HasMaxLength: el texto cifrado es más largo
        // que el plano, y el límite lo pone el dominio sobre el valor plano.

        builder.HasIndex(a => new { a.TenantId, a.ClienteEmpresarialId, a.EmpresaId }).IsUnique();
        builder.HasIndex(a => new { a.TenantId, a.EmpresaId });

        // FKs compuestas con el Tenant (mismo patrón que RelacionEmpresarial): ni el Cliente
        // empresarial ni la Empresa pueden ser de otro Tenant.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.ClienteEmpresarialId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
