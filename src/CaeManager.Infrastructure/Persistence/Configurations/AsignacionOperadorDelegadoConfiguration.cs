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
        builder.Property(a => a.MotivoRevocacion).HasMaxLength(AsignacionOperadorDelegado.LongitudMaximaMotivoRevocacion);

        // Un Operador Delegado no puede tener dos asignaciones VIGENTES a la
        // misma delegación (un único rol por Delegated Workspace). Las
        // revocadas quedan fuera del índice: son historial y no deben impedir
        // que se le conceda un rol delegable nuevo.
        builder.HasIndex(a => new { a.DelegacionTenantId, a.UsuarioId })
            .IsUnique()
            .HasFilter($"\"{nameof(AsignacionOperadorDelegado.RevocadaEnUtc)}\" IS NULL");
        builder.HasIndex(a => a.UsuarioId);

        // Sin HasQueryFilter: catálogo global de autorización, mismo
        // tratamiento que DelegacionTenant (y ModeloTenantTests prohíbe un
        // filtro en lo que no es EntidadConTenant). Las revocadas se ocultan
        // en otro sitio: CaeManagerDbContext.AsignacionesOperadorDelegado solo
        // expone las no revocadas, y la tabla completa solo se alcanza con
        // AsignacionesOperadorDelegadoConRevocadas.
    }
}
