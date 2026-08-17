using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
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
/// <param name="BloqueaAcceso">True si algún item del grupo es <see cref="TipoItemBandeja.RequisitoPendiente"/> — esos grupos van primero (§ 11 de la ficha SCREEN 01, Parte XV de la auditoría).</param>
public record GrupoColaDto(string GrupoId, string Titulo, bool BloqueaAcceso, IReadOnlyList<ItemBandejaDto> Items);

/// <param name="SinGrupo">
/// Items sin Cliente ni Empresa resueltos (p. ej. una sugerencia de visita
/// sin Centro detectado por la IA) — no se descartan, se muestran sueltos
/// tras los grupos, igual que hacía la lista plana antes de este cambio.
/// </param>
public record BandejaAgrupadaDto(IReadOnlyList<GrupoColaDto> Grupos, IReadOnlyList<ItemBandejaDto> SinGrupo);

public class ObtenerBandejaAgrupadaQueryHandler(IMediator mediator) : IRequestHandler<ObtenerBandejaAgrupadaQuery, BandejaAgrupadaDto>
{
    public async Task<BandejaAgrupadaDto> Handle(ObtenerBandejaAgrupadaQuery request, CancellationToken cancellationToken)
    {
        var items = await mediator.Send(new ObtenerBandejaGestorQuery(), cancellationToken);
        return Agrupar(items);
    }

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
            .Select(id => new GrupoColaDto(id, claves[id].Titulo, claves[id].Items.Any(i => i.Tipo == TipoItemBandeja.RequisitoPendiente), claves[id].Items))
            .OrderByDescending(g => g.BloqueaAcceso)
            .ThenBy(g => g.Items.Min(PrioridadTipo))
            .ThenByDescending(g => g.Items.Count)
            .ToList();

        return new BandejaAgrupadaDto(grupos, sinGrupo);
    }

    /// <summary>(tipo de clave, id, título) — null cuando el item no tiene ni Cliente ni Empresa resueltos.</summary>
    private static (string GrupoId, string Titulo)? ClaveGrupo(ItemBandejaDto item) => item switch
    {
        { ClienteId: { } clienteId } => ($"cliente-{clienteId}", item.ClienteNombre ?? "—"),
        { EmpresaId: { } empresaId, EmpresaNombre: { } empresaNombre } => ($"empresa-{empresaId}", empresaNombre),
        _ => null
    };

    private static int PrioridadTipo(ItemBandejaDto item) => item.Tipo switch
    {
        TipoItemBandeja.SugerenciaVisitaUrgente => 0,
        TipoItemBandeja.Faltante => 1,
        TipoItemBandeja.Vencido => 2,
        TipoItemBandeja.VisitaUrgente => 3,
        TipoItemBandeja.RequisitoPendiente => 4,
        TipoItemBandeja.Urgente => 5,
        _ => 6
    };
}
