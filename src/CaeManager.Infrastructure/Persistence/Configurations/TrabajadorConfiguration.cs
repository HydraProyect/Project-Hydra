using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TrabajadorConfiguration : IEntityTypeConfiguration<Trabajador>
{
    public void Configure(EntityTypeBuilder<Trabajador> builder)
    {
        builder.ToTable("Trabajadores");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Nombre).IsRequired().HasMaxLength(Trabajador.LongitudMaximaNombre);
        builder.Property(t => t.Apellidos).IsRequired().HasMaxLength(Trabajador.LongitudMaximaApellidos);
        builder.Property(t => t.Alias).HasMaxLength(Trabajador.LongitudMaximaAlias);
        builder.Property(t => t.Dni).IsRequired().HasMaxLength(Trabajador.LongitudMaximaDni);
        builder.Property(t => t.Email).HasMaxLength(Trabajador.LongitudMaximaEmail);
        builder.Property(t => t.Observaciones).HasMaxLength(Trabajador.LongitudMaximaObservaciones);

        builder.HasIndex(t => new { t.TenantId, t.Dni }).IsUnique();
        builder.HasIndex(t => t.EmpresaId);
        builder.HasIndex(t => t.SubcontrataId);

        // FKs reales — ver P0-1 de docs/business/MATURITY_REVIEW.md. Un
        // Trabajador es de Empresa O de Subcontrata (nunca ambas, ver
        // Trabajador.DeEmpresa/DeSubcontrata): con una columna nula la FK
        // compuesta correspondiente queda sin comprobar (MATCH SIMPLE de
        // Postgres), que es exactamente el comportamiento que hace falta aquí.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(t => new { t.TenantId, t.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Subcontrata>().WithMany()
            .HasForeignKey(t => new { t.TenantId, t.SubcontrataId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Prerequisito de las FKs que Asignacion/Documento/Evaluacion/... declaran hacia Trabajador.
        builder.HasIndex(t => new { t.TenantId, t.Id }).IsUnique();

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
