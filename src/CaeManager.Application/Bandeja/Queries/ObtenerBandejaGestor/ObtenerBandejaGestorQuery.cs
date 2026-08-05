using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.RequisitosDocumentales.Queries.ObtenerRequisitosDocumentalesPendientes;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;

/// <summary>
/// Fase C: una sola cola priorizada — vencidos, urgentes, faltantes,
/// revisiones IA pendientes y requisitos documentales que bloquean el
/// acceso a un Centro — para que el gestor no tenga que visitar cuatro
/// pantallas distintas para saber qué atender primero. Compone Queries ya
/// existentes vía <see cref="IMediator"/> en vez de reimplementar sus
/// condiciones (mismo patrón que <c>ObtenerDashboardEjecutivoQuery</c>),
/// así que cada alcance/cartera de usuario (<c>IAlcanceDatosService</c>) se
/// sigue resolviendo una única vez, dentro de cada Query compuesta.
///
/// Deliberadamente **no** incluye <see cref="EstadoDocumento.Proximo"/>: es
/// el mismo umbral "todavía no urgente" que ya separa `/alertas` de una
/// cola de trabajo real — ver `EstadoDocumentoUi`. Sigue disponible completo
/// en `/alertas`, que no pierde ninguna fila.
/// </summary>
public record ObtenerBandejaGestorQuery : IRequest<IReadOnlyList<ItemBandejaDto>>;

public enum TipoItemBandeja
{
    Faltante,
    Vencido,
    RequisitoPendiente,
    Urgente,
    RevisionIa
}

public record ItemBandejaDto(
    string Id,
    TipoItemBandeja Tipo,
    string Titulo,
    string Subtitulo,
    DateOnly? Fecha,
    Guid? TrabajadorId,
    Guid? CentroId,
    Guid? DocumentoId,
    Guid? TipoDocumentoId,
    Guid? RequisitoId);

public class ObtenerBandejaGestorQueryHandler(IMediator mediator) : IRequestHandler<ObtenerBandejaGestorQuery, IReadOnlyList<ItemBandejaDto>>
{
    public async Task<IReadOnlyList<ItemBandejaDto>> Handle(ObtenerBandejaGestorQuery request, CancellationToken cancellationToken)
    {
        var alertas = await mediator.Send(new ObtenerAlertasQuery(), cancellationToken);
        var revisiones = await mediator.Send(new ObtenerRevisionesIaPendientesQuery(), cancellationToken);
        var requisitos = await mediator.Send(new ObtenerRequisitosDocumentalesPendientesQuery(), cancellationToken);

        return Fusionar(alertas, revisiones, requisitos);
    }

    /// <summary>
    /// Extraído como método puro y estático (mismo patrón que
    /// <c>ObtenerDashboardEjecutivoQueryHandler.Fusionar</c>) para poder
    /// probar la fusión/prioridad sin tener que construir un
    /// <see cref="IMediator"/> real.
    /// </summary>
    public static IReadOnlyList<ItemBandejaDto> Fusionar(
        IReadOnlyList<AlertaDto> alertas,
        IReadOnlyList<RevisionIaDocumentoDto> revisiones,
        IReadOnlyList<RequisitoDocumentalPendienteDto> requisitos)
    {
        var items = new List<ItemBandejaDto>();

        items.AddRange(alertas
            .Where(a => a.Estado != EstadoDocumento.Proximo)
            .Select(a => new ItemBandejaDto(
                Id: $"alerta-{a.DocumentoId?.ToString() ?? $"{a.TrabajadorId}-{a.TipoDocumentoId}"}",
                Tipo: a.Estado == EstadoDocumento.Faltante ? TipoItemBandeja.Faltante
                    : a.Estado == EstadoDocumento.Vencido ? TipoItemBandeja.Vencido
                    : TipoItemBandeja.Urgente,
                Titulo: a.TipoDocumentoNombre,
                Subtitulo: a.CentroNombre is null ? a.TrabajadorNombre : $"{a.TrabajadorNombre} — {a.CentroNombre}",
                Fecha: a.FechaVencimiento,
                TrabajadorId: a.TrabajadorId,
                CentroId: null,
                DocumentoId: a.DocumentoId,
                TipoDocumentoId: a.TipoDocumentoId,
                RequisitoId: null)));

        items.AddRange(revisiones.Select(r => new ItemBandejaDto(
            Id: $"revision-{r.Id}",
            Tipo: TipoItemBandeja.RevisionIa,
            Titulo: r.TipoDocumentoNombre,
            Subtitulo: $"{r.TrabajadorNombre} — {r.Motivo}",
            Fecha: r.FechaEmisionDetectada,
            TrabajadorId: null,
            CentroId: null,
            DocumentoId: r.DocumentoId,
            TipoDocumentoId: null,
            RequisitoId: null)));

        items.AddRange(requisitos.Select(rq => new ItemBandejaDto(
            Id: $"requisito-{rq.Id}",
            Tipo: TipoItemBandeja.RequisitoPendiente,
            Titulo: rq.Descripcion,
            Subtitulo: rq.CentroNombre,
            Fecha: null,
            TrabajadorId: null,
            CentroId: rq.CentroId,
            DocumentoId: null,
            TipoDocumentoId: null,
            RequisitoId: rq.Id)));

        // Faltante (nada subido) y Vencido compiten por el primer puesto;
        // un Requisito que bloquea el acceso físico a un Centro entero pesa
        // más que un Urgente individual, que a su vez pesa más que una
        // revisión IA (ya tiene documento, solo falta confirmar el dato).
        return items
            .OrderBy(i => i.Tipo switch
            {
                TipoItemBandeja.Faltante => 0,
                TipoItemBandeja.Vencido => 1,
                TipoItemBandeja.RequisitoPendiente => 2,
                TipoItemBandeja.Urgente => 3,
                _ => 4
            })
            .ThenBy(i => i.Fecha)
            .ThenBy(i => i.Id)
            .ToList();
    }
}
