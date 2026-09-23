using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Autorizacion;

/// <summary>
/// Implementación real de IAlcanceDatosService — vive en Infrastructure
/// porque necesita leer ApplicationUser (CoordinadorUsuarioId/ClienteId),
/// que Application no puede referenciar (ver Roles.cs).
///
/// <para>
/// Resuelve el alcance del usuario que MIRA, y es el único punto que lo hace:
/// toda restricción de datos por cartera pasa por aquí. No confundir con
/// <c>DirectorioUsuariosTenant.ObtenerCarterasVigentesAsync</c>, que responde
/// la pregunta simétrica —qué alcanzan OTROS— y es puramente informativa: pinta
/// una columna en /usuarios y no restringe nada. Si algún día hay que
/// restringir datos según el alcance de un tercero, se hace desde aquí, no
/// desde allí.
/// </para> Cachea el resultado
/// de cada método en la propia instancia (scoped por request/circuito) para
/// no repetir la misma resolución de cartera varias veces en la misma
/// petición cuando varios filtros de una Query la necesitan. En Blazor Server
/// esa instancia dura lo que el circuito: por eso la memoización caduca
/// (<see cref="CaducidadAlcanceOptions"/>, cota de lectura de una revocación
/// hecha desde otro circuito) y se descarta antes y después de cada Command.
///
/// La memoización cubre los seis alcances, no solo el de Cliente. Antes solo
/// estaba el de Cliente y el resto se recalculaba cada vez, con el agravante
/// de que se llaman en cascada: Trabajador pide Centro, Vehículo pide Empresa
/// y Subcontrata, y Subcontrata vuelve a pedir Empresa. Una sola carga del
/// listado de Documentos —que pide cuatro alcances— repetía la consulta de
/// Empresas tres veces.
///
/// <para>
/// El caché está indexado por <see cref="ITenantActual.TenantId"/>, no es un
/// único valor por instancia. El fan-out multi-tenant de
/// <c>ObtenerKpisGlobalesQuery</c>/<c>ObtenerDashboardEjecutivoQuery</c>
/// reutiliza esta MISMA instancia (scoped) para varios tenants dentro de la
/// misma petición, cambiando <c>AmbitoTenantExplicito</c> en cada vuelta del
/// bucle: un único valor por instancia habría memoizado el acceso total (y la
/// cartera) del PRIMER tenant visitado y lo habría servido, sin volver a
/// resolverlo, a cada tenant siguiente — un Administrador de la consultora
/// habría calculado acceso total también para sus Clientes Delegantes
/// (hallazgo Codex 2026-09-11). Indexar por tenant hace que cada clave del
/// diccionario se resuelva una sola vez —conservando la memoización dentro de
/// un mismo tenant— sin que ninguna se sirva de otra.
/// </para>
/// </summary>
public class AlcanceDatosService(
    CaeManagerDbContext dbContext,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IVistaDemoActual? vistaDemo = null,
    TimeProvider? reloj = null,
    Microsoft.Extensions.Options.IOptions<CaducidadAlcanceOptions>? caducidad = null)
    : IAlcanceDatosService, IInvalidadorAlcance
{
    // Dictionary<TKey,TValue> exige TKey : notnull, y tenantActual.TenantId es
    // Guid? (null cuando no hay tenant resuelto) — Guid.Empty es la clave
    // centinela para ese caso: ningún Tenant real usa ese Id (se generan con
    // Guid.NewGuid()), así que no puede colisionar con uno de verdad. Es una
    // clave más, no un caso especial, y TryGetValue ya distingue de forma
    // nativa "no está la clave" de "está con valor null", así que no hace
    // falta un flag "resuelto" aparte por alcance como antes de indexar por
    // tenant.
    private readonly Dictionary<Guid, bool> _accesoTotal = new();
    private readonly Dictionary<Guid, AlcanceCartera> _alcanceCartera = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _clienteIds = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _centroIds = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _empresaIds = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _subcontrataIds = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _trabajadorIds = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _vehiculoIds = new();

    // Marca de tiempo MONOTÓNICA (TimeProvider.GetTimestamp) del inicio de la generación memoizada
    // de cada Tenant: un ajuste del reloj de pared no puede alargar la cota. Ver ClaveTenantVigente.
    private readonly Dictionary<Guid, long> _inicioGeneracion = new();

    // Cambia cada vez que se descarta una generación (caducidad o Invalidar). Un alcance cuya
    // resolución empezó en una generación anterior se devuelve, pero no se memoiza: ver Memoizar.
    private long _generacion;

    private readonly TimeProvider _reloj = reloj ?? TimeProvider.System;
    // El techo se aplica aquí, no solo al leer la configuración: una sustitución de las opciones
    // (otro registro, un test) tampoco puede alargar la cota más allá de 60 s.
    private readonly TimeSpan _caducidad = CaducidadAlcanceOptions.Acotar(
        caducidad?.Value.Caducidad ?? CaducidadAlcanceOptions.CaducidadPorDefecto);

    private static Guid ClaveTenant(Guid? tenantId) => tenantId ?? Guid.Empty;

    /// <summary>
    /// Clave del Tenant actual, tras descartar su memoización si ya caducó. Es la cota de cuánto
    /// tarda un circuito vivo en dejar de LEER con un alcance revocado desde FUERA de él —un
    /// Coordinador CAE que cierra la Asignación de Cartera de un Gestor CAE, o una Asignación que
    /// llega a su <c>VigenciaHasta</c> sin que nadie escriba nada—: ninguna de las dos pasa por un
    /// Command de ese circuito. Sin caducidad, en Blazor Server la visión se conservaba mientras
    /// viviera el circuito (medido sobre PostgreSQL real, 2026-09-20 y 2026-09-23).
    ///
    /// <para>
    /// La caducidad es por generación y por Tenant, no por diccionario: al caducar se descartan a
    /// la vez todos los alcances del Tenant, así que cada valor memoizado se calculó después de
    /// que empezara su generación, y una revocación deja de servirse, como mucho,
    /// <see cref="CaducidadAlcanceOptions.Caducidad"/> después de producirse (60 s por defecto,
    /// decisión del propietario 2026-09-23). Las escrituras no esperan a esa cota:
    /// <see cref="InvalidacionAlcanceBehavior{TRequest,TResponse}"/> invalida ANTES de cada Command.
    /// </para>
    /// </summary>
    private Guid ClaveTenantVigente()
    {
        var tenant = ClaveTenant(tenantActual.TenantId);
        if (_inicioGeneracion.TryGetValue(tenant, out var inicio) && _reloj.GetElapsedTime(inicio) < _caducidad)
            return tenant;

        if (_inicioGeneracion.ContainsKey(tenant)) _generacion++;
        _accesoTotal.Remove(tenant);
        _alcanceCartera.Remove(tenant);
        _clienteIds.Remove(tenant);
        _centroIds.Remove(tenant);
        _empresaIds.Remove(tenant);
        _subcontrataIds.Remove(tenant);
        _trabajadorIds.Remove(tenant);
        _vehiculoIds.Remove(tenant);
        _inicioGeneracion[tenant] = _reloj.GetTimestamp();
        return tenant;
    }

    /// <summary>
    /// Memoiza solo si ninguna generación se descartó mientras se resolvía. Los alcances se piden
    /// en cascada (Trabajador pide Centro, Vehículo pide Empresa y Subcontrata): si la caducidad
    /// salta a mitad, el resultado puede mezclar listas de dos generaciones. Se devuelve igual
    /// —ninguna parte es más vieja que la cota—, pero no se guarda en la generación nueva, que
    /// solo contiene lo resuelto entero dentro de ella.
    /// </summary>
    private void Memoizar<T>(Dictionary<Guid, T> memo, Guid tenant, long generacionAlEmpezar, T valor)
    {
        if (generacionAlEmpezar == _generacion) memo[tenant] = valor;
    }

    /// <summary>
    /// Descarta todos los diccionarios (todos los Tenants de esta instancia: el fan-out reutiliza la
    /// misma). Lo invoca <see cref="InvalidacionAlcanceBehavior{TRequest,TResponse}"/> antes y
    /// después de cada Command. La memoización sigue siendo por instancia y por Tenant; esto evita
    /// que un Command se autorice, o que la lectura siguiente se sirva, con una visión anterior. Lo
    /// que se revoca desde otro circuito lo acota la caducidad (<see cref="ClaveTenantVigente"/>).
    /// </summary>
    public void Invalidar()
    {
        _generacion++;
        _inicioGeneracion.Clear();
        _accesoTotal.Clear();
        _alcanceCartera.Clear();
        _clienteIds.Clear();
        _centroIds.Clear();
        _empresaIds.Clear();
        _subcontrataIds.Clear();
        _trabajadorIds.Clear();
        _vehiculoIds.Clear();
    }

    /// <summary>
    /// La lente de demo Gestor que aplica AHORA, o null. Es solo una coordenada de contexto que
    /// ESTRECHA: todo lo que la usa intersecta con el resultado real, nunca lo sustituye, así
    /// que ni una lente equivocada puede ampliar un alcance. Sin <c>IVistaDemoActual</c>
    /// registrado (producción normal) no hay lente.
    /// </summary>
    private async Task<Guid?> ObtenerGestorDeLenteAsync(CancellationToken cancellationToken) =>
        vistaDemo is not null
        && await vistaDemo.ObtenerEfectivaAsync(cancellationToken) is { Vista: VistaDemo.GestorCae, GestorUsuarioId: { } gestorId }
            ? gestorId
            : null;

    /// <summary>
    /// Acceso total EFECTIVO: el real, salvo que la lente de demo Gestor lo retire. La lente solo
    /// puede pasar de true a false, nunca al revés.
    /// </summary>
    public async Task<bool> TieneAccesoTotalAsync(CancellationToken cancellationToken = default) =>
        await TieneAccesoTotalRealAsync(cancellationToken)
        && await ObtenerGestorDeLenteAsync(cancellationToken) is null;

    private async Task<bool> TieneAccesoTotalRealAsync(CancellationToken cancellationToken)
    {
        var tenant = ClaveTenantVigente();
        var generacion = _generacion;
        if (_accesoTotal.TryGetValue(tenant, out var cacheado)) return cacheado;

        bool accesoTotal;

        // Plano 3 antes que el rol, porque una sesión privilegiada NO tiene rol
        // de negocio: <c>ObtenerRolActualAsync</c> devuelve null a propósito
        // (ADR-011 § 4bis.3 — el técnico de soporte no es miembro del workspace
        // que visita). Sin esta rama, SoporteLectura abriría el contexto del
        // tenant y no vería ni una fila, que es la inspección de soporte
        // convertida en pantalla vacía.
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is { } sesion)
        {
            // "Total" es total DENTRO del tenant objetivo, nunca más allá: el
            // filtro global de tenant sigue puesto y es el que acota (§ 4bis.3
            // — el privilegio cambia por qué se autoriza abrir el contexto,
            // nunca si los filtros aplican).
            //
            // Y solo estas tres capacidades. AdminPlataforma queda fuera a
            // propósito: administrar tenants, facturación y configuración
            // global no incluye leer el contenido documental de nadie, y
            // meterlo aquí reintroduciría el rol monolítico que la matriz por
            // capacidades elimina (§ 4bis.2). Impersonacion también queda
            // fuera: su alcance es el del usuario simulado, no un alcance
            // total, y resolverlo es trabajo de su propia fase.
            //
            // Aprovisionamiento (PD-A3) entra junto a SoporteLectura y
            // BreakGlass: sin acceso total al catálogo del tenant objetivo, el
            // alta de contenido CAE no vería las Empresas/Centros/Trabajadores
            // ya creados en la misma operación para deduplicar contra ellos.
            //
            // Las dos acaban igual: sin acceso total, y con el reparto por
            // cliente saliendo de la rama de rol, que sin rol devuelve lista
            // vacía. Fallo cerrado.
            //
            // Y solo DENTRO del tenant que la sesión abrió: sesion.Capacidad es
            // un dato de la concesión, fijo mientras dure la sesión, y no varía
            // con el tenant que esté de visita. Sin comparar TenantObjetivoId
            // aquí, un fan-out multi-tenant que reutilizara esta misma
            // instancia (scoped) para varios tenants —cambiando solo
            // AmbitoTenantExplicito en cada vuelta, igual que el defecto de
            // #571 con el rol— heredaría "acceso total" en cada tenant
            // visitado a partir de una sesión abierta para uno solo.
            accesoTotal = sesion.TenantObjetivoId == tenantActual.TenantId
                          && (sesion.Capacidad is CapacidadPrivilegio.SoporteLectura
                              or CapacidadPrivilegio.BreakGlass
                              or CapacidadPrivilegio.Aprovisionamiento);
        }
        else
        {
            var rol = await currentUserService.ObtenerRolActualAsync();
            accesoTotal = Roles.AlcanzaTodaLaOrganizacion(rol);
        }

        Memoizar(_accesoTotal, tenant, generacion, accesoTotal);
        return accesoTotal;
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenantVigente();
        var generacion = _generacion;
        if (_clienteIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        var alcance = await ObtenerAlcanceDeCarteraAsync(cancellationToken);
        var resultado = alcance switch
        {
            { SinRestriccion: true } => null,
            // F3b — Empresas, no la tabla legacy Clientes: un Cliente creado tras la congelación
            // solo existe ahí (EsCritico != null lo identifica).
            { TenantEntero: true } => await dbContext.Empresas.Where(e => e.EsCritico != null).Select(e => e.Id).ToListAsync(cancellationToken),
            _ => alcance.ClienteIds
        };

        Memoizar(_clienteIds, tenant, generacion, resultado);
        return resultado;
    }

    /// <summary>
    /// Decisión del propietario 2026-09-23: una Asignación de Cartera de ámbito universal vigente
    /// da al Gestor CAE —y al Coordinador CAE de ese Gestor— TODAS las ramas operativas del Tenant
    /// actual, estén o no unidas a un Cliente empresarial por un Centro, una Relación Empresarial o
    /// una Asignación: un Centro cuyo cliente no es Cliente empresarial, una Subcontrata sin
    /// Relación, un Trabajador de Subcontrata sin Asignación, un Vehículo de Subcontrata...
    ///
    /// <para>
    /// Es autoridad de Operación, nunca de Propiedad: <see cref="TieneAccesoTotalAsync"/> sigue en
    /// false —usuarios, configuración, delegaciones y la autorización de Operadores CAE externos no
    /// dependen de estas listas y quedan fuera— y las listas son EXPLÍCITAS, no null, porque varios
    /// consumidores leen null sin acceso total como denegación (p. ej. EnviarReclamacionCommand).
    /// Clientes: todos los Clientes empresariales del Tenant (<c>EsCritico != null</c>), como hacía
    /// ya la cartera universal. Empresas: toda Empresa. Subcontratas: toda Empresa con
    /// <c>NivelServicio</c>, tenga o no Relación —no un superconjunto, porque los Commands de
    /// Subcontrata no comprueban el tipo y confían en esta lista—. Centros, Trabajadores y
    /// Vehículos: la tabla entera. Todo ello aunque el Tenant no tenga ningún Cliente empresarial:
    /// cada rama mira el Tenant entero antes de cortar por lista de Clientes vacía. El dbContext ya
    /// está acotado al Tenant actual (RLS + filtro global), así que no cruza Tenants.
    /// </para>
    /// </summary>
    private async Task<bool> AlcanzaTenantEnteroPorCarteraAsync(CancellationToken cancellationToken) =>
        (await ObtenerAlcanceDeCarteraAsync(cancellationToken)).TenantEntero;

    private async Task<IReadOnlyList<Guid>> TodasLasEmpresasDelTenantAsync(CancellationToken cancellationToken) =>
        await dbContext.Empresas.Select(e => e.Id).ToListAsync(cancellationToken);

    /// <summary>
    /// Alcance de Clientes efectivo: el real, estrechado por la lente de demo Gestor si la hay.
    /// Memoizado por Tenant junto a los demás alcances.
    /// </summary>
    private async Task<AlcanceCartera> ObtenerAlcanceDeCarteraAsync(CancellationToken cancellationToken)
    {
        var tenant = ClaveTenantVigente();
        var generacion = _generacion;
        if (_alcanceCartera.TryGetValue(tenant, out var cacheado)) return cacheado;

        var real = await ObtenerAlcanceRealAsync(cancellationToken);

        // Lente de demo Gestor: la Asignación de Cartera de ESE Gestor CAE, calculada con el mismo
        // camino que su propia sesión (ObtenerCarteraAsync), e INTERSECADA con lo que la cuenta ya
        // alcanzaba. Sin restricción ∩ cartera = cartera; nunca sale nada que el resultado real no
        // contuviera.
        var resultado = real;
        if (await ObtenerGestorDeLenteAsync(cancellationToken) is { } gestorLente)
            resultado = real.Intersecar(await ObtenerCarteraAsync([gestorLente], cancellationToken));

        Memoizar(_alcanceCartera, tenant, generacion, resultado);
        return resultado;
    }

    private async Task<AlcanceCartera> ObtenerAlcanceRealAsync(CancellationToken cancellationToken)
    {
        if (await TieneAccesoTotalRealAsync(cancellationToken)) return AlcanceCartera.Total;

        var rol = await currentUserService.ObtenerRolActualAsync();
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();

        return (rol, usuarioId) switch
        {
            (Roles.Cliente, { } id) => AlcanceCartera.DeLista(await ObtenerClienteIdsParaRolClienteAsync(id, cancellationToken)),
            (Roles.GestorCae, { } id) => await ObtenerCarteraAsync([id], cancellationToken),
            (Roles.CoordinadorCae, { } id) => await ObtenerCarteraParaCoordinadorAsync(id, cancellationToken),
            _ => AlcanceCartera.Ninguno
        };
    }

    /// <summary>
    /// Alcance de Clientes resuelto: sin restricción (acceso total real), el Tenant entero por
    /// cartera universal, o una lista explícita de Clientes empresariales (vacía = nada).
    /// </summary>
    private sealed record AlcanceCartera(bool SinRestriccion, bool TenantEntero, IReadOnlyList<Guid> ClienteIds)
    {
        public static readonly AlcanceCartera Total = new(true, false, []);
        public static readonly AlcanceCartera Universal = new(false, true, []);
        public static readonly AlcanceCartera Ninguno = new(false, false, []);
        public static AlcanceCartera DeLista(IReadOnlyList<Guid> clienteIds) => new(false, false, clienteIds);

        /// <summary>Intersección: solo puede estrechar. Sin restricción ⊇ Tenant entero ⊇ lista.</summary>
        public AlcanceCartera Intersecar(AlcanceCartera otro) =>
            SinRestriccion ? otro
            : otro.SinRestriccion ? this
            : TenantEntero ? otro
            : otro.TenantEntero ? this
            : DeLista(ClienteIds.Intersect(otro.ClienteIds).ToList());
    }

    /// <summary>
    /// ApplicationUser.ClienteId es, desde F4.2a, un Empresa.Id (ver su
    /// doc-comment) — comparable directamente contra RelacionEmpresarial.ClienteId
    /// en ObtenerEmpresaIdsVisiblesAsync/ObtenerSubcontrataIdsVisiblesAsync.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ObtenerClienteIdsParaRolClienteAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        var clienteId = await dbContext.Users
            .Where(u => u.Id == usuarioId)
            .Select(u => u.ClienteId)
            .FirstOrDefaultAsync(cancellationToken);

        return clienteId is { } id ? [id] : [];
    }

    private async Task<AlcanceCartera> ObtenerCarteraParaCoordinadorAsync(Guid coordinadorUsuarioId, CancellationToken cancellationToken)
    {
        // Sin filtro de cuenta activa, a propósito (decisión del propietario, opción C, 2026-09-24):
        // desactivar a un Gestor CAE no cierra sus Asignaciones de Cartera, y su Coordinador CAE sigue
        // heredándolas —con una universal, el Tenant entero— para que el servicio continúe.
        var gestorIds = await dbContext.Users
            .Where(u => u.CoordinadorUsuarioId == coordinadorUsuarioId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        if (gestorIds.Count == 0) return AlcanceCartera.Ninguno;

        return await ObtenerCarteraAsync(gestorIds, cancellationToken);
    }

    /// <summary>
    /// La cartera de uno o varios usuarios, leída de las asignaciones
    /// operativas (F1 del plan de migración). Sustituye a la consulta directa
    /// sobre <c>Cliente.EjecutivoUsuarioId</c>, que queda como proyección de
    /// compatibilidad para los lectores informativos.
    ///
    /// Dos condiciones que no estaban en el modelo anterior y que ahora hay que
    /// imponer explícitamente:
    /// <list type="bullet">
    /// <item>la cartera debe pertenecer al <b>tenant en el que se está
    /// operando</b>. Un usuario puede tener carteras en varios tenants (el suyo
    /// y los que opera por delegación), y sin este filtro los clientes de un
    /// workspace se colarían en otro;</item>
    /// <item>su operación debe estar <b>vigente</b>. Una cartera bajo una
    /// operación cerrada o suspendida no concede nada, y el cierre en cascada
    /// puede no haber corrido todavía si la operación caducó por fecha.</item>
    /// </list>
    /// Una cartera de ámbito universal sobre este tenant da el Tenant entero
    /// (<see cref="AlcanzaTenantEnteroPorCarteraAsync"/>), no solo sus Clientes
    /// empresariales. Si varios usuarios aportan carteras (Coordinador CAE), basta
    /// una universal.
    /// </summary>
    private async Task<AlcanceCartera> ObtenerCarteraAsync(
        IReadOnlyList<Guid> usuarioIds, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } propietarioTenantId) return AlcanceCartera.Ninguno;

        // La otra mitad de la política de posición: además de que la cartera
        // sea del tenant en el que se opera, la operación que la ampara tiene
        // que estar operada por el tenant de ORIGEN del usuario. Sin esto, una
        // cartera mal formada cuyo PropietarioTenantId casara con el tenant
        // activo entraría en el alcance aunque perteneciera a otra posición.
        // Es el tenant del claim de sesión, nunca el activo: dentro de un
        // workspace delegado el activo es el del propietario.
        var operadorTenantId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (operadorTenantId is null) return AlcanceCartera.Ninguno;

        var ahora = DateTime.UtcNow;

        var carteras = await dbContext.AsignacionesCartera
            .Where(c => usuarioIds.Contains(c.UsuarioId)
                        && c.PropietarioTenantId == propietarioTenantId
                        && c.Estado == EstadoAsignacion.Vigente
                        && c.VigenciaDesde <= ahora
                        && (c.VigenciaHasta == null || ahora < c.VigenciaHasta))
            .Join(dbContext.AsignacionesOperacion.Where(o =>
                    o.Estado == EstadoAsignacion.Vigente
                    && o.OperadorTenantId == operadorTenantId.Value
                    && o.VigenciaDesde <= ahora
                    && (o.VigenciaHasta == null || ahora < o.VigenciaHasta)),
                c => c.AsignacionOperacionId, o => o.Id, (c, o) => c.AmbitoRelacionClienteId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (carteras.Count == 0) return AlcanceCartera.Ninguno;

        // Ámbito universal: el Tenant entero (decisión del propietario 2026-09-23: la cartera de un
        // Gestor CAE es siempre sobre el Tenant entero). Las listas las materializa cada método de
        // rama: ver AlcanzaTenantEnteroPorCarteraAsync. Un rol de alcance total ya salió por
        // TieneAccesoTotalAsync sin consultar carteras; a un rol de cartera solo se le emite una
        // universal cuando un Coordinador CAE acepta su solicitud de incorporación al Tenant
        // propietario entero (CatalogoIncorporacionCartera).
        if (carteras.Any(id => id is null))
            return AlcanceCartera.Universal;

        return AlcanceCartera.DeLista(carteras.Where(id => id is not null).Select(id => id!.Value).ToList());
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenantVigente();
        var generacion = _generacion;
        if (_centroIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);

        IReadOnlyList<Guid>? resultado = clienteIds switch
        {
            null => null,
            // Antes que el corte por lista vacía: un Tenant sin ningún Cliente empresarial sigue
            // siendo entero para una cartera universal.
            _ when await AlcanzaTenantEnteroPorCarteraAsync(cancellationToken) =>
                await dbContext.Centros.Select(c => c.Id).ToListAsync(cancellationToken),
            { Count: 0 } => [],
            _ => await dbContext.Centros
                .Where(c => clienteIds.Contains(c.ClienteId))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken)
        };
        Memoizar(_centroIds, tenant, generacion, resultado);

        return resultado;
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerCentroIdsParaGestionAsync(CancellationToken cancellationToken = default)
    {
        // Mismo criterio que ObtenerEmpresaIdsParaGestionAsync (REC-153): el rol
        // Cliente es un usuario de portal y ve el estado de sus Centros, pero
        // no opera sobre ellos. Lista vacía y no null — null significa "sin
        // restricción", que aquí sería exactamente lo contrario de lo que toca
        // (fallo cerrado).
        if (await currentUserService.ObtenerRolActualAsync() == Roles.Cliente)
            return [];

        return await ObtenerCentroIdsVisiblesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsParaGestionAsync(CancellationToken cancellationToken = default)
    {
        // El rol Cliente es un usuario de portal: ve la documentación de las
        // contratistas relacionadas con su Cliente, pero no opera sobre ellas.
        // Lista vacía y no null — null significa "sin restricción", que aquí
        // sería exactamente lo contrario de lo que toca (fallo cerrado).
        if (await currentUserService.ObtenerRolActualAsync() == Roles.Cliente)
            return [];

        return await ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
    }

    /// <summary>
    /// F4 — reescrito sobre <c>RelacionEmpresarial</c> en vez de
    /// <c>EmpresaCliente</c> (contrato verificado con paridad exacta OLD/NEW,
    /// ver f4-diseno-fisico-relacionempresarial-2026-08-26.md § 6/8ter).
    /// <c>porCentro</c> no cambia: F4 no toca <c>Centro</c> (eso es F5).
    ///
    /// D-8 (piloto Outbound): además, un Gestor/Coordinador CAE con cartera no vacía ve las Empresas
    /// propias del Tenant actual aunque no exista Centro ni Relación Empresarial que las una a un
    /// Cliente de su cartera (la Empresa propia es parte estructural del contexto, no algo que
    /// haya que fabricar con un Centro o una Relación). Con el mismo criterio
    /// (<see cref="IncluyeEstructuraPropiaAsync"/>) ve también a todos sus Trabajadores, tengan o no
    /// Asignación: ver <see cref="ObtenerTrabajadorIdsVisiblesAsync"/>.
    ///
    /// El filtro <c>Proveedora.EsPropia</c> repone una garantía que antes
    /// daba gratis la separación física de tablas — en la tabla unificada
    /// hay que comprobarlo explícitamente, o una relación Subcontrata→Cliente
    /// (mismo ClienteId) se colaría aquí.
    /// </summary>
    public async Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenantVigente();
        var generacion = _generacion;
        if (_empresaIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        // Antes que el corte por lista vacía: un Tenant sin ningún Cliente empresarial sigue siendo
        // entero para una cartera universal.
        if (await AlcanzaTenantEnteroPorCarteraAsync(cancellationToken))
        {
            var todas = await TodasLasEmpresasDelTenantAsync(cancellationToken);
            Memoizar(_empresaIds, tenant, generacion, todas);
            return todas;
        }

        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);

        if (clienteIds is null || clienteIds.Count == 0)
        {
            var vacioOSinRestriccion = clienteIds is null ? null : (IReadOnlyList<Guid>)[];
            Memoizar(_empresaIds, tenant, generacion, vacioOSinRestriccion);
            return vacioOSinRestriccion;
        }

        var porCentro = dbContext.Centros.Where(c => clienteIds.Contains(c.ClienteId)).Select(c => c.EmpresaId);
        var porVinculoDirecto = dbContext.RelacionesEmpresariales
            .Where(r => clienteIds.Contains(r.ClienteId) && r.VigenciaHasta == null)
            .Join(dbContext.Empresas.Where(e => e.EsPropia), r => r.ProveedoraId, e => e.Id, (r, e) => e.Id);

        // D-8 (piloto Outbound): la Empresa propia es parte estructural del Tenant del que el Gestor
        // tiene cartera, con o sin Centro ni Relación Empresarial.
        // dbContext ya está acotado al Tenant actual (RLS + filtro), así que no cruza Tenants.
        var visibles = porCentro.Concat(porVinculoDirecto);
        if (await IncluyeEstructuraPropiaAsync(cancellationToken))
            visibles = visibles.Concat(dbContext.Empresas.Where(e => e.EsPropia).Select(e => e.Id));

        var resultado = await visibles.Distinct().ToListAsync(cancellationToken);
        Memoizar(_empresaIds, tenant, generacion, resultado);

        return resultado;
    }

    /// <summary>
    /// D-8 (piloto Outbound): si quien mira, con cartera no vacía en el Tenant actual, alcanza la
    /// estructura propia del Tenant —la Empresa propia y toda su plantilla— sin que haga falta un
    /// Centro, una Relación Empresarial ni una Asignación que la una a un Cliente de su cartera.
    /// Criterio único para <see cref="ObtenerEmpresaIdsVisiblesAsync"/> y
    /// <see cref="ObtenerTrabajadorIdsVisiblesAsync"/>: si divergieran, un Gestor CAE vería la
    /// Empresa propia sin su plantilla, o al revés.
    ///
    /// Solo los roles de cartera de Operador (Gestor/Coordinador CAE): el rol Cliente (portal) no
    /// gana estructura del Tenant, y cualquier otro rol que llegue aquí con cartera no vacía sigue
    /// sin ella (falla cerrado). Consulta no pasa por aquí porque ya tiene alcance total dentro del
    /// Tenant. La lente de demo Gestor muestra lo mismo que vería ese Gestor con su propia cuenta,
    /// así que también la incluye (sin ella, la lente y la cuenta real mostrarían datos distintos);
    /// solo cuenta si quien mira ya tiene alcance total real (Administrador, Consulta), para quien
    /// la estructura propia ya estaba a su alcance: la lente no amplía nada.
    ///
    /// Solo decide el rol: quien llama ya ha comprobado que la cartera no está vacía.
    /// </summary>
    private async Task<bool> IncluyeEstructuraPropiaAsync(CancellationToken cancellationToken) =>
        await currentUserService.ObtenerRolActualAsync() is Roles.GestorCae or Roles.CoordinadorCae
        || (await ObtenerGestorDeLenteAsync(cancellationToken) is not null
            && await TieneAccesoTotalRealAsync(cancellationToken));

    /// <summary>
    /// F4 — reescrito sobre <c>RelacionEmpresarial</c> en vez de
    /// <c>SubcontrataCliente</c>/<c>SubcontrataEmpresa</c> (contrato
    /// verificado con paridad exacta OLD/NEW).
    ///
    /// El filtro <c>Proveedora.NivelServicio != null</c> es el marcador
    /// TRANSITORIO de F3a que distingue una subcontrata — no el contrato
    /// definitivo. Se retira cuando F4 termine de re-anclar el nivel de
    /// servicio a <c>RelacionEmpresarial</c>, nunca antes: ver el ratchet
    /// pendiente (§ 6 del diseño físico) que debe existir antes de retirar
    /// esa columna mientras este método siga dependiendo de ella.
    /// </summary>
    public async Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenantVigente();
        var generacion = _generacion;
        if (_subcontrataIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        // Antes que el corte por lista vacía: un Tenant sin ningún Cliente empresarial sigue siendo
        // entero para una cartera universal.
        // Tenant entero: toda Subcontrata (NivelServicio != null), tenga o no Relación. No todas las
        // Empresas: los Commands de Subcontrata cargan la Empresa sin comprobar su tipo y confían en
        // esta lista, así que con un superconjunto la Empresa propia o un Cliente empresarial
        // podrían tratarse como Subcontrata.
        if (await AlcanzaTenantEnteroPorCarteraAsync(cancellationToken))
        {
            var todas = await dbContext.Empresas.Where(e => e.NivelServicio != null).Select(e => e.Id).ToListAsync(cancellationToken);
            Memoizar(_subcontrataIds, tenant, generacion, todas);
            return todas;
        }

        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);

        if (clienteIds is null || clienteIds.Count == 0)
        {
            var vacioOSinRestriccion = clienteIds is null ? null : (IReadOnlyList<Guid>)[];
            Memoizar(_subcontrataIds, tenant, generacion, vacioOSinRestriccion);
            return vacioOSinRestriccion;
        }

        var empresaIds = await ObtenerEmpresaIdsVisiblesAsync(cancellationToken) ?? [];

        var relacionesConSubcontrataComoProveedora = dbContext.RelacionesEmpresariales
            .Where(r => r.VigenciaHasta == null)
            .Join(dbContext.Empresas.Where(e => e.NivelServicio != null), r => r.ProveedoraId, e => e.Id, (r, e) => new { r.ClienteId, SubcontrataId = e.Id });

        var porCliente = relacionesConSubcontrataComoProveedora.Where(x => clienteIds.Contains(x.ClienteId)).Select(x => x.SubcontrataId);
        var porEmpresa = relacionesConSubcontrataComoProveedora.Where(x => empresaIds.Contains(x.ClienteId)).Select(x => x.SubcontrataId);

        var resultado = await porCliente.Concat(porEmpresa).Distinct().ToListAsync(cancellationToken);
        Memoizar(_subcontrataIds, tenant, generacion, resultado);

        return resultado;
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsParaGestionAsync(CancellationToken cancellationToken = default)
    {
        // Mismo criterio que ObtenerEmpresaIdsParaGestionAsync (REC-159, gemelo
        // de REC-153): el rol Cliente es un usuario de portal y ve la
        // documentación de las subcontratas de su Cliente, pero no opera sobre
        // ellas. Lista vacía y no null — null significa "sin restricción", que
        // aquí sería exactamente lo contrario de lo que toca (fallo cerrado).
        if (await currentUserService.ObtenerRolActualAsync() == Roles.Cliente)
            return [];

        return await ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
    }

    /// <summary>
    /// Dos vías, unidas:
    /// <list type="bullet">
    /// <item>por Asignación: los Trabajadores —de cualquier empleador— con una <c>Asignacion</c>
    /// activa en un Centro visible;</item>
    /// <item>por estructura propia (decisión del propietario 2026-09-22, opción A): toda la
    /// plantilla de la Empresa propia del Tenant, desde su alta y sin Asignación, para que el Gestor
    /// CAE prepare el expediente antes de la primera. Mismo criterio que la Empresa propia en
    /// <see cref="ObtenerEmpresaIdsVisiblesAsync"/> (<see cref="IncluyeEstructuraPropiaAsync"/>).
    /// Solo Trabajadores de la Empresa propia: los de una Subcontrata o de otra Empresa contraparte
    /// siguen entrando únicamente por Asignación.</item>
    /// </list>
    /// La condición de entrada es la cartera (Clientes visibles), no los Centros: un Gestor CAE con
    /// cartera pero sin ningún Centro todavía ve la plantilla propia. Sin cartera, [].
    /// </summary>
    public async Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenantVigente();
        var generacion = _generacion;
        if (_trabajadorIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        // Antes que el corte por lista vacía: un Tenant sin ningún Cliente empresarial sigue siendo
        // entero para una cartera universal.
        if (await AlcanzaTenantEnteroPorCarteraAsync(cancellationToken))
        {
            var todos = await dbContext.Trabajadores.Select(t => t.Id).ToListAsync(cancellationToken);
            Memoizar(_trabajadorIds, tenant, generacion, todos);
            return todos;
        }

        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);

        if (clienteIds is null || clienteIds.Count == 0)
        {
            var vacioOSinRestriccion = clienteIds is null ? null : (IReadOnlyList<Guid>)[];
            Memoizar(_trabajadorIds, tenant, generacion, vacioOSinRestriccion);
            return vacioOSinRestriccion;
        }

        var centroIds = await ObtenerCentroIdsVisiblesAsync(cancellationToken) ?? [];
        var visibles = dbContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId) && a.FechaBaja == null)
            .Select(a => a.TrabajadorId);

        // dbContext ya está acotado al Tenant actual (RLS + filtro), así que no cruza Tenants. Un
        // Trabajador de Subcontrata tiene EmpresaId null (CK_Trabajadores_EmpresaXorSubcontrata), así
        // que el join con la Empresa propia ya lo deja fuera.
        if (await IncluyeEstructuraPropiaAsync(cancellationToken))
            visibles = visibles.Concat(dbContext.Trabajadores
                .Join(dbContext.Empresas.Where(e => e.EsPropia), t => t.EmpresaId, e => (Guid?)e.Id, (t, e) => t.Id));

        var resultado = await visibles.Distinct().ToListAsync(cancellationToken);
        Memoizar(_trabajadorIds, tenant, generacion, resultado);

        return resultado;
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenantVigente();
        var generacion = _generacion;
        if (_vehiculoIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        var empresaIds = await ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        if (empresaIds is null)
        {
            Memoizar(_vehiculoIds, tenant, generacion, null);
            return null;
        }

        if (await AlcanzaTenantEnteroPorCarteraAsync(cancellationToken))
        {
            var todos = await dbContext.Vehiculos.Select(v => v.Id).ToListAsync(cancellationToken);
            Memoizar(_vehiculoIds, tenant, generacion, todos);
            return todos;
        }

        var subcontrataIds = await ObtenerSubcontrataIdsVisiblesAsync(cancellationToken) ?? [];

        IReadOnlyList<Guid> resultado = empresaIds.Count == 0 && subcontrataIds.Count == 0
            ? []
            : await dbContext.Vehiculos
                .Where(v =>
                    (v.EmpresaId != null && empresaIds.Contains(v.EmpresaId.Value)) ||
                    (v.SubcontrataId != null && subcontrataIds.Contains(v.SubcontrataId.Value)))
                .Select(v => v.Id)
                .ToListAsync(cancellationToken);
        Memoizar(_vehiculoIds, tenant, generacion, resultado);

        return resultado;
    }

    /// <summary>
    /// Sin memoización por diseño: a diferencia de los seis alcances de
    /// arriba (un único valor por request, reutilizado por varios filtros de
    /// una misma Query), esto se llama con un Id distinto cada vez —
    /// memoizar por Id sería un diccionario para un método que ya resuelve
    /// con una única consulta indexada por clave primaria.
    /// </summary>
    public async Task<bool> ConexionIntegracionVisibleAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default)
    {
        var propietarioId = await dbContext.ConexionesIntegracion
            .Where(c => c.Id == conexionIntegracionId)
            .Select(c => c.GestorPropietarioId)
            .FirstOrDefaultAsync(cancellationToken);

        if (propietarioId is null) return true;

        var usuarioActualId = await currentUserService.ObtenerUsuarioActualIdAsync();
        return propietarioId == usuarioActualId;
    }
}
