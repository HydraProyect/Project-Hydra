using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Tenants;
using CaeManager.Application.VistaDemo;
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
public record ObtenerClientesAutorizadosQuery : IRequest<IReadOnlyList<ClienteAutorizadoDto>>;

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
    bool EsCarteraGestorCae = false);

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
        || (activo is { EsOrigen: true } && autorizados.Any(c => !c.EsOrigen));

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
    /// La usan la página de Trabajadores, su endpoint de exportación y la paleta de
    /// comandos; ninguna vuelve a escribir la condición.
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
    /// Decisión 7 quater (2026-09-29, opción A): solo cuando esa Asignación de
    /// Cartera es de rol Gestor CAE. Con otro rol (p. ej. Consulta, el Operador
    /// delegado) el usuario entra en su Tenant de origen y ve el selector: nadie
    /// queda dentro de un Tenant externo sin forma de volver.
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

public class ObtenerClientesAutorizadosQueryHandler(
    ITenantsQueryContext dbContext, IOperacionesQueryContext operaciones,
    ICurrentUserService currentUserService, IVistaDemoActual? vistaDemo = null)
    : IRequestHandler<ObtenerClientesAutorizadosQuery, IReadOnlyList<ClienteAutorizadoDto>>
{
    public async Task<IReadOnlyList<ClienteAutorizadoDto>> Handle(
        ObtenerClientesAutorizadosQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        if (usuarioId is null || tenantOrigenId is null)
            return [];

        var ahora = DateTime.UtcNow;
        var resultado = new List<ClienteAutorizadoDto>();

        var tenantOrigen = await dbContext.Tenants
            .Where(t => t.Id == tenantOrigenId.Value)
            .Select(t => new { t.Id, t.Nombre, t.LogoVersion })
            .FirstOrDefaultAsync(cancellationToken);

        if (tenantOrigen is not null)
            resultado.Add(new ClienteAutorizadoDto(
                tenantOrigen.Id, tenantOrigen.Nombre, EsOrigen: true,
                LogoVersion: tenantOrigen.LogoVersion,
                EsGestionadoPorOperacion: await TenantsBeneficiariosAutorizados.OrigenGestionadoAsync(
                    operaciones, usuarioId.Value, tenantOrigenId.Value, ahora, cancellationToken)));

        var porOperacion = (await TenantsBeneficiariosAutorizados
            .CarterasPorOperacion(operaciones, usuarioId.Value, tenantOrigenId.Value, ahora)
            .Select(v => v.Operacion.PropietarioTenantId)
            .ToListAsync(cancellationToken)).ToHashSet();

        // Decisión 7 quater: el rol se resuelve con la MISMA selección de operación y la misma
        // precedencia de carteras que el rol efectivo (RolPorOperacionAsync), no con «existe alguna
        // cartera de ese rol»: con una universal Consulta y una parcial GestorCae manda la universal.
        var comoGestorCae = new HashSet<Guid>();
        foreach (var tenantId in porOperacion)
        {
            if (await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                    operaciones, usuarioId.Value, tenantOrigenId.Value, tenantId, ahora, cancellationToken)
                is not { } operacionId)
                continue;
            if (await TenantsBeneficiariosAutorizados.RolPorOperacionAsync(
                    operaciones, usuarioId.Value, tenantOrigenId.Value, tenantId, operacionId, ahora, cancellationToken)
                == ContextoOperadorCae.RolGestorCae)
                comoGestorCae.Add(tenantId);
        }

        var porDelegacion = await TenantsBeneficiariosAutorizados
            .AsignacionesHeredadasVigentes(dbContext, usuarioId.Value, ahora)
            .Select(v => v.Concesion.TenantClienteId)
            .ToListAsync(cancellationToken);

        // Un Tenant alcanzado por varias vías o por varias asignaciones (una
        // delegación Comercial y otra de Soporte, o cartera y la delegación que
        // la incorporación escribe a la vez) es un solo contexto: una sola
        // entrada. Sin esto SelectorClienteActivo pintaba dos <option> idénticas
        // y el E2E reventaba en modo estricto (FlujoSoporteTests). Se deduplica
        // por Id tras materializar: el conjunto es el de un único usuario.
        var externos = porOperacion.Concat(porDelegacion)
            .Where(id => id != tenantOrigenId.Value)
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

        // Lente de demo Gestor: la lista multi-Tenant de ESE Gestor CAE son los Tenants donde tiene
        // cartera vigente (más el propio, que siempre está autorizado sobre sí mismo). Solo QUITA
        // entradas de la lista real — nunca añade un Tenant que la cuenta no alcanzara ya —, y
        // el endpoint de cambio de Tenant activo sigue autorizando contra las carteras reales.
        if (vistaDemo is not null && await vistaDemo.ObtenerTenantIdsAcotadosAsync(cancellationToken) is { } acotados)
            resultado.RemoveAll(c => !c.EsOrigen && !acotados.Contains(c.TenantId));

        return resultado;
    }
}
