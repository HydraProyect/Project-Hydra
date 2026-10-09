using CaeManager.Domain.Common;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.Empresas;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPendientes;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;

/// <summary>
/// Nivel 1 del Gestor CAE externo — «Mi trabajo» agregada entre todos los
/// Tenants propietarios de su cartera
/// (Project-Hydra-Negocio/tecnico/CONTRATO-MI-TRABAJO-GEN2-MULTI-TENANT-2026-09-22.md,
/// D-9 RESUELTA). Mismo patrón de fan-out por
/// <see cref="AmbitoTenantExplicito"/> sobre <see cref="ObtenerClientesAutorizadosQuery"/>
/// que ya usa <see cref="Dashboard.Queries.ObtenerKpisGlobalesQueryHandler"/> —
/// nunca un filtro global de tenant ni <c>IgnoreQueryFilters</c>: cada Tenant
/// se consulta con su RLS normal, sellado por
/// <see cref="AmbitoTenantExplicito"/>, y el resultado ya autorizado se
/// fusiona en memoria (contrato § 9).
///
/// <para>
/// Reutiliza <see cref="ObtenerBandejaGestorQueryHandler.Fusionar"/> y
/// <see cref="ObtenerBandejaAgrupadaQueryHandler.Agrupar"/> TAL CUAL —cero
/// cambios— para Bloqueo+Actuación: es la misma cola que ya ve
/// <c>/bandeja</c> (Nivel 0) por Tenant, con la misma clasificación y el
/// mismo agrupado por Cliente empresarial (contrato § 1, Nivel B). El
/// contrato exige además dos buckets que Nivel 0 excluye a propósito
/// (<see cref="TipoItemBandeja.VencimientoProximo"/>,
/// <see cref="TipoItemBandeja.EnPlataformaSeguimiento"/> — ver sus
/// comentarios) — se extraen de los MISMOS resultados que ya trae
/// <see cref="ObtenerAlertasQuery"/> y
/// <see cref="ObtenerAcreditacionesPorProveedorQuery"/> (esta última con
/// <c>IncluirSubidas: true</c> e <c>IncluirVencidasEnPlataforma: true</c>, que
/// alimenta el bloqueo <see cref="TipoItemBandeja.PlataformaVencida"/> de la
/// decisión P12), sin ningún <see cref="IMediator.Send"/>
/// adicional: por Tenant es el mismo número de Send que ya hace
/// <see cref="ObtenerBandejaGestorQueryHandler"/> hoy.
/// </para>
///
/// <para>
/// Sí añade una consulta directa por Tenant, a <see cref="IEmpresasQueryContext"/>:
/// la que rellena <see cref="ItemBandejaDto.EmpresaEsPropia"/> de las tareas
/// cuyo sujeto es una Empresa (contrato § 14: «Documentación de empresa» o
/// «Subcontrata · nombre»). Va dentro del mismo <see cref="AmbitoTenantExplicito"/>
/// que el resto, así que solo ve las Empresas de ese Tenant.
/// </para>
///
/// <para>
/// Y, solo cuando la cola del Tenant trae alguna acreditación Rechazada, el
/// cálculo de estado de sus Centros de Trabajo
/// (<see cref="ICalculoEstadoCentroService.CalcularAsync"/>, vía
/// <see cref="ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloqueanAsync"/>):
/// D-7 del piloto Outbound, una Rechazada bloquea solo si es aplicable a su
/// Centro, y el mismo criterio que usa la cola agrupada de /bandeja e Inicio
/// decide aquí si cuenta como Bloqueo. También corre dentro del
/// <see cref="AmbitoTenantExplicito"/> del Tenant propietario de la cola.
/// </para>
/// </summary>
public record ObtenerMiTrabajoAgregadoQuery : IRequest<MiTrabajoAgregadoDto>;

/// <summary>
/// Forma "barata" del contrato § 7.1 — recuentos por Tenant, sin las filas de
/// trabajo. La taxonomía es la de la propia pantalla Gen2 (mockup
/// "Mi trabajo.dc.html", <c>sevMeta()</c>: bloqueo / actuación / próximo /
/// seguimiento), no el texto ilustrativo del contrato ("Bloqueos, Vencidos,
/// Proximos, Pendientes") — mismo criterio que el resto de este incremento:
/// el mockup manda sobre la prosa ilustrativa cuando difieren en el detalle,
/// nunca en la semántica (TenantId explícito, Nivel A/B nunca fusionados).
/// </summary>
public record ResumenMiTrabajoTenantDto(
    Guid TenantId,
    string TenantNombre,
    bool EsOrigen,
    int TotalAcciones,
    int Bloqueos,
    int Actuaciones,
    int Proximos,
    int Seguimiento);

/// <summary>
/// Forma "top-N" del contrato § 7.2, por Tenant. <see cref="BloqueoActuacion"/>
/// ya viene agrupada por Cliente empresarial (mismo <see cref="GrupoColaDto"/>
/// que renderiza <c>/bandeja</c> hoy — reutilizable tal cual con el
/// componente <c>GrupoCola</c>); <see cref="Proximos"/> y
/// <see cref="Seguimiento"/> son listas planas: no tienen hoy un
/// agrupador natural distinto del que ya da <see cref="ItemBandejaDto.ClienteId"/>,
/// y forzarlas por <see cref="ObtenerBandejaAgrupadaQueryHandler.Agrupar"/>
/// solo para dos categorías que en el mockup se muestran sin subcabeceras de
/// Cliente habría sido una abstracción sin uso real.
/// </summary>
/// <param name="AlcanceCero">
/// Quien mira no alcanza nada en este Tenant: sin acceso total y con la
/// cartera de Clientes empresariales vacía (<see cref="IAlcanceDatosService"/>,
/// resuelto dentro del mismo <see cref="AmbitoTenantExplicito"/> que la cola).
/// Es el caso de un Gestor CAE sin ninguna Asignación de Cartera vigente en el
/// Tenant, o del Administrador de un Operador CAE externo que ve el Tenant
/// delegado sin cartera en él. Con él, la pantalla distingue «no hay nada que
/// vigilar» de «lo vigilado está al día» (P2.3 de la demo a Dirección): una
/// cola vacía con alcance cero no es una cartera al día. Solo describe el
/// alcance ya aplicado; no filtra ni autoriza nada.
/// </param>
public record MiTrabajoTenantDto(
    Guid TenantId,
    string TenantNombre,
    bool EsOrigen,
    BandejaAgrupadaDto BloqueoActuacion,
    IReadOnlyList<ItemBandejaDto> Proximos,
    IReadOnlyList<ItemBandejaDto> Seguimiento,
    ResumenMiTrabajoTenantDto Resumen,
    bool AlcanceCero);

/// <param name="Tenants">
/// En el mismo orden que <see cref="ObtenerClientesAutorizadosQuery"/> — el
/// tenant de origen del Operador CAE primero (<c>EsOrigen</c>), luego el
/// resto por nombre. La pantalla decide si lo separa visualmente (contrato
/// § 10, "Mi organización" vs "Mi cartera Outbound") — esta Query no filtra
/// ni reordena esa distinción, solo la transporta en <c>EsOrigen</c>.
/// </param>
/// <param name="TenantsNoConsultados">
/// Tenants propietarios cuya cola no se pudo construir (FS-07): la consulta de
/// uno falló y los demás se devuelven igual, en vez de dejar sin cola a toda
/// la cartera. Nunca se mezclan con <paramref name="Tenants"/>: un Tenant no
/// consultado no es un Tenant al día. <c>null</c> equivale a vacío.
/// </param>
public record MiTrabajoAgregadoDto(
    IReadOnlyList<MiTrabajoTenantDto> Tenants,
    IReadOnlyList<TenantNoConsultadoDto>? TenantsNoConsultados = null)
{
    public IReadOnlyList<TenantNoConsultadoDto> NoConsultados => TenantsNoConsultados ?? [];
}

/// <summary>Un Tenant propietario de la cartera cuya cola no se pudo consultar. Solo lo nombra: no trae ningún dato suyo.</summary>
public record TenantNoConsultadoDto(Guid TenantId, string TenantNombre, bool EsOrigen);

/// <summary>
/// Mismo recorrido que <see cref="ObtenerMiTrabajoAgregadoQuery"/>, pero entregado por partes: una por Tenant
/// propietario, en cuanto su cola está construida, para que la pantalla pinte cada Empresa a medida que termina.
/// El recorrido sigue siendo secuencial y sellado por Tenant (<see cref="AmbitoTenantExplicito"/>); cada parte ya
/// viene autorizada y no se comparte entre usuarios ni se guarda. La consulta agregada no cambia.
/// </summary>
public record ObtenerMiTrabajoPorPartesQuery : IStreamRequest<ParteMiTrabajoDto>;

/// <summary>
/// Una parte del recorrido: la cola de un Tenant propietario o su aviso de «no consultado» (nunca las dos). La primera
/// parte de cada recorrido es de apertura —<see cref="Completados"/> en 0, sin Tenant ni aviso— y solo informa de
/// cuántos Tenants se van a consultar.
/// </summary>
/// <param name="Total">Tenants propietarios que va a consultar el recorrido (los de <see cref="ObtenerClientesAutorizadosQuery"/>).</param>
/// <param name="Completados">Tenants ya resueltos, contando éste: consultado o no consultado.</param>
public record ParteMiTrabajoDto(
    int Total, int Completados, MiTrabajoTenantDto? Tenant = null, TenantNoConsultadoDto? NoConsultado = null);

public class ObtenerMiTrabajoAgregadoQueryHandler(
    IMediator mediator, IConfiguracionQueryContext configuracionContext, IEmpresasQueryContext empresasContext,
    ICalculoEstadoCentroService calculoEstadoCentro, IAlcanceDatosService alcanceDatos,
    ILogger<ObtenerMiTrabajoAgregadoQueryHandler> logger, PuertaAccesoDatos? puerta = null)
    : IRequestHandler<ObtenerMiTrabajoAgregadoQuery, MiTrabajoAgregadoDto>,
      IStreamRequestHandler<ObtenerMiTrabajoPorPartesQuery, ParteMiTrabajoDto>
{
    public async Task<MiTrabajoAgregadoDto> Handle(ObtenerMiTrabajoAgregadoQuery request, CancellationToken cancellationToken)
    {
        // La consulta agregada ya corre dentro de la puerta de acceso a datos (SerializacionAccesoDatosBehavior).
        var resultado = new List<MiTrabajoTenantDto>();
        var noConsultados = new List<TenantNoConsultadoDto>();
        await foreach (var parte in RecorrerAsync(serializarPorTenant: false, cancellationToken))
        {
            if (parte.Tenant is not null) resultado.Add(parte.Tenant);
            if (parte.NoConsultado is not null) noConsultados.Add(parte.NoConsultado);
        }

        return new MiTrabajoAgregadoDto(resultado, noConsultados);
    }

    /// <summary>
    /// Un flujo no pasa por el pipeline de MediatR, así que la puerta de acceso a datos (que serializa el DbContext del
    /// circuito) se toma aquí: una vez por Tenant, y se suelta antes de entregar la parte, para no retenerla mientras
    /// la pantalla se pinta ni dejar ningún <see cref="AmbitoTenantExplicito"/> abierto al ceder el control.
    /// </summary>
    public IAsyncEnumerable<ParteMiTrabajoDto> Handle(ObtenerMiTrabajoPorPartesQuery request, CancellationToken cancellationToken)
    {
        if (puerta is null)
            throw new InvalidOperationException("Mi trabajo por partes necesita la puerta de acceso a datos del circuito.");
        return RecorrerAsync(serializarPorTenant: true, cancellationToken);
    }

    private async IAsyncEnumerable<ParteMiTrabajoDto> RecorrerAsync(
        bool serializarPorTenant, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var tenants = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        yield return new ParteMiTrabajoDto(tenants.Count, 0);
        var completados = 0;
        foreach (var tenant in tenants)
        {
            // Secuencial, un Tenant cada vez. El sellado vive dentro de ConsultarTenantAsync y se cierra antes de cada
            // yield: ningún ámbito queda abierto mientras el consumidor tiene el control.
            var parte = serializarPorTenant
                ? await puerta!.EjecutarAsync(() => ConsultarTenantAsync(tenant, hoy, cancellationToken), cancellationToken)
                : await ConsultarTenantAsync(tenant, hoy, cancellationToken);
            completados++;
            yield return parte with { Total = tenants.Count, Completados = completados };
        }
    }

    private async Task<ParteMiTrabajoDto> ConsultarTenantAsync(
        ClienteAutorizadoDto tenant, DateOnly hoy, CancellationToken cancellationToken)
    {
        // FS-07: el fallo de un Tenant no se lleva por delante la cola de
        // los demás. Se degrada por Tenant —su cola no aparece y la
        // pantalla lo nombra— en vez de abortar la consulta entera, que
        // dejaba al Gestor CAE sin nada y con un «Reintentar» inútil si el
        // fallo era determinista. Cancelar sí aborta: no es un fallo del
        // Tenant, es que ya nadie espera la respuesta.
        // Sellado por Tenant, un Tenant cada vez — nunca una query con el
        // filtro global quitado (contrato § 9). El resultado de cada
        // vuelta ya está autorizado antes de pasar a la siguiente.
        // ParametroSistema también es una fila por Tenant (mismo criterio
        // que ObtenerBandejaGestorQueryHandler, pero AHÍ el Tenant ya lo
        // sella la petición HTTP de fuera; aquí lo sella este método, así
        // que el propio SingleAsync tiene que caer DENTRO del ámbito, o
        // ve cero filas — RLS no deja pasar la fila de ningún Tenant sin
        // AmbitoTenantExplicito activo).
        try
        {
            using (AmbitoTenantExplicito.Establecer(tenant.TenantId))
            {
                return new ParteMiTrabajoDto(0, 0, await ConstruirTenantAsync(tenant, hoy, cancellationToken));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Mi trabajo: no se pudo construir la cola del Tenant {TenantId}; se devuelve el resto de la cartera.", tenant.TenantId);
            return new ParteMiTrabajoDto(0, 0, NoConsultado: new TenantNoConsultadoDto(tenant.TenantId, tenant.Nombre, tenant.EsOrigen));
        }
    }

    private async Task<MiTrabajoTenantDto> ConstruirTenantAsync(
        ClienteAutorizadoDto tenant, DateOnly hoy, CancellationToken cancellationToken)
    {
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var alertas = await mediator.Send(new ObtenerAlertasQuery(), cancellationToken);
        var revisiones = await mediator.Send(new ObtenerRevisionesIaPendientesQuery(), cancellationToken);
        var requisitos = await mediator.Send(new ObtenerDocumentacionBloqueantePendienteQuery(), cancellationToken);
        var visitasUrgentes = await mediator.Send(
            new ObtenerVisitasQuery(Busqueda: null, SoloActivas: true, NotificadoCliente: null, SoloUrgentes: true, TamanoPagina: 200),
            cancellationToken);
        var sugerenciasVisita = await mediator.Send(new ObtenerSugerenciasVisitaCorreoPendientesQuery(), cancellationToken);
        var detecciones = await mediator.Send(new ObtenerDeteccionesPendientesQuery(), cancellationToken);
        // IncluirSubidas: true — a diferencia de /bandeja (Nivel 0), Mi trabajo
        // Gen2 SÍ necesita las acreditaciones ya subidas para el bucket
        // "Seguimiento" (contrato § 5/§ 7). PendienteDeSubir/Rechazada se
        // comportan exactamente igual que hoy.
        // IncluirVencidasEnPlataforma: true — las aceptadas cuya vigencia en la
        // plataforma ya venció (P12), que Fusionar tampoco toca.
        var pendientesPlataforma = await mediator.Send(
            new ObtenerAcreditacionesPorProveedorQuery(IncluirSubidas: true, IncluirVencidasEnPlataforma: true), cancellationToken);

        // Sin cambios respecto a /bandeja: Fusionar ya ignora por sí mismo
        // EstadoDocumento.Proximo y EstadoAcreditacion.Subida/Aceptada (ver su
        // propio comentario) — pasarle alertas/pendientesPlataforma con esas
        // filas incluidas es seguro, no las cuela en Bloqueo/Actuación. Las
        // vencidas en plataforma entran después. El orden se fija más abajo,
        // una sola vez, cuando ya se sabe qué Rechazada cierra su Centro.
        var fusionados = ObtenerBandejaGestorQueryHandler.Fusionar(
                alertas, revisiones, requisitos, visitasUrgentes.Elementos, sugerenciasVisita, detecciones, pendientesPlataforma,
                hoy, parametros.HorasAvisoVisita, parametros.HorasCriticasVisita)
            .Concat(MapearVencidasEnPlataforma(pendientesPlataforma, alertas))
            .ToList();
        var proximosSinEmpresa = MapearProximos(alertas);
        var seguimientoSinEmpresa = MapearSeguimiento(pendientesPlataforma);

        // Dos filas con el mismo Id son dos @key hermanas: la vista numera las repeticiones para que el circuito no
        // muera (ClavesDeHermanos, en la Web), pero el productor que las emite tiene un defecto que alguien debe ver.
        var idsDuplicados = IdDeFilaDeCola.Duplicados(fusionados.Concat(proximosSinEmpresa).Concat(seguimientoSinEmpresa), i => i.Id);
        if (idsDuplicados.Count > 0)
        {
            logger.LogWarning(
                "Mi trabajo: la cola del Tenant {TenantId} trae {Cantidad} Id de fila repetidos (p. ej. {Ejemplos}); la pantalla los numera, pero un productor de filas de la cola emite filas hermanas con el mismo Id.",
                tenant.TenantId, idsDuplicados.Count, string.Join(", ", idsDuplicados.Keys.Take(3)));
        }

        var empresas = await CargarEmpresasSujetoAsync(
            fusionados.Concat(proximosSinEmpresa).Concat(seguimientoSinEmpresa), cancellationToken);
        // D-7: qué Rechazada cierra de verdad su Centro de Trabajo lo decide
        // el cálculo de estado del Centro, sobre los datos de ESTE Tenant
        // (seguimos dentro de su AmbitoTenantExplicito). Sin esto, EsBloqueo
        // no tendría RechazoBloqueaCentro que leer, ni el orden de la cola
        // —el mismo de /bandeja, que pone primero lo que bloquea el acceso—
        // sabría qué Rechazada va delante: por eso se ordena después de marcar.
        var bloqueoActuacionItems = ObtenerBandejaGestorQueryHandler.Ordenar(
            await ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloqueanAsync(
                MarcarEmpresaSujeto(fusionados, empresas), calculoEstadoCentro, cancellationToken));
        var proximos = MarcarEmpresaSujeto(proximosSinEmpresa, empresas);
        var seguimiento = MarcarEmpresaSujeto(seguimientoSinEmpresa, empresas);
        var bloqueoActuacion = ObtenerBandejaAgrupadaQueryHandler.Agrupar(bloqueoActuacionItems);

        var bloqueos = bloqueoActuacionItems.Count(EsBloqueo);
        var resumen = new ResumenMiTrabajoTenantDto(
            tenant.TenantId, tenant.Nombre, tenant.EsOrigen,
            TotalAcciones: bloqueoActuacionItems.Count + proximos.Count + seguimiento.Count,
            Bloqueos: bloqueos,
            Actuaciones: bloqueoActuacionItems.Count - bloqueos,
            Proximos: proximos.Count,
            Seguimiento: seguimiento.Count);

        return new MiTrabajoTenantDto(
            tenant.TenantId, tenant.Nombre, tenant.EsOrigen, bloqueoActuacion, proximos, seguimiento, resumen,
            AlcanceCero: await EsAlcanceCeroAsync(alcanceDatos, cancellationToken));
    }

    /// <summary>
    /// Alcance cero en el Tenant vigente (el del <see cref="AmbitoTenantExplicito"/>
    /// de la vuelta): sin acceso total y sin ningún Cliente empresarial en
    /// cartera. Con la cartera de Clientes vacía, <see cref="IAlcanceDatosService"/>
    /// deja vacíos también los alcances derivados (Centros, Empresas —incluida la
    /// propia—, Trabajadores), así que no queda nada que la cola pudiera enseñar.
    /// Lee el alcance, no lo cambia: cada consulta de la cola ya lo aplicó.
    /// </summary>
    public static async Task<bool> EsAlcanceCeroAsync(IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken) =>
        !await alcanceDatos.TieneAccesoTotalAsync(cancellationToken)
        && await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken) is { Count: 0 };

    /// <summary>
    /// El sujeto de la tarea es una Empresa, no una persona: documento de
    /// Empresa (revisión IA, acreditación en plataforma). DeteccionPendiente
    /// también lleva EmpresaId sin TrabajadorId, pero su sujeto es la persona
    /// detectada, así que queda fuera.
    /// </summary>
    public static bool SujetoEsEmpresa(ItemBandejaDto item) =>
        item.TrabajadorId is null && item.EmpresaId is not null && item.Tipo != TipoItemBandeja.DeteccionPendiente;

    private async Task<Dictionary<Guid, (string RazonSocial, bool EsPropia)>> CargarEmpresasSujetoAsync(
        IEnumerable<ItemBandejaDto> items, CancellationToken cancellationToken)
    {
        var ids = items.Where(SujetoEsEmpresa).Select(i => i.EmpresaId!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await empresasContext.Empresas
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => (e.RazonSocial, e.EsPropia), cancellationToken);
    }

    /// <summary>
    /// Rellena <see cref="ItemBandejaDto.EmpresaEsPropia"/> y, si falta,
    /// <see cref="ItemBandejaDto.EmpresaNombre"/> en las tareas cuyo sujeto es
    /// una Empresa. Una Empresa que no aparece (no debería: la tarea sale del
    /// mismo Tenant) deja la tarea como estaba, con null, que la pantalla
    /// pinta igual que hoy.
    /// </summary>
    public static List<ItemBandejaDto> MarcarEmpresaSujeto(
        IEnumerable<ItemBandejaDto> items, IReadOnlyDictionary<Guid, (string RazonSocial, bool EsPropia)> empresas) => items
        .Select(i => SujetoEsEmpresa(i) && empresas.TryGetValue(i.EmpresaId!.Value, out var empresa)
            ? i with { EmpresaEsPropia = empresa.EsPropia, EmpresaNombre = i.EmpresaNombre ?? empresa.RazonSocial }
            : i)
        .ToList();

    /// <summary>
    /// Severidad «Bloqueo» de Mi trabajo (contrato § 5, D-4/D-6/D-7, P12). Faltante,
    /// Vencido, la acreditación vencida en la plataforma
    /// (<see cref="TipoItemBandeja.PlataformaVencida"/>) y la sugerencia de
    /// visita urgente son siempre bloqueo. Para
    /// RequisitoPendiente y PlataformaRechazada decide
    /// <see cref="ObtenerBandejaAgrupadaQueryHandler.BloqueaElAcceso"/>
    /// —el mismo criterio que marca «bloquea acceso» en la cola agrupada—, así
    /// que Mi trabajo no puede contradecir a /bandeja ni a Centro 360: un
    /// requisito pendiente siempre bloquea (también el de un Trabajador recién
    /// dado de alta sin documentación), y una Rechazada solo bloquea si el
    /// cálculo de estado de su Centro de Trabajo la cuenta como causa
    /// bloqueante (<see cref="ItemBandejaDto.RechazoBloqueaCentro"/>). Una
    /// Rechazada no aplicable a su Centro queda en «Requiere actuación».
    ///
    /// <para>
    /// Ya no coincide con <c>TipoItemBandejaUi.Tono == TonoBadge.Peligro</c>
    /// (CaeManager.Web): el badge de una Rechazada sigue en rojo, que describe
    /// el estado de la acreditación, no si cierra el Centro.
    /// </para>
    /// </summary>
    public static bool EsBloqueo(ItemBandejaDto item) => item.Tipo switch
    {
        TipoItemBandeja.SugerenciaVisitaUrgente => true,
        TipoItemBandeja.Faltante => true,
        TipoItemBandeja.Vencido => true,
        TipoItemBandeja.PlataformaVencida => true,
        _ => ObtenerBandejaAgrupadaQueryHandler.BloqueaElAcceso(item)
    };

    /// <summary>
    /// Decisión P12 (2026-09-23): una acreditación de Plataforma CAE aceptada
    /// cuya vigencia en la plataforma ya venció entra en Mi trabajo como
    /// bloqueo de prioridad alta (<see cref="TipoItemBandeja.PlataformaVencida"/>),
    /// una por acreditación, en el Tenant propietario de la cola.
    ///
    /// <para>
    /// Solo <see cref="EstadoAcreditacion.Aceptada"/> con
    /// <see cref="EstadoVigenciaEnPlataforma.VenceEnFecha"/> y la fecha
    /// estrictamente anterior a hoy: la vigencia vale hasta esa fecha inclusive
    /// (<see cref="VigenciaEnPlataforma.EstaVencidaEl"/>), y «sin confirmar» no
    /// es estar vencida. La comparación la hace la consulta, con la misma fecha
    /// con la que elige las filas (<see cref="AcreditacionDrillDownDto.VencidaEnPlataforma"/>):
    /// aquí no se vuelve a leer el reloj, porque entre las dos lecturas podía
    /// cambiar el día y perderse una vencida. Una Rechazada o pendiente de subir
    /// ya tiene su propio ítem, y rechazar o renovar reinician la vigencia, así
    /// que no hay dos ítems para la misma acreditación. Una Subida ya reenviada
    /// sigue en Seguimiento.
    /// </para>
    ///
    /// <para>
    /// Se deduplica con el vencimiento documental: si el mismo Documento ya
    /// está <see cref="EstadoDocumento.Vencido"/> en TALVEG, la cola ya trae ese
    /// bloqueo, y la acción es renovar el documento, lo que además devuelve sus
    /// acreditaciones a pendiente de subir.
    /// </para>
    /// </summary>
    public static List<ItemBandejaDto> MapearVencidasEnPlataforma(
        IReadOnlyList<ProveedorAcreditacionesDto> acreditaciones, IReadOnlyList<AlertaDto> alertas)
    {
        var documentosVencidos = alertas
            .Where(a => a.Estado == EstadoDocumento.Vencido && a.DocumentoId is not null)
            .Select(a => a.DocumentoId!.Value)
            .ToHashSet();

        return acreditaciones
            .SelectMany(proveedor => proveedor.Clientes.SelectMany(cliente => cliente.Documentos
                .Where(d => d.Estado == EstadoAcreditacion.Aceptada
                            && d.VencidaEnPlataforma
                            && !documentosVencidos.Contains(d.DocumentoId))
                .Select(d => new ItemBandejaDto(
                    Id: IdDeFilaDeCola.PlataformaVencida(d.AcreditacionId),
                    Tipo: TipoItemBandeja.PlataformaVencida,
                    Titulo: d.TipoDocumentoNombre,
                    Subtitulo: d.PropietarioNombre,
                    Fecha: d.FechaVencimientoEnPlataforma,
                    TrabajadorId: d.TrabajadorId,
                    CentroId: d.CentroId,
                    DocumentoId: d.DocumentoId,
                    TipoDocumentoId: d.TipoDocumentoId,
                    RequisitoId: null,
                    ClienteId: cliente.ClienteId,
                    ClienteNombre: cliente.ClienteNombre,
                    EmpresaId: d.EmpresaId,
                    TrabajadorNombre: d.TrabajadorId is not null ? d.PropietarioNombre : null,
                    ProveedorNombre: proveedor.ProveedorNombre,
                    AcreditacionId: d.AcreditacionId))))
            .ToList();
    }

    /// <summary>Mismo mapeo de campos que la rama "alertas" de <see cref="ObtenerBandejaGestorQueryHandler.Fusionar"/>, solo que aquí SÍ se queda con Proximo en vez de descartarlo.</summary>
    public static List<ItemBandejaDto> MapearProximos(IReadOnlyList<AlertaDto> alertas) => alertas
        .Where(a => a.Estado == EstadoDocumento.Proximo)
        .Select(a => new ItemBandejaDto(
            Id: a.IdDeFila("proximo"),
            Tipo: TipoItemBandeja.VencimientoProximo,
            Titulo: a.TipoDocumentoNombre,
            Subtitulo: a.CentroNombre is null ? a.TrabajadorNombre : $"{a.TrabajadorNombre} — {a.CentroNombre}",
            Fecha: a.FechaVencimiento,
            TrabajadorId: a.TrabajadorId,
            CentroId: a.CentroId,
            DocumentoId: a.DocumentoId,
            TipoDocumentoId: a.TipoDocumentoId,
            RequisitoId: null,
            ClienteId: a.ClienteId,
            ClienteNombre: a.ClienteNombre,
            EmpresaId: a.EmpresaId,
            EmpresaNombre: a.EmpresaNombre,
            TrabajadorNombre: a.TrabajadorNombre))
        .ToList();

    /// <summary>Mismo mapeo de campos que la rama "plataformas" de <see cref="ObtenerBandejaGestorQueryHandler.Fusionar"/>, restringido a <see cref="EstadoAcreditacion.Subida"/> (la única que esa rama descarta).</summary>
    public static List<ItemBandejaDto> MapearSeguimiento(IReadOnlyList<ProveedorAcreditacionesDto> pendientesPlataforma) => pendientesPlataforma
        .SelectMany(proveedor => proveedor.Clientes.SelectMany(cliente => cliente.Documentos
            .Where(d => d.Estado == EstadoAcreditacion.Subida)
            .Select(d => new ItemBandejaDto(
                Id: IdDeFilaDeCola.Seguimiento(d.AcreditacionId),
                Tipo: TipoItemBandeja.EnPlataformaSeguimiento,
                Titulo: d.TipoDocumentoNombre,
                Subtitulo: d.PropietarioNombre,
                Fecha: null,
                TrabajadorId: d.TrabajadorId,
                CentroId: d.CentroId,
                DocumentoId: d.DocumentoId,
                TipoDocumentoId: d.TipoDocumentoId,
                RequisitoId: null,
                ClienteId: cliente.ClienteId,
                ClienteNombre: cliente.ClienteNombre,
                EmpresaId: d.EmpresaId,
                TrabajadorNombre: d.TrabajadorId is not null ? d.PropietarioNombre : null,
                ProveedorNombre: proveedor.ProveedorNombre,
                AcreditacionId: d.AcreditacionId))))
        .ToList();
}
