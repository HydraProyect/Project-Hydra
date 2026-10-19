using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class DelegacionEmpresaConfiguration : IEntityTypeConfiguration<DelegacionEmpresa>
{
    public void Configure(EntityTypeBuilder<DelegacionEmpresa> builder)
    {
        builder.ToTable("DelegacionesEmpresa");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Nombre).HasMaxLength(DelegacionEmpresa.LongitudMaximaNombre).IsRequired();
        builder.Property(d => d.ContactoReclamacionNombre).HasMaxLength(DelegacionEmpresa.LongitudMaximaContactoNombre);
        builder.Property(d => d.ContactoReclamacionCorreo).HasMaxLength(DelegacionEmpresa.LongitudMaximaContactoCorreo);
        builder.Property(d => d.ContactoReclamacionTelefono).HasMaxLength(DelegacionEmpresa.LongitudMaximaContactoTelefono);

        // FK compuesta con TenantId: una Delegación no puede colgar de una Empresa de otro Tenant.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(d => new { d.TenantId, d.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Clave alterna que usa la FK de Trabajador (TenantId, EmpresaId, DelegacionEmpresaId): la Delegación de un
        // Trabajador tiene que ser de su misma Empresa, y lo garantiza la base de datos.
        builder.HasAlternateKey(d => new { d.TenantId, d.EmpresaId, d.Id });
        builder.HasIndex(d => new { d.TenantId, d.EmpresaId });
    }
}
