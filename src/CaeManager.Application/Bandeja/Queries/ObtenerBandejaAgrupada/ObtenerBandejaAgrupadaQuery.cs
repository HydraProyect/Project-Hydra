using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Centros;
using MediatR;

namespace CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;

/// <summary>
/// Agrupa <see cref="ObtenerBandejaGestorQuery"/> "por situación" en vez de
/// una lista plana — cierra el hallazgo P-03 de la auditoría de producto
/// 2026-08-16 ("la Bandeja no escala ni agrupa"): con cartera real, una
/// pared de tarjetas individuales sin agregación por Cliente no deja ver que
/// 12 vencidos son en realidad UN problema (un Cliente con acceso
/// bloqueado), no doce.
/// </summary>
public record ObtenerBandejaAgrupadaQuery : IRequest<BandejaAgrupadaDto>;

/// <param name="GrupoId">Identificador estable del grupo — "cliente-{id}" o "empresa-{id}" (ver <see cref="ObtenerBandejaAgrupadaQueryHandler.Agrupar"/>).</param>
/// <param name="BloqueaAcceso">
/// True si algún item del grupo es <see cref="TipoItemBandeja.RequisitoPendiente"/>
/// (también de alta nueva) o cumple <see cref="ObtenerBandejaAgrupadaQueryHandler.BloqueaAccesoAlCentro"/>
/// — esos grupos van primero (§ 11 de la ficha SCREEN 01, Parte XV de la
/// auditoría). Es la clave de orden, no el dato «bloquea acceso» que se
/// enseña: ese, sin las altas nuevas, es <c>TipoItemBandejaUi.BloqueaAccesoDeVerdad</c>.
/// </param>
public record GrupoColaDto(string GrupoId, string Titulo, bool BloqueaAcceso, IReadOnlyList<ItemBandejaDto> Items);

/// <param name="SinGrupo">
/// Items sin Cliente ni Empresa resueltos (p. ej. una sugerencia de visita
/// sin Centro detectado por la IA) — no se descartan, se muestran sueltos
/// tras los grupos, igual que hacía la lista plana antes de este cambio.
/// </param>
public record BandejaAgrupadaDto(IReadOnlyList<GrupoColaDto> Grupos, IReadOnlyList<ItemBandejaDto> SinGrupo);

public class ObtenerBandejaAgrupadaQueryHandler(IMediator mediator, ICalculoEstadoCentroService calculoEstadoCentro)
    : IRequestHandler<ObtenerBandejaAgrupadaQuery, BandejaAgrupadaDto>
{
    public async Task<BandejaAgrupadaDto> Handle(ObtenerBandejaAgrupadaQuery request, CancellationToken cancellationToken)
    {
        var items = await mediator.Send(new ObtenerBandejaGestorQuery(), cancellationToken);
        return Agrupar(await MarcarRechazosQueBloqueanAsync(items, calculoEstadoCentro, cancellationToken));
    }

    /// <summary>
    /// D-7 del piloto Outbound: una acreditación Rechazada aplicable bloquea el
    /// Centro de Trabajo. Qué es «aplicable» no se decide aquí — se pregunta al
    /// mismo cálculo que pinta el estado del Centro
    /// (<see cref="ICalculoEstadoCentroService.CalcularAsync"/>), para que el
    /// «bloquea acceso» de un grupo por un rechazo no pueda contradecir al
    /// Centro 360. Solo se calcula para los Centros que tienen alguna
    /// rechazada en la cola: sin ninguna, no hay consulta extra.
    /// </summary>
    public static async Task<IReadOnlyList<ItemBandejaDto>> MarcarRechazosQueBloqueanAsync(
        IReadOnlyList<ItemBandejaDto> items, ICalculoEstadoCentroService calculoEstadoCentro, CancellationToken cancellationToken)
    {
        var centroIds = items
            .Where(i => i.Tipo == TipoItemBandeja.PlataformaRechazada && i.CentroId is not null && i.DocumentoId is not null)
            .Select(i => i.CentroId!.Value)
            .Distinct()
            .ToList();
        if (centroIds.Count == 0) return items;

        var estados = await calculoEstadoCentro.CalcularAsync(centroIds, cancellationToken);
        return MarcarRechazosQueBloquean(items, estados);
    }

    /// <summary>
    /// Parte pura de <see cref="MarcarRechazosQueBloqueanAsync"/>. Una rechazada
    /// bloquea si el estado de SU Centro tiene una causa bloqueante sobre SU
    /// documento: el cálculo solo emite la causa de rechazo cuando la rechazada
    /// es aplicable a ese Centro, así que un rechazo de otro Centro, de un
    /// Trabajador desvinculado o de un tipo que no aplica queda en false. No se
    /// distingue qué clase de causa bloqueante es: si ese mismo documento
    /// cierra el Centro por otro motivo (vigencia vencida en la plataforma),
    /// el grupo también bloquea de verdad.
    /// </summary>
    public static IReadOnlyList<ItemBandejaDto> MarcarRechazosQueBloquean(
        IReadOnlyList<ItemBandejaDto> items, IReadOnlyDictionary<Guid, ResultadoEstadoCentro> estadosPorCentro) => items
        .Select(i => i.Tipo == TipoItemBandeja.PlataformaRechazada
                     && i.CentroId is { } centroId
                     && i.DocumentoId is { } documentoId
                     && estadosPorCentro.TryGetValue(centroId, out var estado)
                     && estado.Causas.Any(c => c.Bloqueante && c.DocumentoId == documentoId)
            ? i with { RechazoBloqueaCentro = true }
            : i)
        .ToList();

    /// <summary>Extraído como método puro (mismo patrón que ObtenerBandejaGestorQueryHandler.Fusionar) para poder probar la agrupación sin IMediator.</summary>
    public static BandejaAgrupadaDto Agrupar(IReadOnlyList<ItemBandejaDto> items)
    {
        var sinGrupo = new List<ItemBandejaDto>();
        var claves = new Dictionary<string, (string Titulo, List<ItemBandejaDto> Items)>();
        var orden = new List<string>();

        foreach (var item in items)
        {
            var clave = ClaveGrupo(item);
            if (clave is null)
            {
                sinGrupo.Add(item);
                continue;
            }

            var (grupoId, titulo) = clave.Value;
            if (!claves.TryGetValue(grupoId, out var entrada))
            {
                entrada = (titulo, []);
                claves[grupoId] = entrada;
                orden.Add(grupoId);
            }
            entrada.Items.Add(item);
        }

        // La prioridad de ObtenerBandejaGestorQueryHandler.Fusionar ya dejó
        // los items más urgentes primero dentro de cada grupo (GroupBy es
        // estable) — aquí solo hace falta ordenar los GRUPOS entre sí:
        // primero los que bloquean acceso, luego por la severidad más alta
        // presente, luego por tamaño.
        var grupos = orden
            .Select(id => new GrupoColaDto(
                id, claves[id].Titulo,
                claves[id].Items.Any(i => i.Tipo == TipoItemBandeja.RequisitoPendiente || BloqueaAccesoAlCentro(i)),
                claves[id].Items))
            .OrderByDescending(g => g.BloqueaAcceso)
            .ThenBy(g => g.Items.Min(PrioridadTipo))
            .ThenByDescending(g => g.Items.Count)
            .ToList();

        return new BandejaAgrupadaDto(grupos, sinGrupo);
    }

    /// <summary>
    /// Qué item de la cola cierra de verdad el acceso a un Centro de Trabajo.
    /// Única fuente para la tarjeta del grupo (badge y banda), el recuento
    /// «M bloquean acceso» y la pista «N centros bloqueados» de Inicio:
    /// <list type="bullet">
    ///   <item><description><see cref="TipoItemBandeja.RequisitoPendiente"/> que no es alta nueva — un alta sin completar no cierra nada.</description></item>
    ///   <item><description><see cref="TipoItemBandeja.PlataformaRechazada"/> que el cálculo de estado del Centro cuenta como bloqueante (D-7, <see cref="ItemBandejaDto.RechazoBloqueaCentro"/>). Una rechazada no aplicable a ese Centro no cuenta.</description></item>
    /// </list>
    /// </summary>
    public static bool BloqueaAccesoAlCentro(ItemBandejaDto item) => item.Tipo switch
    {
        TipoItemBandeja.RequisitoPendiente => !item.EsAltaNueva,
        TipoItemBandeja.PlataformaRechazada => item.RechazoBloqueaCentro,
        _ => false
    };

    /// <summary>(tipo de clave, id, título) — null cuando el item no tiene ni Cliente ni Empresa resueltos.</summary>
    private static (string GrupoId, string Titulo)? ClaveGrupo(ItemBandejaDto item) => item switch
    {
        { ClienteId: { } clienteId } => ($"cliente-{clienteId}", item.ClienteNombre ?? "—"),
        { EmpresaId: { } empresaId, EmpresaNombre: { } empresaNombre } => ($"empresa-{empresaId}", empresaNombre),
        _ => null
    };

    private static int PrioridadTipo(ItemBandejaDto item) => ObtenerBandejaGestorQueryHandler.Prioridad(item.Tipo);
}
