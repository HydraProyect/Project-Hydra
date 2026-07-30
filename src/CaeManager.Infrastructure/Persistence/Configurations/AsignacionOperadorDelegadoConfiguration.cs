using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AsignacionOperadorDelegadoConfiguration : IEntityTypeConfiguration<AsignacionOperadorDelegado>
{
    public void Configure(EntityTypeBuilder<AsignacionOperadorDelegado> builder)
    {
        builder.ToTable("AsignacionesOperadorDelegado");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.DelegacionTenantId).IsRequired();
        builder.Property(a => a.UsuarioId).IsRequired();
        builder.Property(a => a.Rol).IsRequired().HasMaxLength(50);

        // Un Operador Delegado no puede tener dos asignaciones a la misma
        // delegación (un único rol por Delegated Workspace).
        builder.HasIndex(a => new { a.DelegacionTenantId, a.UsuarioId }).IsUnique();
        builder.HasIndex(a => a.UsuarioId);

        // Sin HasQueryFilter: catálogo global de autorización, mismo
        // tratamiento que DelegacionTenant.
    }
}
