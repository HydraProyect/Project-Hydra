using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AsignacionCarteraConfiguration : IEntityTypeConfiguration<AsignacionCartera>
{
    public void Configure(EntityTypeBuilder<AsignacionCartera> builder)
    {
        builder.ToTable("AsignacionesCartera");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.AsignacionOperacionId).IsRequired();
        builder.Property(a => a.PropietarioTenantId).IsRequired();
        builder.Property(a => a.OperadorTenantId).IsRequired();
        builder.Property(a => a.UsuarioId).IsRequired();
        builder.Property(a => a.Rol).HasMaxLength(AsignacionCartera.LongitudMaximaRol);
        builder.Property(a => a.VigenciaDesde).IsRequired();
        builder.Property(a => a.Estado).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.MotivoCierre).HasConversion<string>().HasMaxLength(30);

        // "¿Qué lleva este usuario?" — la pregunta que hace el servicio de
        // alcance de datos al principio de cada circuito.
        builder.HasIndex(a => new { a.UsuarioId, a.Estado });
        // Sin índice propio por AsignacionOperacionId: la FK compuesta hacia la
        // operación ya crea uno que lo lleva de primera columna.

        // Restricción de TRANSICIÓN, no invariante de dominio. Residuo del reparto por Cliente empresarial,
        // retirado con D-7 (2026-10-02): ningún productor escribe ya una cartera por Cliente empresarial y
        // la migración ConvierteCarterasPorClienteATenantEntero cerró las que existían. El índice sigue
        // porque una instancia anterior al despliegue todavía podría escribir una; se retira, junto a la
        // lectura de ese ámbito, cuando ya no pueda haber instancias antiguas.
        //
        // GLOBAL por propietario-cliente, no por operación (auditoría Módulo 5, hallazgo crítico 3/9):
        // como mucho una cartera vigente por Cliente empresarial y Tenant propietario.
        builder.HasIndex(
                a => new { a.PropietarioTenantId, a.AmbitoRelacionClienteId },
                "IX_AsignacionesCartera_ResponsableRelacionVigente")
            .IsUnique()
            .HasFilter(
                $"\"{nameof(AsignacionCartera.Estado)}\" = 'Vigente' " +
                $"AND \"{nameof(AsignacionCartera.AmbitoRelacionClienteId)}\" IS NOT NULL " +
                $"AND \"{nameof(AsignacionCartera.AmbitoCentroId)}\" IS NULL " +
                $"AND \"{nameof(AsignacionCartera.AmbitoTrabajadorId)}\" IS NULL " +
                $"AND \"{nameof(AsignacionCartera.AmbitoProyectoId)}\" IS NULL");

        // Un usuario no puede tener dos carteras universales vigentes sobre la
        // misma operación — es el invariante que hoy impone el índice único
        // (DelegacionTenantId, UsuarioId) de AsignacionOperadorDelegado.
        builder.HasIndex(
                a => new { a.AsignacionOperacionId, a.UsuarioId },
                "IX_AsignacionesCartera_UsuarioUniversalVigente")
            .IsUnique()
            .HasFilter(
                $"\"{nameof(AsignacionCartera.Estado)}\" = 'Vigente' " +
                $"AND \"{nameof(AsignacionCartera.AmbitoRelacionClienteId)}\" IS NULL " +
                $"AND \"{nameof(AsignacionCartera.AmbitoCentroId)}\" IS NULL " +
                $"AND \"{nameof(AsignacionCartera.AmbitoTrabajadorId)}\" IS NULL " +
                $"AND \"{nameof(AsignacionCartera.AmbitoProyectoId)}\" IS NULL");

        // La FK que ata la cartera a su operación INCLUYENDO el propietario.
        // Con la clave alternativa (Id, PropietarioTenantId) de la operación,
        // una cartera cuyo propietario no sea el de su operación no se puede
        // ni escribir. Es el endurecimiento que convierte esa coherencia en
        // garantía de la base de datos y no en una comprobación de dominio.
        builder.HasOne<AsignacionOperacion>().WithMany()
            .HasForeignKey(a => new { a.AsignacionOperacionId, a.PropietarioTenantId })
            .HasPrincipalKey(o => new { o.Id, o.PropietarioTenantId })
            .OnDelete(DeleteBehavior.Restrict);

        // FKs compuestas del ámbito — mismo mecanismo y mismo motivo que en
        // AsignacionOperacion: imposibilitan físicamente apuntar a datos de
        // otro tenant.
        // F3b — AmbitoRelacionClienteId repunta contra Empresas (ver CentroConfiguration).
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(a => new { a.PropietarioTenantId, a.AmbitoRelacionClienteId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(a => new { a.PropietarioTenantId, a.AmbitoCentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Trabajador>().WithMany()
            .HasForeignKey(a => new { a.PropietarioTenantId, a.AmbitoTrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Proyecto>().WithMany()
            .HasForeignKey(a => new { a.PropietarioTenantId, a.AmbitoProyectoId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Sin HasQueryFilter: catálogo global de autorización, igual que la
        // operación de la que cuelga.
    }
}
