using CaeManager.Domain.Seguridad;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RegistroInicioSesionConfiguration : IEntityTypeConfiguration<RegistroInicioSesion>
{
    public void Configure(EntityTypeBuilder<RegistroInicioSesion> builder)
    {
        builder.ToTable("RegistrosInicioSesion");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.UsuarioId).IsRequired();
        builder.Property(r => r.OcurridoEnUtc).IsRequired();
        // Por nombre, no por ordinal: un valor intercalado en el futuro no
        // reinterpreta filas existentes (mismo criterio que la auditoría).
        builder.Property(r => r.Metodo).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.Resultado).IsRequired().HasConversion<string>().HasMaxLength(30);
        // Banderas combinables: entero de bits; los valores no se reutilizan.
        builder.Property(r => r.Senales).IsRequired().HasConversion<int>();
        builder.Property(r => r.DireccionIp).HasMaxLength(45);
        builder.Property(r => r.PaisIso).HasMaxLength(2).IsFixedLength();
        builder.Property(r => r.Ciudad).HasMaxLength(100);
        builder.Property(r => r.HuellaDispositivo).HasMaxLength(64);
        builder.Property(r => r.Dispositivo).HasMaxLength(60);

        builder.Ignore(r => r.EsSospechoso);

        // Las dos consultas: el historial de una cuenta (detección y, más
        // adelante, su pantalla) y las alertas recientes del Tenant.
        builder.HasIndex(r => new { r.TenantId, r.UsuarioId, r.OcurridoEnUtc });
        builder.HasIndex(r => new { r.TenantId, r.OcurridoEnUtc });
        // La purga por antigüedad recorre todos los Tenants.
        builder.HasIndex(r => r.OcurridoEnUtc);

        // Sin FK hacia AspNetUsers: el rastro sobrevive a la baja de la cuenta
        // hasta su retención, igual que RegistroAuditoria.
        // RLS, permisos y purga: migración AnadeRegistroIniciosSesion.
    }
}

public class PoliticaPaisesInicioSesionConfiguration : IEntityTypeConfiguration<PoliticaPaisesInicioSesion>
{
    public void Configure(EntityTypeBuilder<PoliticaPaisesInicioSesion> builder)
    {
        builder.ToTable("PoliticasPaisesInicioSesion");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Activa).IsRequired().HasDefaultValue(false);
        builder.Property(p => p.ActualizadaEnUtc).IsRequired();
        builder.Property<List<string>>("_paisesPermitidos")
            .HasColumnName("PaisesPermitidos")
            .HasColumnType("text[]")
            .IsRequired();
        builder.Ignore(p => p.PaisesPermitidos);

        // Una por Tenant.
        builder.HasIndex(p => p.TenantId).IsUnique();
    }
}
