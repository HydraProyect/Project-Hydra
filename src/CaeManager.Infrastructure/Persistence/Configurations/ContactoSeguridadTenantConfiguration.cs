using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ContactoSeguridadTenantConfiguration : IEntityTypeConfiguration<ContactoSeguridadTenant>
{
    public void Configure(EntityTypeBuilder<ContactoSeguridadTenant> builder)
    {
        builder.ToTable("ContactosSeguridadTenant", t =>
        {
            t.HasCheckConstraint(
                "CK_ContactosSeguridadTenant_Estado",
                "\"Estado\" IN ('Propuesto', 'Validado', 'Rechazado', 'Sustituido')");
            // Lo que cada estado exige de quién resolvió y por qué: validar y rechazar dejan actor e
            // instante; solo un rechazo lleva motivo.
            t.HasCheckConstraint(
                "CK_ContactosSeguridadTenant_ResolucionCoherente",
                "(\"Estado\" IN ('Validado', 'Rechazado')) = (\"ResueltoPorUsuarioId\" IS NOT NULL AND \"ResueltoEnUtc\" IS NOT NULL)"
                + " OR (\"Estado\" = 'Sustituido' AND \"ResueltoPorUsuarioId\" IS NOT NULL AND \"ResueltoEnUtc\" IS NOT NULL)");
            t.HasCheckConstraint(
                "CK_ContactosSeguridadTenant_MotivoSoloAlRechazar",
                "(\"Estado\" = 'Rechazado') = (\"MotivoRechazo\" IS NOT NULL)");
            t.HasCheckConstraint(
                "CK_ContactosSeguridadTenant_InstanteSustitucion",
                "(\"Estado\" = 'Sustituido') = (\"SustitucionEnUtc\" IS NOT NULL)");
        });
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Nombre).IsRequired().HasMaxLength(ContactoSeguridadTenant.LongitudMaximaNombre);
        builder.Property(c => c.Correo).IsRequired().HasMaxLength(ContactoSeguridadTenant.LongitudMaximaCorreo);
        builder.Property(c => c.Telefono).HasMaxLength(ContactoSeguridadTenant.LongitudMaximaTelefono);
        builder.Property(c => c.Estado).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.PropuestoPorUsuarioId).IsRequired();
        builder.Property(c => c.PropuestoEnUtc).IsRequired();
        builder.Property(c => c.MotivoRechazo).HasMaxLength(ContactoSeguridadTenant.LongitudMaximaMotivoRechazo);

        // Como mucho una propuesta pendiente y un contacto vigente por Tenant. Índice sobre (TenantId, Estado)
        // y no solo TenantId: así EF ve que la sustitución de la propuesta pendiente libera el valor que ocupa
        // la nueva y ordena el UPDATE antes del INSERT en el mismo SaveChanges.
        builder.HasIndex(c => new { c.TenantId, c.Estado })
            .IsUnique()
            .HasFilter("\"Estado\" IN ('Propuesto', 'Validado')")
            .HasDatabaseName("IX_ContactosSeguridadTenant_UnoPropuestoYUnoValidadoPorTenant");

        // La cola de propuestas pendientes de la pantalla de Plataforma.
        builder.HasIndex(c => new { c.Estado, c.PropuestoEnUtc });

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
