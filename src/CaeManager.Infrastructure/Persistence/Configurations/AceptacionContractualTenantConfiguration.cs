using CaeManager.Domain.Cumplimiento;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AceptacionContractualTenantConfiguration : IEntityTypeConfiguration<AceptacionContractualTenant>
{
    public void Configure(EntityTypeBuilder<AceptacionContractualTenant> builder)
    {
        builder.ToTable("AceptacionesContractualesTenant");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Documento).IsRequired().HasMaxLength(AceptacionContractualTenant.LongitudMaximaDocumento);
        builder.Property(a => a.Version).IsRequired().HasMaxLength(AceptacionContractualTenant.LongitudMaximaVersion);
        builder.Property(a => a.HashSha256).IsRequired().HasMaxLength(AceptacionContractualTenant.LongitudHash).IsFixedLength();
        builder.Property(a => a.Ip).IsRequired().HasMaxLength(AceptacionContractualTenant.LongitudMaximaIp);
        builder.Property(a => a.ImportePermanencia)
            .HasPrecision(AceptacionContractualTenant.PrecisionImporte, AceptacionContractualTenant.EscalaImporte);

        builder.HasIndex(a => new { a.TenantId, a.Documento, a.AceptadaEnUtc });

        // Idempotencia del alta: una Solicitud de alta deja una sola aceptación por documento.
        builder.HasIndex(a => new { a.TenantId, a.SolicitudAltaId, a.Documento })
            .IsUnique()
            .HasFilter("\"SolicitudAltaId\" IS NOT NULL");

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}

public class AcuseAceptacionContractualTenantConfiguration : IEntityTypeConfiguration<AcuseAceptacionContractualTenant>
{
    public void Configure(EntityTypeBuilder<AcuseAceptacionContractualTenant> builder)
    {
        builder.ToTable("AcusesAceptacionContractualTenant");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Fase).IsRequired().HasConversion<string>()
            .HasMaxLength(AcuseAceptacionContractualTenant.LongitudMaximaFase);

        builder.HasIndex(a => new { a.AceptacionContractualTenantId, a.TenantId });

        // Un envío deja como mucho una fila por aceptación y fase: ni dos «Enviado» del mismo envío ni un
        // resultado repetido por un reintento del guardado.
        builder.HasIndex(a => new { a.TenantId, a.EnvioId, a.AceptacionContractualTenantId, a.Fase }).IsUnique();

        // Sin clave foránea a propósito: las dos tablas solo admiten INSERT y el acuse solo se crea a
        // partir de una aceptación ya leída del mismo Tenant (EnvioAcuseAceptacionContractual). Una FK
        // escrita solo en SQL, que EF no conociera, rompería el orden de borrado de RetiradaTenantDemoService.
    }
}
