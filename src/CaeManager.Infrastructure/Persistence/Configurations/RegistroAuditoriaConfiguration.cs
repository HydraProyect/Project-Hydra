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

        builder.HasIndex(r => new { r.EntidadTipo, r.EntidadId });
        builder.HasIndex(r => r.FechaUtc);

        // "¿Qué hizo realmente este administrador?" es la pregunta que una
        // impersonación obliga a poder responder, y no se contesta filtrando
        // por UsuarioId: ahí figurará el usuario simulado.
        builder.HasIndex(r => r.ActorRealUsuarioId)
            .HasFilter($"\"{nameof(RegistroAuditoria.ActorRealUsuarioId)}\" IS NOT NULL");

        // "¿Qué se tocó bajo esta delegación?" — hoy exigiría cruzar la
        // auditoría con la tabla de asignaciones a mano.
        builder.HasIndex(r => r.ViaAccesoId)
            .HasFilter($"\"{nameof(RegistroAuditoria.ViaAccesoId)}\" IS NOT NULL");
    }
}
