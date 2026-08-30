using CaeManager.Domain.Auditoria;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RegistroAuditoriaConfiguration : IEntityTypeConfiguration<RegistroAuditoria>
{
    public void Configure(EntityTypeBuilder<RegistroAuditoria> builder)
    {
        builder.ToTable("RegistrosAuditoria");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.EntidadTipo).IsRequired().HasMaxLength(200);
        builder.Property(r => r.Accion).IsRequired().HasMaxLength(20);
        builder.Property(r => r.DatosAntes).HasColumnType("TEXT");
        builder.Property(r => r.DatosDespues).HasColumnType("TEXT");

        // Se guarda el nombre del enum, no su número, mismo criterio que el
        // resto del modelo: un valor intercalado en el futuro no debe
        // reinterpretar filas existentes — y aquí eso convertiría un acceso
        // normal en uno privilegiado.
        builder.Property(r => r.ViaAcceso).HasConversion<string>().HasMaxLength(20);

        // Con TenantId primero en los cuatro: el filtro global de EF Core
        // (RegistroAuditoria es EntidadConTenant) añade WHERE TenantId = ...
        // a TODA consulta real contra esta tabla, aunque ningún .Where() lo
        // escriba explícitamente — un índice que no empiece por esa columna
        // no puede servir de prefijo para ese filtro (auditoría Módulo 8).
        builder.HasIndex(r => new { r.TenantId, r.FechaUtc })
            .HasDatabaseName("IX_RegistrosAuditoria_TenantId_FechaUtc");
        builder.HasIndex(r => new { r.TenantId, r.EntidadTipo, r.EntidadId, r.FechaUtc })
            .HasDatabaseName("IX_RegistrosAuditoria_TenantId_EntidadTipo_EntidadId_FechaUtc");
        builder.HasIndex(r => new { r.TenantId, r.UsuarioId, r.FechaUtc })
            .HasDatabaseName("IX_RegistrosAuditoria_TenantId_UsuarioId_FechaUtc");

        // "¿Qué hizo realmente este administrador?" es la pregunta que una
        // impersonación obliga a poder responder, y no se contesta filtrando
        // por UsuarioId: ahí figurará el usuario simulado.
        builder.HasIndex(r => new { r.TenantId, r.ActorRealUsuarioId, r.FechaUtc })
            .HasFilter($"\"{nameof(RegistroAuditoria.ActorRealUsuarioId)}\" IS NOT NULL")
            .HasDatabaseName("IX_RegistrosAuditoria_TenantId_ActorRealUsuarioId_FechaUtc");

        // "¿Qué se tocó bajo esta delegación?" — hoy exigiría cruzar la
        // auditoría con la tabla de asignaciones a mano.
        builder.HasIndex(r => new { r.TenantId, r.ViaAccesoId })
            .HasFilter($"\"{nameof(RegistroAuditoria.ViaAccesoId)}\" IS NOT NULL")
            .HasDatabaseName("IX_RegistrosAuditoria_TenantId_ViaAccesoId");
    }
}
