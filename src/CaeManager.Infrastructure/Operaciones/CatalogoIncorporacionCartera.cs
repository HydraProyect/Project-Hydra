using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Operaciones;

/// <inheritdoc cref="ICatalogoIncorporacionCartera" />
public class CatalogoIncorporacionCartera(
    CaeManagerDbContext dbContext,
    ICurrentUserService currentUserService)
    : ICatalogoIncorporacionCartera
{
    /// <summary>
    /// El rol con el que entra quien se incorpora. Fijo: la solicitud es de un
    /// Gestor CAE y la decisión del propietario (contrato § 13) es que se sume
    /// como Gestor CAE adicional, no con el rol que tenga en su propio Tenant.
    /// </summary>
    private const string RolIncorporado = Roles.GestorCae;

    public async Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerCandidatosAsync(
        Guid operadorTenantId, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var ahora = DateTime.UtcNow;

        var operaciones = await (
            from operacion in dbContext.AsignacionesOperacion
            join tenant in dbContext.Tenants on operacion.PropietarioTenantId equals tenant.Id
            where !operacion.EsRaiz
                  && operacion.OperadorTenantId == operadorTenantId
                  && operacion.PropietarioTenantId != operadorTenantId
                  && operacion.Servicio == ServicioCae.Outbound
                  && operacion.AmbitoRelacionClienteId == null
                  && operacion.AmbitoCentroId == null
                  && operacion.AmbitoTrabajadorId == null
                  && operacion.AmbitoProyectoId == null
                  && operacion.Estado == EstadoAsignacion.Vigente
                  && operacion.VigenciaDesde <= ahora
                  && (operacion.VigenciaHasta == null || ahora < operacion.VigenciaHasta)
                  && dbContext.DelegacionesTenant.Any(d =>
                      d.TenantClienteId == operacion.PropietarioTenantId
                      && d.TenantConsultoraId == operadorTenantId
                      && d.Proposito == PropositoDelegacion.OperadorExterno
                      && d.Activa
                      && (d.ExpiraEnUtc == null || d.ExpiraEnUtc > ahora))
            select new TenantCandidatoIncorporacion(operacion.PropietarioTenantId, tenant.Nombre, operacion.Id))
            .ToListAsync(cancellationToken);

        if (operaciones.Count == 0) return [];

        var propietarios = operaciones.Select(o => o.PropietarioTenantId).ToList();
        var yaEnCartera = await PropietariosEnCarteraAsync(propietarios, operadorTenantId, usuarioId, cancellationToken);

        // Una sola entrada por Tenant propietario: si hubiera dos operaciones
        // universales vigentes (no debería, el índice de responsabilidad lo
        // impide), se pide sobre la más antigua de forma estable.
        return operaciones
            .Where(o => !yaEnCartera.Contains(o.PropietarioTenantId))
            .GroupBy(o => o.PropietarioTenantId)
            .Select(g => g.OrderBy(o => o.AsignacionOperacionId).First())
            .OrderBy(o => o.Nombre)
            .ToList();
    }

    public Task<AsignacionOperacion?> ObtenerOperacionVigenteAsync(
        Guid asignacionOperacionId, CancellationToken cancellationToken = default)
    {
        var ahora = DateTime.UtcNow;

        return dbContext.AsignacionesOperacion
            .FirstOrDefaultAsync(o => o.Id == asignacionOperacionId
                                      && o.Estado == EstadoAsignacion.Vigente
                                      && o.VigenciaDesde <= ahora
                                      && (o.VigenciaHasta == null || ahora < o.VigenciaHasta), cancellationToken);
    }

    public async Task<ResultadoIncorporacionCartera> IncorporarAsync(
        SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);

        var operacion = await ObtenerOperacionVigenteAsync(solicitud.AsignacionOperacionId, cancellationToken);
        if (operacion is null)
            return ResultadoIncorporacionCartera.Anulada(MotivoAnulacionSolicitudCartera.OperacionNoVigente);

        var ahora = DateTime.UtcNow;
        var vinculo = await dbContext.DelegacionesTenant
            .Where(d => d.TenantClienteId == solicitud.PropietarioTenantId
                        && d.TenantConsultoraId == solicitud.OperadorTenantId
                        && d.Proposito == PropositoDelegacion.OperadorExterno
                        && d.Activa
                        && (d.ExpiraEnUtc == null || d.ExpiraEnUtc > ahora))
            .OrderBy(d => d.CreadoEnUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (vinculo is null)
            return ResultadoIncorporacionCartera.Anulada(MotivoAnulacionSolicitudCartera.OperacionNoVigente);

        var yaEnCartera = await PropietariosEnCarteraAsync(
            [solicitud.PropietarioTenantId], solicitud.OperadorTenantId, solicitud.SolicitanteUsuarioId, cancellationToken);
        if (yaEnCartera.Count > 0)
            return ResultadoIncorporacionCartera.Anulada(MotivoAnulacionSolicitudCartera.YaEnCartera);

        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();

        var cartera = AsignacionCartera.Externa(
            operacion, solicitud.SolicitanteUsuarioId, RolIncorporado, AmbitoAsignacion.Universal,
            ahora, vigenciaHasta: null, ahora, actorId);
        dbContext.AsignacionesCartera.Add(cartera);

        // Doble escritura de F1: el selector de Tenant, el fan-out de Mi
        // trabajo y el rol dentro de un ámbito explícito enumeran todavía los
        // Tenants por la fila heredada, no por las carteras. Sin ella la
        // cartera existiría y el Gestor CAE no vería el Tenant en ningún sitio.
        var filaHeredada = new AsignacionOperadorDelegado(vinculo.Id, solicitud.SolicitanteUsuarioId, RolIncorporado);
        dbContext.AsignacionesOperadorDelegado.Add(filaHeredada);

        return new ResultadoIncorporacionCartera(cartera, filaHeredada.Id, null);
    }

    public async Task RetirarAsync(SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        var ahora = DateTime.UtcNow;

        if (solicitud.AsignacionCarteraId is { } carteraId)
        {
            var cartera = await dbContext.AsignacionesCartera
                .FirstOrDefaultAsync(c => c.Id == carteraId && c.Estado == EstadoAsignacion.Vigente, cancellationToken);
            cartera?.Cerrar(MotivoCierreAsignacion.RetiradaPorElOperador, ahora);
        }

        if (solicitud.AsignacionOperadorDelegadoId is { } filaId)
        {
            var fila = await dbContext.AsignacionesOperadorDelegado
                .FirstOrDefaultAsync(a => a.Id == filaId && a.UsuarioId == solicitud.SolicitanteUsuarioId, cancellationToken);
            if (fila is not null)
                dbContext.AsignacionesOperadorDelegado.Remove(fila);
        }
    }

    /// <summary>Las restricciones únicas cuya violación es la carrera esperada, no un defecto.</summary>
    private static readonly HashSet<string> RestriccionesDeCarrera =
    [
        SolicitudIncorporacionCarteraConfiguration.IndicePendienteUnica,
        "IX_AsignacionesCartera_UsuarioUniversalVigente",
        "IX_AsignacionesOperadorDelegado_DelegacionTenantId_UsuarioId",
    ];

    public async Task<bool> GuardarDetectandoCarreraAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
        // Mismo patrón que OperacionImportacionRepository: se comprueba el
        // nombre de la restricción, no cualquier 23505.
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" } pg
                                           && pg.ConstraintName is { } restriccion
                                           && RestriccionesDeCarrera.Contains(restriccion))
        {
            // Un SaveChanges fallido no revierte el estado Added en memoria: sin
            // el Clear, lo que perdió la carrera se colaría en el siguiente
            // SaveChanges del mismo contexto.
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    public void DescartarPendientes() => dbContext.ChangeTracker.Clear();

    public async Task<IReadOnlySet<Guid>> FiltrarCarterasVigentesAsync(
        IReadOnlyCollection<Guid> asignacionCarteraIds, CancellationToken cancellationToken = default)
    {
        if (asignacionCarteraIds.Count == 0) return new HashSet<Guid>();

        var ahora = DateTime.UtcNow;
        var vigentes = await dbContext.AsignacionesCartera
            .Where(c => asignacionCarteraIds.Contains(c.Id)
                        && c.Estado == EstadoAsignacion.Vigente
                        && (c.VigenciaHasta == null || ahora < c.VigenciaHasta))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        return vigentes.ToHashSet();
    }

    /// <summary>
    /// De <paramref name="propietarios"/>, los que el usuario ya tiene en su
    /// cartera — ver «En su cartera» en <see cref="ICatalogoIncorporacionCartera"/>.
    /// </summary>
    private async Task<HashSet<Guid>> PropietariosEnCarteraAsync(
        IReadOnlyCollection<Guid> propietarios, Guid operadorTenantId, Guid usuarioId, CancellationToken cancellationToken)
    {
        var ahora = DateTime.UtcNow;

        var porCartera = await dbContext.AsignacionesCartera
            .Where(c => c.UsuarioId == usuarioId
                        && propietarios.Contains(c.PropietarioTenantId)
                        && c.Estado == EstadoAsignacion.Vigente
                        && (c.VigenciaHasta == null || ahora < c.VigenciaHasta))
            .Select(c => c.PropietarioTenantId)
            .ToListAsync(cancellationToken);

        var porFilaHeredada = await (
            from asignacion in dbContext.AsignacionesOperadorDelegado
            join vinculo in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals vinculo.Id
            where asignacion.UsuarioId == usuarioId
                  && propietarios.Contains(vinculo.TenantClienteId)
                  && vinculo.TenantConsultoraId == operadorTenantId
                  && vinculo.Activa
                  && (vinculo.ExpiraEnUtc == null || vinculo.ExpiraEnUtc > ahora)
            select vinculo.TenantClienteId)
            .ToListAsync(cancellationToken);

        return porCartera.Concat(porFilaHeredada).ToHashSet();
    }
}
