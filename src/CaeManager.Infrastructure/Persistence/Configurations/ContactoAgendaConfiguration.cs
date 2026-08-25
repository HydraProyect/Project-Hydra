using CaeManager.Domain.Centros;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ContactoAgendaConfiguration : IEntityTypeConfiguration<ContactoAgenda>
{
    public void Configure(EntityTypeBuilder<ContactoAgenda> builder)
    {
        builder.ToTable("ContactosAgenda");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Nombre).IsRequired().HasMaxLength(ContactoAgenda.LongitudMaximaNombre);
        builder.Property(c => c.Email).IsRequired().HasMaxLength(ContactoAgenda.LongitudMaximaEmail);
        builder.Property(c => c.Telefono).HasMaxLength(ContactoAgenda.LongitudMaximaTelefono);
        builder.Property(c => c.Cargo).HasMaxLength(ContactoAgenda.LongitudMaximaCargo);
        builder.Property(c => c.Notas).HasMaxLength(ContactoAgenda.LongitudMaximaNotas);

        // Un índice por propietario: la agenda siempre se lee "la de este
        // Cliente/Empresa/Subcontrata/Centro", nunca la tabla entera.
        builder.HasIndex(c => new { c.TenantId, c.ClienteId });
        builder.HasIndex(c => new { c.TenantId, c.EmpresaId });
        builder.HasIndex(c => new { c.TenantId, c.SubcontrataId });
        builder.HasIndex(c => new { c.TenantId, c.CentroId });

        // Restrict como el resto de referencias entre agregados de este
        // repositorio: borrar un Cliente con agenda debe fallar de forma
        // visible, no llevarse los contactos por delante en silencio.
        //
        // F3 (verificación del modelo real) — a diferencia de Centro/Documento,
        // estas tres FKs NO eran compuestas (TenantId, Id) antes de F3 —
        // apuntaban solo a Id (PK simple). Se conserva ese estilo al
        // repuntear (cambio mecánico, no se introduce el patrón compuesto
        // aquí — corrige la caracterización de
        // f3-revision-adversaria-2026-08-25.md punto 3, que asumía FK
        // compuesta uniforme en las 4 entidades).
        builder.HasOne<Empresa>().WithMany().HasForeignKey(c => c.ClienteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Empresa>().WithMany().HasForeignKey(c => c.EmpresaId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Empresa>().WithMany().HasForeignKey(c => c.SubcontrataId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Centro>().WithMany().HasForeignKey(c => c.CentroId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(c => c.TiposDocumento)
            .WithOne()
            .HasForeignKey(t => t.ContactoAgendaId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.TiposDocumento).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(c => c.Roles)
            .WithOne()
            .HasForeignKey(r => r.ContactoAgendaId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);

        // El CHECK XOR del propietario polimórfico se declara en la migración
        // (mismo criterio que Documentos: EF no genera num_nonnulls).
        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

public class ContactoAgendaTipoDocumentoConfiguration : IEntityTypeConfiguration<ContactoAgendaTipoDocumento>
{
    public void Configure(EntityTypeBuilder<ContactoAgendaTipoDocumento> builder)
    {
        builder.ToTable("ContactosAgendaTiposDocumento");
        builder.HasKey(t => t.Id);

        builder.HasIndex(t => t.ContactoAgendaId);

        // Único por contacto: marcar dos veces el mismo tipo no significa nada.
        builder.HasIndex(t => new { t.TenantId, t.ContactoAgendaId, t.TipoDocumentoId }).IsUnique();

        builder.HasOne<TipoDocumento>()
            .WithMany()
            .HasForeignKey(t => t.TipoDocumentoId)
            .OnDelete(DeleteBehavior.Cascade);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

public class ContactoAgendaRolConfiguration : IEntityTypeConfiguration<ContactoAgendaRol>
{
    public void Configure(EntityTypeBuilder<ContactoAgendaRol> builder)
    {
        builder.ToTable("ContactosAgendaRoles");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Rol).IsRequired().HasConversion<string>();

        builder.HasIndex(r => r.ContactoAgendaId);

        // Único por contacto: marcar el mismo rol dos veces no significa nada.
        builder.HasIndex(r => new { r.TenantId, r.ContactoAgendaId, r.Rol }).IsUnique();

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
