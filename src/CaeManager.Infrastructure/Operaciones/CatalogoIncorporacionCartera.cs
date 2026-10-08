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
        var asignables = await ObtenerAsignablesAsync(operadorTenantId, cancellationToken);
        if (asignables.Count == 0) return [];

        var propietarios = asignables.Select(o => o.PropietarioTenantId).ToList();
        var yaEnCartera = await PropietariosEnCarteraAsync(propietarios, operadorTenantId, usuarioId, cancellationToken);

        return asignables.Where(o => !yaEnCartera.Contains(o.PropietarioTenantId)).ToList();
    }

    public async Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerAsignablesAsync(
        Guid operadorTenantId, CancellationToken cancellationToken = default)
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

        // Una sola entrada por Tenant propietario: si hubiera dos operaciones
        // universales vigentes (no debería, el índice de responsabilidad lo
        // impide), se pide sobre la más antigua de forma estable.
        return operaciones
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

    public Task<ResultadoIncorporacionCartera> IncorporarAsync(
        SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);

        return IncorporarAsync(
            solicitud.PropietarioTenantId, solicitud.OperadorTenantId, solicitud.AsignacionOperacionId,
            solicitud.SolicitanteUsuarioId, cancellationToken);
    }

    public async Task<ResultadoIncorporacionCartera> IncorporarAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid asignacionOperacionId, Guid usuarioId,
        CancellationToken cancellationToken = default)
    {
        // La operación tiene que ser la externa de este Operador CAE sobre este
        // Tenant propietario, no solo existir y estar vigente: el Id viene de
        // quien llama, y colgar la cartera de otra operación daría el Tenant
        // propietario por una Asignación de Operación que no lo cubre.
        var operacion = await ObtenerOperacionVigenteAsync(asignacionOperacionId, cancellationToken);
        if (operacion is null
            || operacion.EsRaiz
            || operacion.PropietarioTenantId != propietarioTenantId
            || operacion.OperadorTenantId != operadorTenantId)
            return ResultadoIncorporacionCartera.Anulada(MotivoAnulacionSolicitudCartera.OperacionNoVigente);

        var ahora = DateTime.UtcNow;
        var vinculo = await dbContext.DelegacionesTenant
            .Where(d => d.TenantClienteId == propietarioTenantId
                        && d.TenantConsultoraId == operadorTenantId
                        && d.Proposito == PropositoDelegacion.OperadorExterno
                        && d.Activa
                        && (d.ExpiraEnUtc == null || d.ExpiraEnUtc > ahora))
            .OrderBy(d => d.CreadoEnUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (vinculo is null)
            return ResultadoIncorporacionCartera.Anulada(MotivoAnulacionSolicitudCartera.OperacionNoVigente);

        var yaEnCartera = await PropietariosEnCarteraAsync(
            [propietarioTenantId], operadorTenantId, usuarioId, cancellationToken);
        if (yaEnCartera.Count > 0)
            return ResultadoIncorporacionCartera.Anulada(MotivoAnulacionSolicitudCartera.YaEnCartera);

        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();

        var cartera = AsignacionCartera.Externa(
            operacion, usuarioId, RolIncorporado, AmbitoAsignacion.Universal,
            ahora, vigenciaHasta: null, ahora, actorId);

        // Regla de emisión de la marca de principal (ADR-011 § 2.7, enmienda 2026-10-08): la
        // cartera de Gestor CAE que entra en una operación sin principal vivo nace principal;
        // si ya lo hay, nace sin marca (cartera de apoyo). Vale para las tres vías que llegan
        // aquí —asignación directa, alta de Gestor CAE y solicitud aceptada—, todas decididas
        // por un Coordinador CAE o superior.
        if (!await PrincipalDeOperacion.HayPrincipalVivoAsync(dbContext, operacion, cancellationToken))
            cartera.DesignarPrincipal();

        dbContext.AsignacionesCartera.Add(cartera);

        // Doble escritura de F1: el selector de Tenant, el fan-out de Mi
        // trabajo y el rol dentro de un ámbito explícito enumeran todavía los
        // Tenants por la fila heredada, no por las carteras. Sin ella la
        // cartera existiría y el Gestor CAE no vería el Tenant en ningún sitio.
        var filaHeredada = new AsignacionOperadorDelegado(vinculo.Id, usuarioId, RolIncorporado);
        dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(filaHeredada);

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
                dbContext.AsignacionesOperadorDelegadoConRevocadas.Remove(fila);
        }
    }

    /// <summary>
    /// Las Asignaciones de Cartera universales vigentes del usuario, con rol Gestor CAE, sobre
    /// una operación externa no raíz de este Operador CAE. Una sola definición para listar lo
    /// que se puede retirar y para retirarlo.
    /// </summary>
    private IQueryable<AsignacionCartera> CarterasUniversalesDelGestor(Guid operadorTenantId, Guid usuarioId)
    {
        var ahora = DateTime.UtcNow;

        return from c in dbContext.AsignacionesCartera
               join o in dbContext.AsignacionesOperacion on c.AsignacionOperacionId equals o.Id
               where c.UsuarioId == usuarioId
                     && c.Rol == RolIncorporado
                     && c.Estado == EstadoAsignacion.Vigente
                     && (c.VigenciaHasta == null || ahora < c.VigenciaHasta)
                     && c.AmbitoRelacionClienteId == null
                     && c.AmbitoCentroId == null
                     && c.AmbitoTrabajadorId == null
                     && c.AmbitoProyectoId == null
                     && !o.EsRaiz
                     && o.OperadorTenantId == operadorTenantId
                     && o.PropietarioTenantId == c.PropietarioTenantId
                     && c.PropietarioTenantId != operadorTenantId
               select c;
    }

    public async Task<IReadOnlyList<TenantEnCarteraDeGestor>> ObtenerCarteraUniversalAsync(
        Guid operadorTenantId, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var enCartera = await (
            from c in CarterasUniversalesDelGestor(operadorTenantId, usuarioId)
            join t in dbContext.Tenants on c.PropietarioTenantId equals t.Id
            select new TenantEnCarteraDeGestor(c.PropietarioTenantId, t.Nombre))
            .ToListAsync(cancellationToken);

        return enCartera
            .GroupBy(t => t.PropietarioTenantId)
            .Select(g => g.First())
            .OrderBy(t => t.Nombre)
            .ToList();
    }

    public async Task<bool> RetirarCarteraUniversalAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, Guid actorUsuarioId,
        CancellationToken cancellationToken = default)
    {
        var carteras = await CarterasUniversalesDelGestor(operadorTenantId, usuarioId)
            .Where(c => c.PropietarioTenantId == propietarioTenantId)
            .ToListAsync(cancellationToken);
        if (carteras.Count == 0) return false;

        var ahora = DateTime.UtcNow;
        foreach (var cartera in carteras)
            cartera.Cerrar(MotivoCierreAsignacion.RetiradaPorElOperador, ahora);
        var cerradas = carteras.Select(c => c.Id).ToList();

        // La solicitud que creó esa cartera deja de estar «aceptada» con una cartera cerrada.
        var solicitudes = await dbContext.SolicitudesIncorporacionCartera
            .Where(s => s.OperadorTenantId == operadorTenantId
                        && s.SolicitanteUsuarioId == usuarioId
                        && s.Estado == EstadoSolicitudIncorporacionCartera.Aceptada
                        && s.AsignacionCarteraId != null && cerradas.Contains(s.AsignacionCarteraId.Value))
            .ToListAsync(cancellationToken);
        foreach (var solicitud in solicitudes)
            solicitud.Revocar(actorUsuarioId, ahora);

        // La fila heredada solo sobra si no queda otra cartera vigente del usuario en ese Tenant:
        // otra cartera vigente suya en ese Tenant (de otro rol, o bajo otra operación) sigue necesitando que el
        // Tenant le aparezca.
        var vigenteAhora = DateTime.UtcNow;
        var leQuedaOtra = await dbContext.AsignacionesCartera.AnyAsync(c =>
            c.UsuarioId == usuarioId
            && c.PropietarioTenantId == propietarioTenantId
            && !cerradas.Contains(c.Id)
            && c.Estado == EstadoAsignacion.Vigente
            && (c.VigenciaHasta == null || vigenteAhora < c.VigenciaHasta), cancellationToken);

        if (!leQuedaOtra)
        {
            var filas = await (
                from fila in dbContext.AsignacionesOperadorDelegado
                join vinculo in dbContext.DelegacionesTenant on fila.DelegacionTenantId equals vinculo.Id
                where fila.UsuarioId == usuarioId
                      && vinculo.TenantClienteId == propietarioTenantId
                      && vinculo.TenantConsultoraId == operadorTenantId
                      && vinculo.Proposito == PropositoDelegacion.OperadorExterno
                select fila)
                .ToListAsync(cancellationToken);
            dbContext.AsignacionesOperadorDelegadoConRevocadas.RemoveRange(filas);
        }

        return true;
    }

    /// <summary>Las restricciones únicas cuya violación es la carrera esperada, no un defecto.</summary>
    private static readonly HashSet<string> RestriccionesDeCarrera =
    [
        SolicitudIncorporacionCarteraConfiguration.IndicePendienteUnica,
        "IX_AsignacionesCartera_UsuarioUniversalVigente",
        // Dos emisiones simultáneas a Gestores CAE distintos sobre una operación sin principal:
        // las dos nacen marcadas y el índice deja pasar una. La que pierde se reintenta y nace
        // sin marca.
        AsignacionCarteraConfiguration.IndicePrincipalPorOperacion,
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

        // La cartera y la Asignación de Operación que la sostiene, las dos vigentes (mismo
        // criterio que ObtenerOperacionVigenteAsync): una operación caducada o suspendida deja
        // la cartera sin efecto aunque la propia cartera siga marcada como vigente.
        var ahora = DateTime.UtcNow;
        var vigentes = await (
                from c in dbContext.AsignacionesCartera
                join o in dbContext.AsignacionesOperacion on c.AsignacionOperacionId equals o.Id
                where asignacionCarteraIds.Contains(c.Id)
                      && c.Estado == EstadoAsignacion.Vigente
                      && (c.VigenciaHasta == null || ahora < c.VigenciaHasta)
                      && o.Estado == EstadoAsignacion.Vigente
                      && o.VigenciaDesde <= ahora
                      && (o.VigenciaHasta == null || ahora < o.VigenciaHasta)
                select c.Id)
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
