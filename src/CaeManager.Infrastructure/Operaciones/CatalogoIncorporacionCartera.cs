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

    /// <summary>
    /// El rol de la cartera que el relevo automático del principal emite al Coordinador CAE
    /// (ADR-011 § 2.7, enmienda 2026-10-08, puntos 2 y 3). Fijo, como <see cref="RolIncorporado"/>:
    /// ningún llamante elige el rol de una cartera emitida aquí, y la Operación nunca concede
    /// roles de Propiedad (Administrador, Dirección CAE).
    /// </summary>
    private const string RolDeRelevo = Roles.CoordinadorCae;

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
        var operaciones = await (
            from operacion in OperacionesExternasVivas()
            join tenant in dbContext.Tenants on operacion.PropietarioTenantId equals tenant.Id
            where operacion.OperadorTenantId == operadorTenantId
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

    /// <summary>
    /// Las Asignaciones de Operación externas vivas: Outbound, del Tenant entero, no raíz,
    /// vigentes hoy, de un Operador CAE distinto del Tenant propietario y con su delegación de
    /// Operador CAE externo activa. Una sola definición para las dos miradas: la del Operador
    /// CAE (qué Tenants puede asignar) y la del Tenant propietario (quién lo gestiona).
    /// </summary>
    private IQueryable<AsignacionOperacion> OperacionesExternasVivas()
    {
        var ahora = DateTime.UtcNow;

        return from operacion in dbContext.AsignacionesOperacion
               where !operacion.EsRaiz
                     && operacion.PropietarioTenantId != operacion.OperadorTenantId
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
                         && d.TenantConsultoraId == operacion.OperadorTenantId
                         && d.Proposito == PropositoDelegacion.OperadorExterno
                         && d.Activa
                         && (d.ExpiraEnUtc == null || d.ExpiraEnUtc > ahora))
               select operacion;
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
        var vinculoId = await VinculoVivoConElOperadorAsync(propietarioTenantId, operadorTenantId, ahora, cancellationToken);
        if (vinculoId is null)
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
        var filaHeredada = new AsignacionOperadorDelegado(vinculoId.Value, usuarioId, RolIncorporado);
        dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(filaHeredada);

        return new ResultadoIncorporacionCartera(cartera, filaHeredada.Id, null);
    }

    public async Task<ResultadoApoyoCartera> IncorporarApoyoAsync(
        PropuestaApoyoCartera propuesta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propuesta);

        var operacion = await ObtenerOperacionVigenteAsync(propuesta.AsignacionOperacionId, cancellationToken);
        if (operacion is null
            || operacion.EsRaiz
            || operacion.PropietarioTenantId != propuesta.PropietarioTenantId
            || operacion.OperadorTenantId != propuesta.OperadorTenantId)
            return ResultadoApoyoCartera.Anulada(MotivoAnulacionPropuestaApoyo.OperacionNoVigente);

        var ahora = DateTime.UtcNow;
        var vinculoId = await VinculoVivoConElOperadorAsync(
            propuesta.PropietarioTenantId, propuesta.OperadorTenantId, ahora, cancellationToken);
        if (vinculoId is null)
            return ResultadoApoyoCartera.Anulada(MotivoAnulacionPropuestaApoyo.OperacionNoVigente);

        // El apoyo lo propone el principal: si quien propuso ya no lleva la marca en una
        // cartera vigente de esta operación, la propuesta no se sostiene. Se lee seguida por
        // el contexto para poder escribirla abajo.
        var principal = await CarterasVivasDelOperador(propuesta.OperadorTenantId)
            .FirstOrDefaultAsync(c => c.AsignacionOperacionId == propuesta.AsignacionOperacionId && c.EsPrincipal, cancellationToken);
        if (principal is null || principal.UsuarioId != propuesta.ProponenteUsuarioId)
            return ResultadoApoyoCartera.Anulada(MotivoAnulacionPropuestaApoyo.ProponenteYaNoEsPrincipal);

        var yaEnCartera = await PropietariosEnCarteraAsync(
            [propuesta.PropietarioTenantId], propuesta.OperadorTenantId, propuesta.DestinatarioUsuarioId, cancellationToken);
        if (yaEnCartera.Count > 0)
            return ResultadoApoyoCartera.Anulada(MotivoAnulacionPropuestaApoyo.YaEnCartera);

        // Fecha de fin opcional del apoyo: la que puso quien propuso. Si ya pasó, aceptar
        // emitiría una cartera que nace caducada; la propuesta ya no ofrece nada.
        if (propuesta.VigenciaHastaPropuesta is { } hasta && hasta <= ahora)
            return ResultadoApoyoCartera.Anulada(MotivoAnulacionPropuestaApoyo.FechaDeFinPasada);

        // Candado optimista sobre la marca: se renueva la versión de la cartera del principal
        // sin cambiarle nada más. Una designación, un relevo o un cierre que le quiten la marca
        // a la vez escriben esta misma fila, y uno de los dos guardados pierde por la versión.
        dbContext.Entry(principal).Property(c => c.Version).IsModified = true;

        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();

        // Cartera de apoyo: rol fijo Gestor CAE, Tenant entero, con la fecha de fin de la
        // propuesta si la lleva. Aquí no se marca principal nunca, haya o no principal vivo:
        // esa regla es de IncorporarAsync, que decide un Coordinador CAE o superior; un apoyo
        // lo origina un igual.
        var cartera = AsignacionCartera.Externa(
            operacion, propuesta.DestinatarioUsuarioId, RolIncorporado, AmbitoAsignacion.Universal,
            ahora, propuesta.VigenciaHastaPropuesta, ahora, actorId);
        dbContext.AsignacionesCartera.Add(cartera);

        // Misma doble escritura de F1 que IncorporarAsync.
        var filaHeredada = new AsignacionOperadorDelegado(vinculoId.Value, propuesta.DestinatarioUsuarioId, RolIncorporado);
        dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(filaHeredada);

        return new ResultadoApoyoCartera(cartera, filaHeredada.Id, null);
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
    /// Las Asignaciones de Cartera vivas de Gestor CAE y de Coordinador CAE de este Operador
    /// CAE: vigentes hoy, del Tenant entero, sobre una operación externa no raíz suya. Una sola
    /// definición para leer quién es principal y quién de apoyo, para encender la marca y para
    /// listar y retirar la cartera de un Gestor CAE.
    /// </summary>
    private IQueryable<AsignacionCartera> CarterasVivasDelOperador(Guid operadorTenantId) =>
        from c in CarterasVivasDeTenantEntero()
        join o in dbContext.AsignacionesOperacion on c.AsignacionOperacionId equals o.Id
        where !o.EsRaiz
              && o.OperadorTenantId == operadorTenantId
              && o.PropietarioTenantId == c.PropietarioTenantId
              && c.PropietarioTenantId != operadorTenantId
        select c;

    /// <summary>
    /// Qué es una cartera viva de Gestor CAE o de Coordinador CAE sobre el Tenant entero, sin
    /// decir de qué Operador CAE ni de qué Tenant propietario: eso lo acota cada mirada.
    /// </summary>
    private IQueryable<AsignacionCartera> CarterasVivasDeTenantEntero()
    {
        var ahora = DateTime.UtcNow;

        return dbContext.AsignacionesCartera.Where(c =>
            (c.Rol == RolIncorporado || c.Rol == RolDeRelevo)
            && c.Estado == EstadoAsignacion.Vigente
            && (c.VigenciaHasta == null || ahora < c.VigenciaHasta)
            && c.AmbitoRelacionClienteId == null
            && c.AmbitoCentroId == null
            && c.AmbitoTrabajadorId == null
            && c.AmbitoProyectoId == null);
    }

    /// <summary>
    /// Las Asignaciones de Cartera universales vigentes del usuario, con rol Gestor CAE, sobre
    /// una operación externa no raíz de este Operador CAE. Una sola definición para listar lo
    /// que se puede retirar y para retirarlo.
    /// </summary>
    private IQueryable<AsignacionCartera> CarterasUniversalesDelGestor(Guid operadorTenantId, Guid usuarioId) =>
        CarterasVivasDelOperador(operadorTenantId).Where(c => c.UsuarioId == usuarioId && c.Rol == RolIncorporado);

    public async Task<IReadOnlyList<CarteraVivaDeOperacion>> ObtenerCarterasVivasAsync(
        Guid operadorTenantId, Guid? propietarioTenantId, CancellationToken cancellationToken = default)
    {
        var vivas = await (
            from c in CarterasVivasDelOperador(operadorTenantId)
            join t in dbContext.Tenants on c.PropietarioTenantId equals t.Id
            where propietarioTenantId == null || c.PropietarioTenantId == propietarioTenantId
            select new CarteraVivaDeOperacion(
                c.AsignacionOperacionId, c.PropietarioTenantId, t.Nombre, c.UsuarioId, c.Rol!, c.EsPrincipal, c.VigenciaHasta))
            .ToListAsync(cancellationToken);

        return vivas
            .OrderBy(c => c.NombreTenant)
            .ThenBy(c => c.AsignacionOperacionId)
            .ThenByDescending(c => c.EsPrincipal)
            .ToList();
    }

    public async Task<IReadOnlyList<OperacionExternaSobreTenant>> ObtenerOperacionesExternasSobreTenantAsync(
        Guid propietarioTenantId, CancellationToken cancellationToken = default)
    {
        var operaciones = await (
            from operacion in OperacionesExternasVivas()
            join operador in dbContext.Tenants on operacion.OperadorTenantId equals operador.Id
            where operacion.PropietarioTenantId == propietarioTenantId
            select new { operacion.Id, operacion.OperadorTenantId, NombreOperador = operador.Nombre })
            .ToListAsync(cancellationToken);
        if (operaciones.Count == 0) return [];

        var operacionIds = operaciones.Select(o => o.Id).ToList();
        var carteras = await CarterasVivasDeTenantEntero()
            .Where(c => operacionIds.Contains(c.AsignacionOperacionId) && c.PropietarioTenantId == propietarioTenantId)
            .Select(c => new CarteraVivaDeOperacion(
                c.AsignacionOperacionId, c.PropietarioTenantId, string.Empty, c.UsuarioId, c.Rol!, c.EsPrincipal, c.VigenciaHasta))
            .ToListAsync(cancellationToken);
        var porOperacion = carteras.ToLookup(c => c.AsignacionOperacionId);

        return operaciones
            .OrderBy(o => o.NombreOperador)
            .ThenBy(o => o.Id)
            .Select(o => new OperacionExternaSobreTenant(
                o.Id, o.OperadorTenantId, o.NombreOperador,
                porOperacion[o.Id].OrderByDescending(c => c.EsPrincipal).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<OperacionConPrincipal>> ObtenerOperacionesDondeEsPrincipalAsync(
        Guid operadorTenantId, Guid usuarioId, CancellationToken cancellationToken = default) =>
        await (
            from c in dbContext.AsignacionesCartera
            join o in dbContext.AsignacionesOperacion on c.AsignacionOperacionId equals o.Id
            where c.UsuarioId == usuarioId
                  && c.EsPrincipal
                  && c.Estado != EstadoAsignacion.Cerrada
                  && !o.EsRaiz
                  && o.OperadorTenantId == operadorTenantId
                  && c.PropietarioTenantId != operadorTenantId
            select new OperacionConPrincipal(c.PropietarioTenantId, c.AsignacionOperacionId))
            .ToListAsync(cancellationToken);

    public async Task<bool> ApagarPrincipalAsync(
        Guid operadorTenantId, Guid asignacionOperacionId, Guid usuarioEsperadoId,
        CancellationToken cancellationToken = default)
    {
        var principal = await dbContext.AsignacionesCartera
            .FirstOrDefaultAsync(c => c.AsignacionOperacionId == asignacionOperacionId
                                      && c.OperadorTenantId == operadorTenantId
                                      && c.EsPrincipal
                                      && c.Estado != EstadoAsignacion.Cerrada, cancellationToken);
        if (principal is null || principal.UsuarioId != usuarioEsperadoId)
            return false;

        principal.DejarDeSerPrincipal();
        return true;
    }

    public async Task<bool> EncenderPrincipalAsync(
        Guid operadorTenantId, Guid asignacionOperacionId, Guid usuarioId,
        CancellationToken cancellationToken = default)
    {
        var operacion = await OperacionExternaDelOperadorAsync(asignacionOperacionId, operadorTenantId, cancellationToken);
        if (operacion is null || await PrincipalDeOperacion.HayPrincipalVivoAsync(dbContext, operacion, cancellationToken))
            return false;

        var cartera = await CarterasVivasDelOperador(operadorTenantId)
            .FirstOrDefaultAsync(c => c.AsignacionOperacionId == asignacionOperacionId && c.UsuarioId == usuarioId, cancellationToken);
        if (cartera is null)
            return false;

        cartera.DesignarPrincipal();
        return true;
    }

    public async Task<ResultadoRelevoPrincipal> RelevarPrincipalAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid asignacionOperacionId, Guid coordinadorUsuarioId,
        CancellationToken cancellationToken = default)
    {
        var operacion = await OperacionExternaDelOperadorAsync(asignacionOperacionId, operadorTenantId, cancellationToken);
        if (operacion is null
            || operacion.PropietarioTenantId != propietarioTenantId
            || operacion.Estado == EstadoAsignacion.Cerrada
            || await PrincipalDeOperacion.HayPrincipalVivoAsync(dbContext, operacion, cancellationToken))
            return ResultadoRelevoPrincipal.SinRelevo;

        var ahora = DateTime.UtcNow;
        var suyas = await dbContext.AsignacionesCartera
            .Where(c => c.AsignacionOperacionId == asignacionOperacionId
                        && c.UsuarioId == coordinadorUsuarioId
                        && c.Estado != EstadoAsignacion.Cerrada)
            .ToListAsync(cancellationToken);

        var marcable = suyas.FirstOrDefault(c =>
            c.Estado == EstadoAsignacion.Vigente
            && (c.VigenciaHasta == null || ahora < c.VigenciaHasta)
            && c.Ambito.EsUniversal
            && (c.Rol == RolIncorporado || c.Rol == RolDeRelevo));
        if (marcable is not null)
        {
            marcable.DesignarPrincipal();
            return ResultadoRelevoPrincipal.CarteraExistenteMarcada;
        }

        // Ya tiene otra cartera no cerrada bajo esta operación (de Consulta, suspendida): el
        // relevo no la sustituye ni le pone otra al lado. No se ensancha en silencio el alcance
        // que otro decidió; la operación queda sin principal.
        if (suyas.Count > 0)
            return ResultadoRelevoPrincipal.SinRelevo;

        // Emitir exige lo mismo que IncorporarAsync: operación vigente hoy y delegación viva.
        if (await ObtenerOperacionVigenteAsync(asignacionOperacionId, cancellationToken) is null)
            return ResultadoRelevoPrincipal.SinRelevo;

        var vinculoId = await VinculoVivoConElOperadorAsync(propietarioTenantId, operadorTenantId, ahora, cancellationToken);
        if (vinculoId is null)
            return ResultadoRelevoPrincipal.SinRelevo;

        // La fila heredada es única por delegación y usuario y su rol no cambia: si el
        // Coordinador CAE ya tiene una de Coordinador CAE se reutiliza; si la tiene de otro rol,
        // alguien decidió ese acceso y el relevo no lo cambia.
        var filaHeredada = await dbContext.AsignacionesOperadorDelegado
            .FirstOrDefaultAsync(a => a.DelegacionTenantId == vinculoId.Value && a.UsuarioId == coordinadorUsuarioId, cancellationToken);
        if (filaHeredada is not null && filaHeredada.Rol != RolDeRelevo)
            return ResultadoRelevoPrincipal.SinRelevo;

        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var cartera = AsignacionCartera.Externa(
            operacion, coordinadorUsuarioId, RolDeRelevo, AmbitoAsignacion.Universal,
            ahora, vigenciaHasta: null, ahora, actorId);
        cartera.DesignarPrincipal();
        dbContext.AsignacionesCartera.Add(cartera);

        // Misma doble escritura de F1 que IncorporarAsync: sin la fila heredada el Coordinador
        // CAE tendría la cartera y no vería el Tenant en el selector.
        if (filaHeredada is null)
            dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(vinculoId.Value, coordinadorUsuarioId, RolDeRelevo));

        return ResultadoRelevoPrincipal.CarteraEmitida;
    }

    /// <summary>
    /// La delegación viva de Operador CAE externo de ese Tenant propietario hacia ese Operador CAE
    /// (la más antigua si hubiera varias), de la que cuelga la fila heredada de la doble escritura.
    /// </summary>
    private Task<Guid?> VinculoVivoConElOperadorAsync(
        Guid propietarioTenantId, Guid operadorTenantId, DateTime ahora, CancellationToken cancellationToken) =>
        dbContext.DelegacionesTenant
            .Where(d => d.TenantClienteId == propietarioTenantId
                        && d.TenantConsultoraId == operadorTenantId
                        && d.Proposito == PropositoDelegacion.OperadorExterno
                        && d.Activa
                        && (d.ExpiraEnUtc == null || d.ExpiraEnUtc > ahora))
            .OrderBy(d => d.CreadoEnUtc)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>La operación externa, no raíz, de este Operador CAE; <c>null</c> si el Id es de otra.</summary>
    private Task<AsignacionOperacion?> OperacionExternaDelOperadorAsync(
        Guid asignacionOperacionId, Guid operadorTenantId, CancellationToken cancellationToken) =>
        dbContext.AsignacionesOperacion.FirstOrDefaultAsync(
            o => o.Id == asignacionOperacionId
                 && !o.EsRaiz
                 && o.OperadorTenantId == operadorTenantId
                 && o.PropietarioTenantId != operadorTenantId, cancellationToken);

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

        await CerrarPorRetiradaAsync(carteras, propietarioTenantId, operadorTenantId, usuarioId, actorUsuarioId, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ApoyoVivoDeCartera>> ObtenerApoyosVivosAsync(
        Guid operadorTenantId, CancellationToken cancellationToken = default)
    {
        var apoyos = await (
            from p in dbContext.PropuestasApoyoCartera
            join c in CarterasVivasDelOperador(operadorTenantId) on p.AsignacionCarteraId equals (Guid?)c.Id
            join t in dbContext.Tenants on c.PropietarioTenantId equals t.Id
            where p.OperadorTenantId == operadorTenantId
                  && p.Estado == EstadoPropuestaApoyoCartera.Aceptada
                  && c.UsuarioId == p.DestinatarioUsuarioId
                  && c.Rol == RolIncorporado
                  && !c.EsPrincipal
            select new ApoyoVivoDeCartera(
                p.Id, c.AsignacionOperacionId, c.PropietarioTenantId, t.Nombre,
                p.DestinatarioUsuarioId, p.ProponenteUsuarioId, c.VigenciaHasta))
            .ToListAsync(cancellationToken);

        return apoyos.OrderBy(a => a.NombreTenant).ThenBy(a => a.PropuestaId).ToList();
    }

    public async Task<ResultadoRetiradaApoyo> RetirarCarteraDeApoyoAsync(
        PropuestaApoyoCartera propuesta, Guid actorUsuarioId, bool exigirProponentePrincipal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propuesta);
        if (propuesta.Estado != EstadoPropuestaApoyoCartera.Aceptada || propuesta.AsignacionCarteraId is not { } carteraId)
            throw new InvalidOperationException("Solo se retira el apoyo de una propuesta aceptada.");

        // Seguida por el contexto: su versión es la que detecta que alguien le puso la marca
        // de principal, o la cerró, entre esta lectura y el guardado.
        var cartera = await dbContext.AsignacionesCartera.FirstOrDefaultAsync(
            c => c.Id == carteraId
                 && c.OperadorTenantId == propuesta.OperadorTenantId
                 && c.PropietarioTenantId == propuesta.PropietarioTenantId
                 && c.UsuarioId == propuesta.DestinatarioUsuarioId, cancellationToken);

        // Cerrada (caducó, cayó con la operación, otro la retiró): ya no hay apoyo que retirar.
        // Solo queda poner la propuesta al día.
        if (cartera is null || cartera.Estado == EstadoAsignacion.Cerrada)
        {
            propuesta.Terminar();
            return ResultadoRetiradaApoyo.YaEstabaCerrada;
        }

        if (cartera.EsPrincipal)
            return ResultadoRetiradaApoyo.YaNoEsDeApoyo;

        if (exigirProponentePrincipal)
        {
            var principal = await CarterasVivasDelOperador(propuesta.OperadorTenantId)
                .FirstOrDefaultAsync(c => c.AsignacionOperacionId == propuesta.AsignacionOperacionId && c.EsPrincipal, cancellationToken);
            if (principal is null || principal.UsuarioId != propuesta.ProponenteUsuarioId)
                return ResultadoRetiradaApoyo.ProponenteYaNoEsPrincipal;

            // Mismo candado optimista que IncorporarApoyoAsync: si la marca cambia de manos a
            // la vez, uno de los dos guardados pierde por la versión de esta fila.
            dbContext.Entry(principal).Property(c => c.Version).IsModified = true;
        }

        await CerrarPorRetiradaAsync(
            [cartera], propuesta.PropietarioTenantId, propuesta.OperadorTenantId, propuesta.DestinatarioUsuarioId,
            actorUsuarioId, cancellationToken);
        return ResultadoRetiradaApoyo.Retirada;
    }

    /// <summary>
    /// El camino único de la retirada de una cartera del Tenant entero: cierra las carteras
    /// dadas, pone al día la solicitud de incorporación o la propuesta de apoyo que las creó y
    /// borra la fila heredada de Operador Delegado si ya no la sostiene ninguna otra cartera.
    /// </summary>
    private async Task CerrarPorRetiradaAsync(
        IReadOnlyCollection<AsignacionCartera> carteras, Guid propietarioTenantId, Guid operadorTenantId,
        Guid usuarioId, Guid actorUsuarioId, CancellationToken cancellationToken)
    {
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

        // Y la propuesta de apoyo que la emitió, igual: el apoyo terminó.
        var propuestas = await dbContext.PropuestasApoyoCartera
            .Where(p => p.OperadorTenantId == operadorTenantId
                        && p.DestinatarioUsuarioId == usuarioId
                        && p.Estado == EstadoPropuestaApoyoCartera.Aceptada
                        && p.AsignacionCarteraId != null && cerradas.Contains(p.AsignacionCarteraId.Value))
            .ToListAsync(cancellationToken);
        foreach (var propuesta in propuestas)
            propuesta.Terminar();

        await RetirarFilaHeredadaSiSobraAsync(
            dbContext, propietarioTenantId, operadorTenantId, usuarioId, cerradas, ahora, cancellationToken);
    }

    /// <summary>
    /// Borra la fila heredada de Operador Delegado del usuario sobre ese Tenant propietario
    /// <b>solo si no le queda otra cartera vigente</b> en él (de otro rol, o bajo otra
    /// operación): esa otra sigue necesitando que el Tenant le aparezca. La fila heredada
    /// autoriza el Tenant por sí sola (vía heredada de <c>TenantsBeneficiariosAutorizados</c>),
    /// así que <b>todo cierre de una cartera externa del Tenant entero tiene que pasar por
    /// aquí</b>: sin ello la cartera quedaría cerrada y el acceso, intacto. Lo usan la retirada
    /// y el cierre por caducidad (<see cref="ExpiracionAsignacionesHostedService"/>). Sin guardar.
    /// </summary>
    internal static async Task RetirarFilaHeredadaSiSobraAsync(
        CaeManagerDbContext dbContext, Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId,
        IReadOnlyCollection<Guid> carterasCerradas, DateTime ahora, CancellationToken cancellationToken)
    {
        var leQuedaOtra = await dbContext.AsignacionesCartera.AnyAsync(c =>
            c.UsuarioId == usuarioId
            && c.PropietarioTenantId == propietarioTenantId
            && !carterasCerradas.Contains(c.Id)
            && c.Estado == EstadoAsignacion.Vigente
            && (c.VigenciaHasta == null || ahora < c.VigenciaHasta), cancellationToken);
        if (leQuedaOtra)
            return;

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

    /// <summary>Las restricciones únicas cuya violación es la carrera esperada, no un defecto.</summary>
    private static readonly HashSet<string> RestriccionesDeCarrera =
    [
        SolicitudIncorporacionCarteraConfiguration.IndicePendienteUnica,
        // Dos propuestas de apoyo a la vez al mismo destinatario sobre la misma operación.
        PropuestaApoyoCarteraConfiguration.IndicePendienteUnica,
        "IX_AsignacionesCartera_UsuarioUniversalVigente",
        // Dos emisiones simultáneas a Gestores CAE distintos sobre una operación sin principal:
        // las dos nacen marcadas y el índice deja pasar una. La que pierde se reintenta y nace
        // sin marca. También lo pierden una designación de principal o un relevo que llegan
        // cuando otro ya puso la marca: fallan enteros, dentro de su transacción.
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
