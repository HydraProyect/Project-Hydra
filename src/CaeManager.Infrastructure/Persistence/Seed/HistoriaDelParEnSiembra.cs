using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Lo que una siembra de demostración mira antes de escribir nada sobre una delegación de Operador CAE externo
/// (ronda 2 de Codex en la PR #2237, media 6; decisión de la coordinadora). Una siembra corre en cada arranque, no es
/// un acto explícito: si el par (Tenant propietario, Operador CAE externo) ya tiene historia, no crea filas heredadas,
/// no reactiva la delegación y no abre operación. Lo que fue no vuelve solo, tampoco por la siembra: antes comprobaba
/// solo la ausencia de una fila viva, y tras una revocación (o tras la migración, que revoca las filas heredadas
/// previas) volvía a insertar las mismas filas y a abrir la operación.
/// </summary>
internal static class HistoriaDelParEnSiembra
{
    /// <summary>
    /// Si el par de <paramref name="delegacion"/> tiene historia que la siembra no puede reponer: alguna revocación
    /// contada en cualquier delegación del par, alguna fila heredada revocada, o alguna Asignación de Operación Outbound
    /// externa sin ninguna abierta amparada por su origen. No mira si la delegación está activa: eso lo decide quien
    /// llama. Una siembra ya hecha (filas vivas y la operación amparada) no tiene historia, y la siembra sigue siendo
    /// idempotente.
    /// </summary>
    internal static async Task<bool> TieneHistoriaAsync(
        CaeManagerDbContext dbContext, DelegacionTenant delegacion, CancellationToken cancellationToken)
    {
        var (propietario, operador) = ParDe(delegacion);

        if (delegacion.Revocaciones > 0
            || await dbContext.DelegacionesTenant.AnyAsync(
                d => d.TenantClienteId == propietario && d.TenantConsultoraId == operador && d.Revocaciones > 0,
                cancellationToken))
            return true;

        var delegacionesDelPar = dbContext.DelegacionesTenant
            .Where(d => d.TenantClienteId == propietario && d.TenantConsultoraId == operador)
            .Select(d => d.Id);
        if (await dbContext.AsignacionesOperadorDelegadoConRevocadas.AnyAsync(
                a => a.RevocadaEnUtc != null && delegacionesDelPar.Contains(a.DelegacionTenantId), cancellationToken))
            return true;

        return await LeerOperacionesDelParAsync(dbContext, propietario, operador, async operaciones =>
            await operaciones.AnyAsync(cancellationToken)
            && !await operaciones
                .Where(o => o.Estado != EstadoAsignacion.Cerrada)
                .AmparadasPorSuOrigen(dbContext.DelegacionesTenant, DateTime.UtcNow)
                .AnyAsync(cancellationToken));
    }

    /// <summary>
    /// Si el par de <paramref name="delegacion"/> ya tiene alguna Asignación de Operación Outbound externa, abierta o
    /// cerrada: el último paso del alta de la siembra se hizo, y la siembra no completa nada (ronda 3 de Codex en la PR
    /// #2237, baja 7).
    /// </summary>
    internal static Task<bool> TieneOperacionExternaAsync(
        CaeManagerDbContext dbContext, DelegacionTenant delegacion, CancellationToken cancellationToken)
    {
        var (propietario, operador) = ParDe(delegacion);
        return LeerOperacionesDelParAsync(dbContext, propietario, operador, operaciones => operaciones.AnyAsync(cancellationToken));
    }

    /// <summary>
    /// Si la siembra puede crear la fila heredada de <paramref name="usuarioId"/> en <paramref name="delegacion"/>: la
    /// delegación está activa, el par no tiene historia y la persona no tiene ninguna fila, viva o revocada, en ninguna
    /// delegación del par.
    /// </summary>
    internal static async Task<bool> PuedeSembrarFilaHeredadaAsync(
        CaeManagerDbContext dbContext, DelegacionTenant delegacion, Guid usuarioId, CancellationToken cancellationToken)
    {
        if (!delegacion.Activa || await TieneHistoriaAsync(dbContext, delegacion, cancellationToken))
            return false;

        var delegacionesDelPar = dbContext.DelegacionesTenant
            .Where(d => d.TenantClienteId == delegacion.TenantClienteId && d.TenantConsultoraId == delegacion.TenantConsultoraId)
            .Select(d => d.Id);
        return !await dbContext.AsignacionesOperadorDelegadoConRevocadas.AnyAsync(
            a => a.UsuarioId == usuarioId && delegacionesDelPar.Contains(a.DelegacionTenantId), cancellationToken);
    }

    private static (Guid Propietario, Guid Operador) ParDe(DelegacionTenant delegacion) =>
        (delegacion.TenantClienteId, delegacion.TenantConsultoraId);

    /// <summary>
    /// Lee las Asignaciones de Operación Outbound externas del par en el ámbito del Tenant propietario, dueño de la
    /// operación. Los llamadores siembran en el ámbito del Operador CAE externo, y desde él la política
    /// <c>posicion_en_la_asignacion</c> no deja ver las operaciones del Tenant propietario: la siembra no tiene usuario,
    /// así que la sesión no lleva Tenant de origen, y la lectura habría salido vacía bajo <c>cae_app_runtime</c>.
    /// </summary>
    private static async Task<bool> LeerOperacionesDelParAsync(
        CaeManagerDbContext dbContext, Guid propietario, Guid operador, Func<IQueryable<AsignacionOperacion>, Task<bool>> lectura)
    {
        using (AmbitoTenantExplicito.Establecer(propietario))
        {
            return await lectura(dbContext.AsignacionesOperacion
                .Where(o => !o.EsRaiz
                            && o.PropietarioTenantId == propietario
                            && o.OperadorTenantId == operador
                            && o.Servicio == ServicioCae.Outbound));
        }
    }
}
