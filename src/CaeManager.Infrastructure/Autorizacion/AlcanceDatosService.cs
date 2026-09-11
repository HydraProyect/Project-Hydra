using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
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
/// petición cuando varios filtros de una Query la necesitan.
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
    ISesionPrivilegiadaActual sesionPrivilegiadaActual)
    : IAlcanceDatosService
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
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _clienteIds = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _centroIds = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _empresaIds = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _subcontrataIds = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _trabajadorIds = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>?> _vehiculoIds = new();

    private static Guid ClaveTenant(Guid? tenantId) => tenantId ?? Guid.Empty;

    public async Task<bool> TieneAccesoTotalAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenant(tenantActual.TenantId);
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
            // Y solo estas dos capacidades. AdminPlataforma queda fuera a
            // propósito: administrar tenants, facturación y configuración
            // global no incluye leer el contenido documental de nadie, y
            // meterlo aquí reintroduciría el rol monolítico que la matriz por
            // capacidades elimina (§ 4bis.2). Impersonacion también queda
            // fuera: su alcance es el del usuario simulado, no un alcance
            // total, y resolverlo es trabajo de su propia fase.
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
                          && (sesion.Capacidad is CapacidadPrivilegio.SoporteLectura or CapacidadPrivilegio.BreakGlass);
        }
        else
        {
            var rol = await currentUserService.ObtenerRolActualAsync();
            accesoTotal = Roles.AlcanzaTodaLaOrganizacion(rol);
        }

        _accesoTotal[tenant] = accesoTotal;
        return accesoTotal;
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenant(tenantActual.TenantId);
        if (_clienteIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        if (await TieneAccesoTotalAsync(cancellationToken))
        {
            _clienteIds[tenant] = null;
            return null;
        }

        var rol = await currentUserService.ObtenerRolActualAsync();
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();

        var resultado = (rol, usuarioId) switch
        {
            (Roles.Cliente, { } id) => await ObtenerClienteIdsParaRolClienteAsync(id, cancellationToken),
            (Roles.GestorCae, { } id) => await ObtenerClienteIdsDeCarteraAsync([id], cancellationToken),
            (Roles.CoordinadorCae, { } id) => await ObtenerClienteIdsParaCoordinadorAsync(id, cancellationToken),
            _ => (IReadOnlyList<Guid>)[]
        };
        _clienteIds[tenant] = resultado;

        return resultado;
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

    private async Task<IReadOnlyList<Guid>> ObtenerClienteIdsParaCoordinadorAsync(Guid coordinadorUsuarioId, CancellationToken cancellationToken)
    {
        var gestorIds = await dbContext.Users
            .Where(u => u.CoordinadorUsuarioId == coordinadorUsuarioId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        if (gestorIds.Count == 0) return [];

        return await ObtenerClienteIdsDeCarteraAsync(gestorIds, cancellationToken);
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
    /// Una cartera de ámbito universal sobre este tenant (el caso de un
    /// operador delegado sin reparto interno) da acceso a todos sus clientes.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ObtenerClienteIdsDeCarteraAsync(
        IReadOnlyList<Guid> usuarioIds, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } propietarioTenantId) return [];

        // La otra mitad de la política de posición: además de que la cartera
        // sea del tenant en el que se opera, la operación que la ampara tiene
        // que estar operada por el tenant de ORIGEN del usuario. Sin esto, una
        // cartera mal formada cuyo PropietarioTenantId casara con el tenant
        // activo entraría en el alcance aunque perteneciera a otra posición.
        // Es el tenant del claim de sesión, nunca el activo: dentro de un
        // workspace delegado el activo es el del propietario.
        var operadorTenantId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (operadorTenantId is null) return [];

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

        if (carteras.Count == 0) return [];

        // Ámbito universal: todos los clientes del tenant. Solo llega aquí un
        // rol de alcance total, y esos ya salieron por TieneAccesoTotalAsync
        // sin consultar carteras — a un rol de cartera no se le emite nunca una
        // universal, justamente para no ensanchar su alcance.
        //
        // F3b — Empresas, no la tabla legacy Clientes: un Cliente creado tras
        // la congelación solo existe ahí (EsCritico != null lo identifica).
        if (carteras.Any(id => id is null))
            return await dbContext.Empresas.Where(e => e.EsCritico != null).Select(e => e.Id).ToListAsync(cancellationToken);

        return carteras.Where(id => id is not null).Select(id => id!.Value).ToList();
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenant(tenantActual.TenantId);
        if (_centroIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);

        IReadOnlyList<Guid>? resultado = clienteIds switch
        {
            null => null,
            { Count: 0 } => [],
            _ => await dbContext.Centros
                .Where(c => clienteIds.Contains(c.ClienteId))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken)
        };
        _centroIds[tenant] = resultado;

        return resultado;
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
    /// El filtro <c>Proveedora.EsPropia</c> repone una garantía que antes
    /// daba gratis la separación física de tablas — en la tabla unificada
    /// hay que comprobarlo explícitamente, o una relación Subcontrata→Cliente
    /// (mismo ClienteId) se colaría aquí.
    /// </summary>
    public async Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenant(tenantActual.TenantId);
        if (_empresaIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);

        if (clienteIds is null || clienteIds.Count == 0)
        {
            var vacioOSinRestriccion = clienteIds is null ? null : (IReadOnlyList<Guid>)[];
            _empresaIds[tenant] = vacioOSinRestriccion;
            return vacioOSinRestriccion;
        }

        var porCentro = dbContext.Centros.Where(c => clienteIds.Contains(c.ClienteId)).Select(c => c.EmpresaId);
        var porVinculoDirecto = dbContext.RelacionesEmpresariales
            .Where(r => clienteIds.Contains(r.ClienteId) && r.VigenciaHasta == null)
            .Join(dbContext.Empresas.Where(e => e.EsPropia), r => r.ProveedoraId, e => e.Id, (r, e) => e.Id);

        var resultado = await porCentro.Concat(porVinculoDirecto).Distinct().ToListAsync(cancellationToken);
        _empresaIds[tenant] = resultado;

        return resultado;
    }

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
        var tenant = ClaveTenant(tenantActual.TenantId);
        if (_subcontrataIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        var clienteIds = await ObtenerClienteIdsVisiblesAsync(cancellationToken);

        if (clienteIds is null || clienteIds.Count == 0)
        {
            var vacioOSinRestriccion = clienteIds is null ? null : (IReadOnlyList<Guid>)[];
            _subcontrataIds[tenant] = vacioOSinRestriccion;
            return vacioOSinRestriccion;
        }

        var empresaIds = await ObtenerEmpresaIdsVisiblesAsync(cancellationToken) ?? [];

        var relacionesConSubcontrataComoProveedora = dbContext.RelacionesEmpresariales
            .Where(r => r.VigenciaHasta == null)
            .Join(dbContext.Empresas.Where(e => e.NivelServicio != null), r => r.ProveedoraId, e => e.Id, (r, e) => new { r.ClienteId, SubcontrataId = e.Id });

        var porCliente = relacionesConSubcontrataComoProveedora.Where(x => clienteIds.Contains(x.ClienteId)).Select(x => x.SubcontrataId);
        var porEmpresa = relacionesConSubcontrataComoProveedora.Where(x => empresaIds.Contains(x.ClienteId)).Select(x => x.SubcontrataId);

        var resultado = await porCliente.Concat(porEmpresa).Distinct().ToListAsync(cancellationToken);
        _subcontrataIds[tenant] = resultado;

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

    public async Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenant(tenantActual.TenantId);
        if (_trabajadorIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        var centroIds = await ObtenerCentroIdsVisiblesAsync(cancellationToken);

        IReadOnlyList<Guid>? resultado = centroIds switch
        {
            null => null,
            { Count: 0 } => [],
            _ => await dbContext.Asignaciones
                .Where(a => centroIds.Contains(a.CentroId) && a.FechaBaja == null)
                .Select(a => a.TrabajadorId)
                .Distinct()
                .ToListAsync(cancellationToken)
        };
        _trabajadorIds[tenant] = resultado;

        return resultado;
    }

    public async Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default)
    {
        var tenant = ClaveTenant(tenantActual.TenantId);
        if (_vehiculoIds.TryGetValue(tenant, out var cacheado)) return cacheado;

        var empresaIds = await ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        if (empresaIds is null)
        {
            _vehiculoIds[tenant] = null;
            return null;
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
        _vehiculoIds[tenant] = resultado;

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
