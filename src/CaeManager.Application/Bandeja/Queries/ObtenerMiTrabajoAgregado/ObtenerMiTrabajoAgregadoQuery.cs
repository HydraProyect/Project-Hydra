using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
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
/// <c>IncluirSubidas: true</c>), sin ningún <see cref="IMediator.Send"/>
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
public record MiTrabajoTenantDto(
    Guid TenantId,
    string TenantNombre,
    bool EsOrigen,
    BandejaAgrupadaDto BloqueoActuacion,
    IReadOnlyList<ItemBandejaDto> Proximos,
    IReadOnlyList<ItemBandejaDto> Seguimiento,
    ResumenMiTrabajoTenantDto Resumen);

/// <param name="Tenants">
/// En el mismo orden que <see cref="ObtenerClientesAutorizadosQuery"/> — el
/// tenant de origen del Operador CAE primero (<c>EsOrigen</c>), luego el
/// resto por nombre. La pantalla decide si lo separa visualmente (contrato
/// § 10, "Mi organización" vs "Mi cartera Outbound") — esta Query no filtra
/// ni reordena esa distinción, solo la transporta en <c>EsOrigen</c>.
/// </param>
public record MiTrabajoAgregadoDto(IReadOnlyList<MiTrabajoTenantDto> Tenants);

public class ObtenerMiTrabajoAgregadoQueryHandler(
    IMediator mediator, IConfiguracionQueryContext configuracionContext, IEmpresasQueryContext empresasContext)
    : IRequestHandler<ObtenerMiTrabajoAgregadoQuery, MiTrabajoAgregadoDto>
{
    public async Task<MiTrabajoAgregadoDto> Handle(ObtenerMiTrabajoAgregadoQuery request, CancellationToken cancellationToken)
    {
        var tenants = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var resultado = new List<MiTrabajoTenantDto>();
        foreach (var tenant in tenants)
        {
            // Sellado por Tenant, un Tenant cada vez — nunca una query con el
            // filtro global quitado (contrato § 9). El resultado de cada
            // vuelta ya está autorizado antes de pasar a la siguiente.
            // ParametroSistema también es una fila por Tenant (mismo criterio
            // que ObtenerBandejaGestorQueryHandler, pero AHÍ el Tenant ya lo
            // sella la petición HTTP de fuera; aquí lo sella este bucle, así
            // que el propio SingleAsync tiene que caer DENTRO del ámbito, o
            // ve cero filas — RLS no deja pasar la fila de ningún Tenant sin
            // AmbitoTenantExplicito activo).
            using (AmbitoTenantExplicito.Establecer(tenant.TenantId))
            {
                resultado.Add(await ConstruirTenantAsync(tenant, hoy, cancellationToken));
            }
        }

        return new MiTrabajoAgregadoDto(resultado);
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
        var pendientesPlataforma = await mediator.Send(
            new ObtenerAcreditacionesPorProveedorQuery(IncluirSubidas: true), cancellationToken);

        // Sin cambios respecto a /bandeja: Fusionar ya ignora por sí mismo
        // EstadoDocumento.Proximo y EstadoAcreditacion.Subida (ver su
        // propio comentario) — pasarle alertas/pendientesPlataforma con esas
        // filas incluidas es seguro, no las cuela en Bloqueo/Actuación.
        var fusionados = ObtenerBandejaGestorQueryHandler.Fusionar(
            alertas, revisiones, requisitos, visitasUrgentes.Elementos, sugerenciasVisita, detecciones, pendientesPlataforma,
            hoy, parametros.HorasAvisoVisita, parametros.HorasCriticasVisita);
        var proximosSinEmpresa = MapearProximos(alertas);
        var seguimientoSinEmpresa = MapearSeguimiento(pendientesPlataforma);

        var empresas = await CargarEmpresasSujetoAsync(
            fusionados.Concat(proximosSinEmpresa).Concat(seguimientoSinEmpresa), cancellationToken);
        var bloqueoActuacionItems = MarcarEmpresaSujeto(fusionados, empresas);
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
            tenant.TenantId, tenant.Nombre, tenant.EsOrigen, bloqueoActuacion, proximos, seguimiento, resumen);
    }

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
    /// Mismo criterio que <c>TipoItemBandejaUi.Tono == TonoBadge.Peligro</c>
    /// (CaeManager.Web) — duplicado aquí porque Application no puede
    /// referenciar Web. Recibe el ítem completo, no solo el tipo, por el
    /// mismo motivo que esa función: un RequisitoPendiente con EsAltaNueva no
    /// es un bloqueo (contrato § 5, D-4/D-6/D-7).
    /// </summary>
    public static bool EsBloqueo(ItemBandejaDto item) => item.Tipo switch
    {
        TipoItemBandeja.SugerenciaVisitaUrgente => true,
        TipoItemBandeja.Faltante => true,
        TipoItemBandeja.Vencido => true,
        TipoItemBandeja.PlataformaRechazada => true,
        TipoItemBandeja.RequisitoPendiente => !item.EsAltaNueva,
        _ => false
    };

    /// <summary>Mismo mapeo de campos que la rama "alertas" de <see cref="ObtenerBandejaGestorQueryHandler.Fusionar"/>, solo que aquí SÍ se queda con Proximo en vez de descartarlo.</summary>
    public static List<ItemBandejaDto> MapearProximos(IReadOnlyList<AlertaDto> alertas) => alertas
        .Where(a => a.Estado == EstadoDocumento.Proximo)
        .Select(a => new ItemBandejaDto(
            Id: $"proximo-{a.DocumentoId?.ToString() ?? $"{a.TrabajadorId}-{a.TipoDocumentoId}"}",
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
                Id: $"seguimiento-{d.AcreditacionId}",
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
                ProveedorNombre: proveedor.ProveedorNombre))))
        .ToList();
}
