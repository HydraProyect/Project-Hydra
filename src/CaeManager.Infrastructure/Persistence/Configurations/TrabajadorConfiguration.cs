using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TrabajadorConfiguration : IEntityTypeConfiguration<Trabajador>
{
    public void Configure(EntityTypeBuilder<Trabajador> builder)
    {
        // Auditoría Módulo 5, hueco arquitectónico: un Trabajador es de
        // Empresa O de Subcontrata, nunca ambas ni ninguna — hasta ahora el
        // dominio lo garantizaba (DeEmpresa/DeSubcontrata) pero el esquema
        // admitía las dos columnas nulas o las dos informadas. El CHECK deja
        // la intención escrita en la base, no solo en el constructor.
        builder.ToTable("Trabajadores", t => t.HasCheckConstraint(
            "CK_Trabajadores_EmpresaXorSubcontrata",
            "(\"EmpresaId\" IS NULL) <> (\"SubcontrataId\" IS NULL)"));
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Nombre).IsRequired().HasMaxLength(Trabajador.LongitudMaximaNombre);
        builder.Property(t => t.Apellidos).IsRequired().HasMaxLength(Trabajador.LongitudMaximaApellidos);
        builder.Property(t => t.Alias).HasMaxLength(Trabajador.LongitudMaximaAlias);
        builder.Property(t => t.Puesto).HasMaxLength(Trabajador.LongitudMaximaPuesto);
        builder.Property(t => t.Dni).HasMaxLength(Trabajador.LongitudMaximaDni);
        builder.Property(t => t.Email).HasMaxLength(Trabajador.LongitudMaximaEmail);
        builder.Property(t => t.Telefono).HasMaxLength(Trabajador.LongitudMaximaTelefono);
        builder.Property(t => t.Observaciones).HasMaxLength(Trabajador.LongitudMaximaObservaciones);

        // Parcial, no total (auditoría Módulo 5, hallazgo crítico 9/9): un
        // trabajador anonimizado tiene Dni null, y sin excluir los nulos el
        // segundo anonimizado del mismo tenant chocaría contra el primero —
        // Postgres SÍ considera NULL = NULL como no-coincidente en un índice
        // único normal, pero aquí Dni dejó de ser NOT NULL, así que el filtro
        // lo deja explícito en vez de confiar en ese comportamiento implícito.
        builder.HasIndex(t => new { t.TenantId, t.Dni })
               .IsUnique()
               .HasFilter($"\"{nameof(Trabajador.Dni)}\" IS NOT NULL");
        // Resolver "de quién es este WhatsApp entrante" busca por teléfono en
        // la ingesta, no por Id — sin índice sería un scan por cada mensaje.
        builder.HasIndex(t => new { t.TenantId, t.Telefono });
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

        // F3b-Subcontrata — SubcontrataId repunta contra Empresas también.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(t => new { t.TenantId, t.SubcontrataId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Prerequisito de las FKs que Asignacion/Documento/... declaran hacia Trabajador.
        builder.HasIndex(t => new { t.TenantId, t.Id }).IsUnique();

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
