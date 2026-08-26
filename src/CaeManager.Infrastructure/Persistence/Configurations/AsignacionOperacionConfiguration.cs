using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AsignacionOperacionConfiguration : IEntityTypeConfiguration<AsignacionOperacion>
{
    public void Configure(EntityTypeBuilder<AsignacionOperacion> builder)
    {
        builder.ToTable("AsignacionesOperacion");
        builder.HasKey(a => a.Id);

        // Clave alternativa que hace posible la FK compuesta de AsignacionCartera.
        // Es lo que convierte "una cartera pertenece al mismo propietario que su
        // operación" de invariante de dominio en invariante de la base de datos:
        // con ella, una cartera cuyo PropietarioTenantId no case con el de su
        // operación es irrepresentable, no solo inválida.
        builder.HasAlternateKey(a => new { a.Id, a.PropietarioTenantId });

        builder.Property(a => a.PropietarioTenantId).IsRequired();
        builder.Property(a => a.OperadorTenantId).IsRequired();
        builder.Property(a => a.EsRaiz).IsRequired();
        builder.Property(a => a.VigenciaDesde).IsRequired();

        // Se guarda el nombre del enum, no su número, por el mismo motivo que en
        // DelegacionTenant.Proposito: un valor intercalado en el futuro no debe
        // reinterpretar filas existentes — aquí eso convertiría una operación
        // Outbound en una Inbound, o una asignación Cerrada en Suspendida.
        builder.Property(a => a.Servicio).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.Estado).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.MotivoCierre).HasConversion<string>().HasMaxLength(30);

        // Lookup principal: "¿qué opera este tenant en este servicio?".
        builder.HasIndex(a => new { a.PropietarioTenantId, a.Servicio, a.Estado });

        // "Mis workspaces": las operaciones donde YO soy el operador. Se
        // consulta desde el tenant de origen del usuario, nunca desde el tenant
        // activo — dentro de un workspace delegado el tenant activo es el del
        // propietario y esta consulta no devolvería nada.
        builder.HasIndex(a => new { a.OperadorTenantId, a.Estado });

        builder.HasIndex(a => new { a.PropietarioTenantId, a.AmbitoRelacionClienteId })
            .HasFilter($"\"{nameof(AsignacionOperacion.AmbitoRelacionClienteId)}\" IS NOT NULL")
            .HasDatabaseName("IX_AsignacionesOperacion_AmbitoRelacionCliente");

        // --- Backstop físico de la validación de solape (ADR-011 § 4.4) ---
        //
        // La validación de alta vive en el comando, pero dos altas concurrentes
        // la ganan las dos: entre el SELECT que comprueba y el INSERT que
        // escribe no hay nada que las serialice. Estos índices parciales cierran
        // esa ventana en la base de datos, igual que
        // IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa hizo con las
        // asignaciones trabajador/centro tras un 23505 real en producción.
        //
        // Van sobre Estado='Vigente' a propósito: una Programada todavía no
        // responde del ámbito y debe poder convivir con la Vigente a la que va a
        // sustituir (la ventana de traspaso), y una Cerrada es historia.
        //
        // Solo cubren las dos combinaciones de ámbito que F1 emite. Cuando se
        // habiliten las dimensiones diferidas harán falta más, y el índice de
        // AsignacionCartera por relación tendrá que retirarse (ver su
        // configuración).

        // Una sola raíz vigente por (propietario, servicio).
        builder.HasIndex(
                a => new { a.PropietarioTenantId, a.Servicio },
                "IX_AsignacionesOperacion_RaizVigente")
            .IsUnique()
            .HasFilter($"\"{nameof(AsignacionOperacion.EsRaiz)}\" AND \"{nameof(AsignacionOperacion.Estado)}\" = 'Vigente'");

        // Una sola delegación total vigente por (propietario, servicio): no
        // pueden dos operadores externos llevar "todo" a la vez. Repartir exige
        // ámbitos explícitos.
        builder.HasIndex(
                a => new { a.PropietarioTenantId, a.Servicio },
                "IX_AsignacionesOperacion_DelegacionTotalVigente")
            .IsUnique()
            .HasFilter(
                $"NOT \"{nameof(AsignacionOperacion.EsRaiz)}\" " +
                $"AND \"{nameof(AsignacionOperacion.Estado)}\" = 'Vigente' " +
                $"AND \"{nameof(AsignacionOperacion.AmbitoRelacionClienteId)}\" IS NULL " +
                $"AND \"{nameof(AsignacionOperacion.AmbitoCentroId)}\" IS NULL " +
                $"AND \"{nameof(AsignacionOperacion.AmbitoTrabajadorId)}\" IS NULL " +
                $"AND \"{nameof(AsignacionOperacion.AmbitoProyectoId)}\" IS NULL");

        // Un solo responsable vigente por relación con cliente.
        builder.HasIndex(
                a => new { a.PropietarioTenantId, a.Servicio, a.AmbitoRelacionClienteId },
                "IX_AsignacionesOperacion_ResponsableRelacionVigente")
            .IsUnique()
            .HasFilter(
                $"\"{nameof(AsignacionOperacion.Estado)}\" = 'Vigente' " +
                $"AND \"{nameof(AsignacionOperacion.AmbitoRelacionClienteId)}\" IS NOT NULL " +
                $"AND \"{nameof(AsignacionOperacion.AmbitoCentroId)}\" IS NULL " +
                $"AND \"{nameof(AsignacionOperacion.AmbitoTrabajadorId)}\" IS NULL " +
                $"AND \"{nameof(AsignacionOperacion.AmbitoProyectoId)}\" IS NULL");

        // --- FKs compuestas del ámbito ---
        //
        // (PropietarioTenantId, AmbitoXxxId) contra la clave alternativa
        // (TenantId, Id) que cada agregado ya expone. Es lo que hace
        // FÍSICAMENTE IMPOSIBLE que un ámbito apunte a datos de otro tenant —
        // la fuga cross-tenant más peligrosa de este diseño, cerrada en el
        // esquema y no en una comprobación que alguien pueda olvidar.
        //
        // MATCH SIMPLE de Postgres: con la columna de ámbito a NULL la
        // restricción no se comprueba, que es justo lo que hace falta para un
        // ámbito que no usa esa dimensión. Mismo mecanismo que ya usan
        // Trabajador y Vehiculo para su XOR empresa/subcontrata.
        //
        // Restrict, como todas las FKs del núcleo: nada se borra en cascada
        // desde autorización.
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

        // Sin FK hacia Tenants para Propietario/Operador: mismo tratamiento que
        // DelegacionTenant, que tampoco la tiene.
        //
        // Sin HasQueryFilter: catálogo global de autorización. Que esté fuera del
        // filtro NO la hace legible sin restricción — la política de lectura por
        // posición del llamante vive en Application y la vigila un test de
        // arquitectura.
    }
}
