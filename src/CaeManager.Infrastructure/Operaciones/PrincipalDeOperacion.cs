using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Operaciones;

/// <summary>
/// La pregunta de la que depende la regla de emisión de la marca de principal (ADR-011 § 2.7,
/// enmienda 2026-10-08): una cartera de Gestor CAE que entra en una Asignación de Operación
/// sin principal vivo nace principal; si ya lo hay, nace sin marca. Una sola definición para
/// los dos escritores de cartera (<see cref="CatalogoIncorporacionCartera"/> y
/// <see cref="AsignacionesOperativasWriter"/>).
///
/// <para>
/// «Vivo» es no cerrado —incluye Suspendida y Programada—, el mismo predicado que el índice
/// único <c>IX_AsignacionesCartera_PrincipalPorOperacion</c>. Esta lectura es una cortesía,
/// no la garantía: dos emisiones simultáneas a Gestores CAE distintos leen las dos «no hay»
/// (el candado de cartera es por usuario y no las serializa) y es el índice el que deja pasar
/// una sola; la otra pierde la carrera y se reintenta.
/// </para>
///
/// <para>
/// La consulta se acota por la operación y se ejecuta siempre con el Tenant propietario como
/// Tenant activo, que es la posición desde la que la política RLS deja ver todas las carteras
/// de esa operación: quien llama ya la necesita para escribir la suya.
/// </para>
/// </summary>
internal static class PrincipalDeOperacion
{
    public static async Task<bool> HayPrincipalVivoAsync(
        CaeManagerDbContext dbContext, AsignacionOperacion operacion, CancellationToken cancellationToken)
    {
        // También entre las carteras del contexto aún sin guardar: una siembra emite varias
        // antes de su único guardado, y la consulta no las vería.
        var enElContexto = dbContext.ChangeTracker.Entries<AsignacionCartera>()
            .Any(e => e.State != EntityState.Deleted
                      && e.Entity.AsignacionOperacionId == operacion.Id
                      && e.Entity.EsPrincipal
                      && e.Entity.Estado != EstadoAsignacion.Cerrada);
        if (enElContexto) return true;
        if (dbContext.Entry(operacion).State == EntityState.Added) return false;

        return await dbContext.AsignacionesCartera
            .AnyAsync(c => c.AsignacionOperacionId == operacion.Id
                           && c.EsPrincipal
                           && c.Estado != EstadoAsignacion.Cerrada, cancellationToken);
    }
}
