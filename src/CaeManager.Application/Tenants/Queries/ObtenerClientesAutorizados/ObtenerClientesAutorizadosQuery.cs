using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Operaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;

/// <summary>
/// Los Tenants beneficiarios que el usuario actual puede activar como contexto,
/// por las tres vías de <see cref="TenantsBeneficiariosAutorizados"/> — el mismo
/// predicado que aplican el POST <c>/cuenta/cliente-activo</c> y la revalidación
/// (invariante I2 del contrato del selector de Tenant beneficiario): su Tenant de
/// origen, los Tenants alcanzados por Asignación de Cartera vigente bajo
/// Asignación de Operación vigente de su Operador CAE de origen, y los de la vía
/// heredada de delegación.
///
/// <para>
/// Consumidores y efecto de incluir la vía de Operación (lote 0, decisión 6 del
/// propietario del producto, 2026-09-26): el selector de cabecera, Mi trabajo, los
/// agregados de Dashboard (KPIs globales, Visión de cartera, Dashboard ejecutivo),
/// el asistente IA (candidatos y comprobación de instrucción), el aterrizaje de
/// Inicio y la navegación (<c>NavMenu</c>, «varios Tenants») se ensanchan todos a
/// propósito: cada uno pregunta «qué Tenants alcanza este usuario», y un Tenant
/// alcanzado por Operación lo es igual que uno delegado. En la práctica solo
/// cambia algo para las carteras sin fila heredada: la incorporación de cartera
/// escribe también la delegación (<c>CatalogoIncorporacionCartera</c>).
/// </para>
/// </summary>
[AreaDelEncargo("Tenants")]
public record ObtenerClientesAutorizadosQuery : IRequest<IReadOnlyList<ClienteAutorizadoDto>>
{
    /// <summary>
    /// Leer de la base aunque el ámbito tenga la lista memoizada (<see cref="MemoClientesAutorizados"/>), y no
    /// guardarla. Para las puertas que fallan cerradas sobre la cartera (DEC-33, instrucción de IA), donde un
    /// Tenant recién incorporado desde otro circuito no puede esperar a la caducidad.
    /// </summary>
    public bool LecturaVigente { get; init; }

    /// <summary>
    /// Una entrada por Asignación de Operación para el selector de papel (decisión D-4 de Chris del 2026-11-16; DN-21):
    /// quien llega a un Tenant beneficiario por una Asignación de Operación de su Operador CAE externo, sea cual sea su
    /// perfil de origen (Administrador, Dirección CAE, Coordinador CAE, Gestor CAE o Consulta; decisión O-2 (b) de Chris
    /// del 2026-11-17), recibe una entrada por cada par de Tenant beneficiario y servicio
    /// (<see cref="ClienteAutorizadoDto.Servicio"/>), así que un mismo Tenant puede salir dos veces, una con Outbound y
    /// otra con Inbound. Las deciden los mismos predicados y el mismo perfil que el POST <c>/cuenta/cliente-activo</c>.
    /// Solo lo pide el selector de la cabecera: todos los demás lectores agrupan por Tenant y siguen recibiendo la forma
    /// de siempre, una entrada por Tenant y sin servicio. Sin ninguna Asignación de Operación (el Administrador del Tenant
    /// propio, la vía heredada) la lista es la misma que sin esta opción. No autoriza nada: el POST revalida el servicio pedido.
    /// </summary>
    public bool PorPapel { get; init; }

    /// <summary>
    /// La lista por Tenant de siempre para los lectores del Tenant activo de la interfaz (cabecera de las pantallas de un
    /// Tenant a la vez, estado 4a de las exportaciones, paleta de comandos, asistente IA, fichas y paneles no encontrados y
    /// aviso de enlace profundo): su única fuente. Cuando la persona tiene activo, con el papel Inbound que resolvió el
    /// servidor (<see cref="ICurrentUserService.ObtenerPapelActivoAsync"/>), un Tenant beneficiario al que solo llega por
    /// una Asignación de Operación Inbound (D-4), sea cual sea su perfil de origen (O-2 (b)), la lista añade ese Tenant, y
    /// solo ese, con <see cref="ClienteAutorizadoDto.Servicio"/> Inbound; sin esto <see cref="ClientesAutorizados.Activo"/>
    /// caería al Tenant de origen o al estado 4a. Con el papel Outbound, sin papel, sin una Asignación de Operación Inbound
    /// que lo autorice o con un Tenant activo que ya está en la lista, devuelve la lista por Tenant sin cambios. Es presentación: no autoriza nada, y el Tenant efectivo, la
    /// revalidación de la selección y RLS siguen decidiendo qué se lee.
    /// </summary>
    public bool ParaTenantActivo { get; init; }
}

/// <param name="EsGestionadoPorOperacion">
/// El usuario gestiona ese Tenant por la vía de Operación (Asignación de Cartera
/// vigente). Para el Tenant de origen, que tenga cartera sobre sí mismo. Decide la
/// visibilidad del selector (decisión 1) y el Tenant por defecto (decisión 5); no
/// autoriza nada.
/// </param>
/// <param name="LogoVersion">
/// Versión del logo del Tenant, o <c>null</c> si no tiene: construye la URL versionada del endpoint del
/// logo (contrato del selector, § 4.1.5). No es un secreto ni autoriza nada; el endpoint revalida.
/// </param>
/// <param name="EsCarteraGestorCae">
/// La Asignación de Cartera vigente que da acceso a ese Tenant es de rol Gestor CAE
/// (leído del dato vigente, nunca de la claim). Condición de la decisión 7 quater
/// para el Tenant por defecto; no autoriza nada. Siempre falso para el origen y para
/// la vía heredada.
/// </param>
public record ClienteAutorizadoDto(
    Guid TenantId, string Nombre, bool EsOrigen, bool EsGestionadoPorOperacion = false, string? LogoVersion = null,
    bool EsCarteraGestorCae = false)
{
    /// <summary>
    /// El servicio de la Asignación de Operación de esta entrada (el papel que el selector envía al elegirla), o
    /// <c>null</c>: siempre <c>null</c> fuera de <see cref="ObtenerClientesAutorizadosQuery.PorPapel"/>, y también ahí para
    /// el Tenant de origen y para la vía heredada, que no llegan por una Asignación de Operación (sin selector de papel,
    /// § 1.4 de la interfaz Inbound y Outbound; O-2 (b)). Es una coordenada de presentación, no una autoridad.
    /// </summary>
    public ServicioCae? Servicio { get; init; }
}

public static class ClientesAutorizados
{
    /// <summary>
    /// Visibilidad del selector de Tenant beneficiario (decisión 1, evaluada por
    /// condición y no por rol): el usuario alcanza dos o más Tenants beneficiarios
    /// por la vía de Operación (el Tenant de origen cuenta solo si tiene cartera
    /// sobre él). Con un único Tenant —incluido el que decide la decisión 5 por
    /// defecto— el selector se oculta.
    ///
    /// <para>
    /// Un Tenant alcanzado solo por la vía heredada de delegación (soporte,
    /// Operador Delegado) también muestra el selector: es su único control para
    /// pasar de su Tenant de origen a ese Tenant y volver, y hasta ahora lo veía.
    /// No lo concede: la lista ya es el conjunto autorizado.
    /// </para>
    /// </summary>
    public static bool SelectorVisible(IReadOnlyList<ClienteAutorizadoDto> autorizados, ClienteAutorizadoDto? activo) =>
        autorizados.Count(c => c.EsGestionadoPorOperacion) >= 2
        || autorizados.Any(c => !c.EsOrigen && !c.EsGestionadoPorOperacion)
        // Decisión 7 quater: un Tenant externo de cartera que no es de rol Gestor CAE (p. ej. Consulta)
        // nunca es el Tenant por defecto; si el usuario lo abre, el selector es su único control
        // para volver a su Tenant de origen, así que se mantiene aunque sea el único.
        || autorizados.Any(c => !c.EsOrigen && c.EsGestionadoPorOperacion && !c.EsCarteraGestorCae)
        // Mientras el contexto efectivo sea el origen y haya algún Tenant externo, el control
        // se mantiene: quien volvió al origen a propósito (el Tenant por defecto respeta esa
        // preferencia 8 h) o cuya cartera bajó a un solo Tenant no tendría otra vía de elegirlo.
        || (activo is { EsOrigen: true } && autorizados.Any(c => !c.EsOrigen))
        // D-4: una entrada Inbound hace visible el selector de la cabecera (SelectorClienteActivo), así que también
        // para los lectores del Tenant activo. Solo la trae ObtenerClientesAutorizadosQuery.ParaTenantActivo, cuando el
        // Tenant activo se alcanza solo por Inbound; en la lista por Tenant de siempre Servicio es null y nada cambia.
        || autorizados.Any(c => c.Servicio == ServicioCae.Inbound);

    /// <summary>
    /// Tenants externos que el usuario puede abrir: las filas de «Mi cartera» del
    /// selector. El Tenant de origen no cuenta aquí: va aparte, fijo bajo la lista,
    /// como «Tu organización». Es el «N» de «Mi cartera · N empresas».
    /// </summary>
    public static int TotalCartera(IReadOnlyList<ClienteAutorizadoDto> autorizados) =>
        autorizados.Count(c => !c.EsOrigen);

    /// <summary>
    /// Estado 4a del mockup del selector: el usuario gestiona Tenants externos por
    /// Operación, su Tenant de origen no está en su cartera y el activo es ese
    /// origen (nunca eligió uno, o volvió a él). Las pantallas de un Tenant a la
    /// vez piden entonces elegir uno en vez de presentar los datos del origen como
    /// si fueran de la cartera. No aplica al Administrador del Operador CAE sin
    /// cartera (su origen es su sitio) ni a quien solo tiene la vía heredada.
    /// </summary>
    public static bool SinEmpresaSeleccionada(IReadOnlyList<ClienteAutorizadoDto> autorizados, ClienteAutorizadoDto? activo) =>
        activo is { EsOrigen: true, EsGestionadoPorOperacion: false }
        && autorizados.Any(c => !c.EsOrigen && c.EsGestionadoPorOperacion);

    /// <summary>
    /// UNA sola fuente de la condición del estado 4a para quien tenga que decidir si
    /// una pantalla, un endpoint o una acción de la paleta sirve los datos del Tenant
    /// activo: cierto cuando el selector es visible y <see cref="SinEmpresaSeleccionada"/>.
    /// La usan los endpoints de exportación de los listados que la aplican (Trabajadores,
    /// Vehículos, Proyectos, Documentos, Empresas, Subcontratas, Clientes y Centros) y la
    /// paleta de comandos; las páginas de lista la resuelven con
    /// <c>ContextoEmpresaActiva.ResolverAsync</c>, que compone las mismas dos condiciones
    /// (<see cref="SelectorVisible"/> y <see cref="SinEmpresaSeleccionada"/>) sobre el
    /// mismo <see cref="Activo"/>.
    /// </summary>
    public static bool PideElegirEmpresa(IReadOnlyList<ClienteAutorizadoDto> autorizados, Guid? tenantActualId)
    {
        var activo = Activo(autorizados, tenantActualId);
        return SelectorVisible(autorizados, activo) && SinEmpresaSeleccionada(autorizados, activo);
    }

    /// <summary>
    /// El Tenant activo dentro de la lista: el seleccionado si sigue autorizado
    /// (una selección caducada ya no está en la lista) y, si no, el de origen.
    /// </summary>
    public static ClienteAutorizadoDto? Activo(IReadOnlyList<ClienteAutorizadoDto> autorizados, Guid? seleccionado) =>
        autorizados.FirstOrDefault(c => c.TenantId == seleccionado)
        ?? autorizados.FirstOrDefault(c => c.EsOrigen);

    /// <summary>
    /// Decisión 5 del propietario del producto (2026-09-26): si la cartera tiene
    /// exactamente un Tenant beneficiario externo y el Tenant de origen no está
    /// gestionado, ese Tenant es el activo por defecto. Se exige además que no
    /// haya ningún otro Tenant externo alcanzable (p. ej. una delegación de
    /// soporte): con dos, elegir uno sería decidir por el usuario.
    ///
    /// <para>
    /// Decisión 7 quater (2026-09-29, opción A): solo cuando el usuario actúa
    /// como Gestor CAE. Desde la decisión Chris 2026-10-20 ese rol es su perfil
    /// de origen, no el guardado en la cartera. Con otro perfil (Dirección CAE,
    /// Coordinador CAE, Consulta, Administrador) el usuario entra en su Tenant de
    /// origen y ve el selector: nadie queda dentro de un Tenant externo sin forma
    /// de volver.
    /// </para>
    /// </summary>
    public static ClienteAutorizadoDto? TenantPorDefecto(IReadOnlyList<ClienteAutorizadoDto> autorizados)
    {
        var origen = autorizados.FirstOrDefault(c => c.EsOrigen);
        if (origen is null || origen.EsGestionadoPorOperacion) return null;

        return autorizados.Where(c => !c.EsOrigen).ToList() is [{ EsGestionadoPorOperacion: true, EsCarteraGestorCae: true } unico]
            ? unico
            : null;
    }
}

/// <remarks>
/// Con <see cref="MemoClientesAutorizados"/> y <see cref="ITenantActual"/> (la composición real los registra) la
/// lista real se lee una vez por ámbito y contexto (<see cref="ClaveContextoMemo"/>) dentro de su
/// caducidad; sin ellos (tests que construyen el handler a mano) se lee en cada llamada, como antes.
/// </remarks>
public class ObtenerClientesAutorizadosQueryHandler(
    ITenantsQueryContext dbContext, IOperacionesQueryContext operaciones,
    ICurrentUserService currentUserService, IVistaDemoActual? vistaDemo = null,
    ITenantActual? tenantActual = null, MemoClientesAutorizados? memo = null)
    : IRequestHandler<ObtenerClientesAutorizadosQuery, IReadOnlyList<ClienteAutorizadoDto>>
{
    public async Task<IReadOnlyList<ClienteAutorizadoDto>> Handle(
        ObtenerClientesAutorizadosQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        if (usuarioId is null || tenantOrigenId is null)
            return [];

        // Decisión Chris 2026-10-20: el perfil de sesión en origen rige en todos los Tenants
        // delegados. Administrador, Dirección CAE, Coordinador CAE y Consulta alcanzan los Tenants
        // de las Asignaciones de Operación de su organización; el Gestor CAE, los de su cartera.
        var rolDeOrigen = await currentUserService.ObtenerPerfilQueDecideAlcanceAsync();

        var reales = memo is not null && tenantActual is not null && !request.LecturaVigente
            ? await memo.ObtenerOCalcularAsync(
                new ClaveContextoMemo(
                    usuarioId.Value, tenantOrigenId.Value, rolDeOrigen, tenantActual.TenantId,
                    AmbitoTenantExplicito.TenantIdActual),
                ct => CalcularAsync(usuarioId.Value, tenantOrigenId.Value, rolDeOrigen, ct),
                cancellationToken)
            : await CalcularAsync(usuarioId.Value, tenantOrigenId.Value, rolDeOrigen, cancellationToken);

        // Copia propia en cada llamada: la lente quita entradas y quien llama recibe una lista suya, nunca la
        // memoizada.
        var resultado = reales.ToList();
        var acotados = vistaDemo is null ? null : await vistaDemo.ObtenerTenantIdsAcotadosAsync(cancellationToken);

        // Decisión O-2 (b) de Chris (2026-11-17): el selector de papel y los lectores del Tenant activo no miran el rol,
        // sino si la persona llega al Tenant por una Asignación de Operación; lo deciden los mismos predicados, con el
        // mismo perfil de origen, que aplica el POST /cuenta/cliente-activo (sin ninguna, nada cambia). Con la lente de
        // demo Gestor no se aplica: el perfil que decide sigue siendo el de la cuenta real, y sus Asignaciones de
        // Operación pintarían papeles que el Gestor CAE simulado no tiene; la lista queda por Tenant, como antes.
        if (acotados is null && request.PorPapel)
            resultado = await PorPapelAsync(resultado, usuarioId.Value, tenantOrigenId.Value, rolDeOrigen, cancellationToken);
        else if (acotados is null
                 && request.ParaTenantActivo
                 && tenantActual?.TenantId is { } activoId
                 && resultado.All(c => c.TenantId != activoId)
                 && await TenantActivoSoloInboundAsync(
                     activoId, usuarioId.Value, tenantOrigenId.Value, rolDeOrigen, cancellationToken) is { } soloInbound)
            resultado.Add(soloInbound);

        // Lente de demo Gestor: la lista multi-Tenant de ESE Gestor CAE son los Tenants donde tiene
        // cartera vigente (más el propio, que siempre está autorizado sobre sí mismo). Solo QUITA
        // entradas de la lista real — nunca añade un Tenant que la cuenta no alcanzara ya —, y
        // el endpoint de cambio de Tenant activo sigue autorizando contra las carteras reales.
        if (acotados is not null)
            resultado.RemoveAll(c => !c.EsOrigen && !acotados.Contains(c.TenantId));

        return resultado;
    }

    /// <summary>
    /// Una entrada por Asignación de Operación que autoriza a esta persona (D-4; O-2 (b): sea cual sea su perfil de
    /// origen): la de Outbound es la entrada por Operación de siempre (<see cref="ClienteAutorizadoDto.EsGestionadoPorOperacion"/>,
    /// el predicado <see cref="TenantsBeneficiariosAutorizados.OperacionesQueAutorizan"/> que aplica el POST sin servicio o
    /// con Outbound) con su servicio; la de Inbound sale de <see cref="TenantsBeneficiariosAutorizados.OperacionesInboundQueAutorizan"/>,
    /// el predicado que aplica el POST con <c>servicio=Inbound</c>, y existe aunque el Tenant no tenga entrada de Outbound.
    /// Una entrada Inbound es de cartera de Gestor CAE (<see cref="ClienteAutorizadoDto.EsCarteraGestorCae"/>) solo con
    /// ese perfil, como la de Outbound. El origen y la vía heredada quedan como estaban, sin servicio. Se lee siempre de
    /// la base (sin memo): la clave del memo no distingue esta forma.
    /// </summary>
    private async Task<List<ClienteAutorizadoDto>> PorPapelAsync(
        List<ClienteAutorizadoDto> porTenant, Guid usuarioId, Guid tenantOrigenId, string? rolDeOrigen,
        CancellationToken cancellationToken)
    {
        var ahora = DateTime.UtcNow;
        var esCarteraGestorCae = rolDeOrigen == TenantsBeneficiariosAutorizados.RolAcotadoPorCartera;
        var inboundIds = (await TenantsBeneficiariosAutorizados
            .OperacionesInboundQueAutorizan(operaciones, usuarioId, tenantOrigenId, rolDeOrigen, ahora)
            .Select(o => o.PropietarioTenantId)
            .ToListAsync(cancellationToken))
            .Where(id => id != tenantOrigenId)
            .ToHashSet();

        var conocidos = porTenant.ToDictionary(c => c.TenantId);
        var faltan = inboundIds.Where(id => !conocidos.ContainsKey(id)).ToList();
        var nombresInbound = faltan.Count == 0
            ? []
            : await dbContext.Tenants
                .Where(t => faltan.Contains(t.Id))
                .Select(t => new { t.Id, t.Nombre, t.LogoVersion })
                .ToListAsync(cancellationToken);

        var externos = porTenant
            .Where(c => !c.EsOrigen && c.EsGestionadoPorOperacion)
            .Select(c => c with { Servicio = ServicioCae.Outbound })
            .Concat(porTenant.Where(c => !c.EsOrigen && !c.EsGestionadoPorOperacion))
            .Concat(inboundIds
                .Select(id => conocidos.TryGetValue(id, out var c)
                    ? new ClienteAutorizadoDto(id, c.Nombre, EsOrigen: false, EsGestionadoPorOperacion: true,
                        LogoVersion: c.LogoVersion, EsCarteraGestorCae: esCarteraGestorCae)
                    : nombresInbound.Where(t => t.Id == id)
                        .Select(t => new ClienteAutorizadoDto(t.Id, t.Nombre, EsOrigen: false, EsGestionadoPorOperacion: true,
                            LogoVersion: t.LogoVersion, EsCarteraGestorCae: esCarteraGestorCae))
                        .FirstOrDefault())
                .OfType<ClienteAutorizadoDto>()
                .Select(c => c with { Servicio = ServicioCae.Inbound }))
            .OrderBy(c => c.Nombre)
            .ThenBy(c => c.TenantId)
            .ThenBy(c => c.Servicio is null ? 0 : c.Servicio == ServicioCae.Outbound ? 1 : 2);

        return [.. porTenant.Where(c => c.EsOrigen), .. externos];
    }

    /// <summary>
    /// La entrada del Tenant activo que la persona alcanza solo por Inbound (<see cref="ObtenerClientesAutorizadosQuery.ParaTenantActivo"/>),
    /// o <c>null</c>: exige el papel Inbound resuelto por el servidor y el mismo predicado, con el mismo perfil de origen,
    /// que el POST con <c>servicio=Inbound</c> y que la entrada Inbound del selector
    /// (<see cref="TenantsBeneficiariosAutorizados.OperacionesInboundQueAutorizan"/>), sea cual sea ese perfil (O-2 (b)).
    /// Nunca el Tenant de origen. Se lee de la base (sin memo), y solo cuando el Tenant activo no está en la lista por Tenant.
    /// </summary>
    private async Task<ClienteAutorizadoDto?> TenantActivoSoloInboundAsync(
        Guid activoId, Guid usuarioId, Guid tenantOrigenId, string? rolDeOrigen, CancellationToken cancellationToken)
    {
        if (activoId == tenantOrigenId || await currentUserService.ObtenerPapelActivoAsync() != PapelActivo.Inbound)
            return null;

        var alcanzado = await TenantsBeneficiariosAutorizados
            .OperacionesInboundQueAutorizan(operaciones, usuarioId, tenantOrigenId, rolDeOrigen, DateTime.UtcNow)
            .AnyAsync(o => o.PropietarioTenantId == activoId, cancellationToken);
        if (!alcanzado) return null;

        var tenant = await dbContext.Tenants
            .Where(t => t.Id == activoId)
            .Select(t => new { t.Id, t.Nombre, t.LogoVersion })
            .FirstOrDefaultAsync(cancellationToken);
        return tenant is null
            ? null
            : new ClienteAutorizadoDto(tenant.Id, tenant.Nombre, EsOrigen: false, EsGestionadoPorOperacion: true,
                LogoVersion: tenant.LogoVersion,
                EsCarteraGestorCae: rolDeOrigen == TenantsBeneficiariosAutorizados.RolAcotadoPorCartera)
            { Servicio = ServicioCae.Inbound };
    }

    /// <summary>La lista real, sin lente de demo, leída de la base con el contexto actual.</summary>
    private async Task<IReadOnlyList<ClienteAutorizadoDto>> CalcularAsync(
        Guid usuarioId, Guid tenantOrigenId, string? rolDeOrigen, CancellationToken cancellationToken)
    {
        var ahora = DateTime.UtcNow;
        var resultado = new List<ClienteAutorizadoDto>();

        var tenantOrigen = await dbContext.Tenants
            .Where(t => t.Id == tenantOrigenId)
            .Select(t => new { t.Id, t.Nombre, t.LogoVersion })
            .FirstOrDefaultAsync(cancellationToken);

        if (tenantOrigen is not null)
            resultado.Add(new ClienteAutorizadoDto(
                tenantOrigen.Id, tenantOrigen.Nombre, EsOrigen: true,
                LogoVersion: tenantOrigen.LogoVersion,
                EsGestionadoPorOperacion: await TenantsBeneficiariosAutorizados.OrigenGestionadoAsync(
                    operaciones, usuarioId, tenantOrigenId, ahora, cancellationToken)));

        var porOperacion = (await TenantsBeneficiariosAutorizados
            .OperacionesQueAutorizan(operaciones, dbContext, usuarioId, tenantOrigenId, rolDeOrigen, ahora)
            .Select(o => o.PropietarioTenantId)
            .ToListAsync(cancellationToken)).ToHashSet();

        // Decisión 7 quater, releída con la regla de 2026-10-20: el rol en cada Tenant delegado es
        // el de origen, así que «como Gestor CAE» es todo o nada según ese perfil.
        var comoGestorCae = rolDeOrigen == ContextoOperadorCae.RolGestorCae
            ? porOperacion
            : [];

        var porDelegacion = await TenantsBeneficiariosAutorizados
            .AsignacionesHeredadasVigentes(dbContext, usuarioId, ahora)
            .Select(v => v.Concesion.TenantClienteId)
            .ToListAsync(cancellationToken);

        // Un Tenant alcanzado por varias vías o por varias asignaciones (una
        // delegación Comercial y otra de Soporte, o cartera y la delegación que
        // la incorporación escribe a la vez) es un solo contexto: una sola
        // entrada. Sin esto SelectorClienteActivo pintaba dos <option> idénticas
        // y el E2E reventaba en modo estricto (FlujoSoporteTests). Se deduplica
        // por Id tras materializar: el conjunto es el de un único usuario.
        var externos = porOperacion.Concat(porDelegacion)
            .Where(id => id != tenantOrigenId)
            .ToHashSet();

        var nombres = await dbContext.Tenants
            .Where(t => externos.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre, t.LogoVersion })
            .ToListAsync(cancellationToken);

        resultado.AddRange(nombres
            .Select(t => new ClienteAutorizadoDto(t.Id, t.Nombre, EsOrigen: false,
                EsGestionadoPorOperacion: porOperacion.Contains(t.Id), LogoVersion: t.LogoVersion,
                EsCarteraGestorCae: comoGestorCae.Contains(t.Id)))
            .OrderBy(c => c.Nombre)
            .ThenBy(c => c.TenantId));

        return resultado;
    }
}
