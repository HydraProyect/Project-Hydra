using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPendientes;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using MediatR;
using Microsoft.EntityFrameworkCore;

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
///
/// Fase F añade dos fuentes más: Visitas ya confirmadas dentro de la ventana
/// mínima de validación de la plataforma del cliente, y sugerencias de
/// visita (correo/WhatsApp) sin resolver todavía dentro de esa misma
/// ventana — estas últimas son las de mayor prioridad de toda la cola: una
/// "visita sorpresa" sin confirmar es lo más urgente de gestionar, porque
/// hasta que alguien la confirma ni siquiera hay Visita ni documentación
/// verificada.
/// </summary>
public record ObtenerBandejaGestorQuery : IRequest<IReadOnlyList<ItemBandejaDto>>;

public enum TipoItemBandeja
{
    SugerenciaVisitaUrgente,
    Faltante,
    Vencido,
    RequisitoPendiente,
    VisitaUrgente,
    Urgente,
    RevisionIa,
    DeteccionPendiente
}

/// <param name="CreadaEnUtc">
/// Cuándo apareció este ítem — solo lo tienen SugerenciaVisitaUrgente/DeteccionPendiente/RevisionIa
/// (docs/blueprints/OPERATIONAL-HOME.md § 6): un documento Vencido/Faltante/Urgente no "aparece",
/// su estado cambia, así que no tiene un momento de creación que registrar. Alimenta el resumen
/// de ausencia — null en el resto de tipos.
/// </param>
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
    Guid? RequisitoId,
    Guid? SugerenciaVisitaId = null,
    Guid? EmpresaId = null,
    DateTime? CreadaEnUtc = null);

public class ObtenerBandejaGestorQueryHandler(IMediator mediator, IConfiguracionQueryContext configuracionContext)
    : IRequestHandler<ObtenerBandejaGestorQuery, IReadOnlyList<ItemBandejaDto>>
{
    public async Task<IReadOnlyList<ItemBandejaDto>> Handle(ObtenerBandejaGestorQuery request, CancellationToken cancellationToken)
    {
        var alertas = await mediator.Send(new ObtenerAlertasQuery(), cancellationToken);
        var revisiones = await mediator.Send(new ObtenerRevisionesIaPendientesQuery(), cancellationToken);
        var requisitos = await mediator.Send(new ObtenerDocumentacionBloqueantePendienteQuery(), cancellationToken);
        var visitasUrgentes = await mediator.Send(
            new ObtenerVisitasQuery(Busqueda: null, SoloActivas: true, NotificadoCliente: null, SoloUrgentes: true, TamanoPagina: 200),
            cancellationToken);
        var sugerenciasVisita = await mediator.Send(new ObtenerSugerenciasVisitaCorreoPendientesQuery(), cancellationToken);
        var detecciones = await mediator.Send(new ObtenerDeteccionesPendientesQuery(), cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        return Fusionar(
            alertas, revisiones, requisitos, visitasUrgentes.Elementos, sugerenciasVisita, detecciones,
            hoy, parametros.HorasAvisoVisita, parametros.HorasCriticasVisita);
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
        IReadOnlyList<DocumentacionBloqueantePendienteDto> requisitos,
        IReadOnlyList<VisitaListaDto> visitasUrgentes,
        IReadOnlyList<SugerenciaVisitaCorreoPendienteDto> sugerenciasVisita,
        IReadOnlyList<DeteccionPendienteDto> detecciones,
        DateOnly hoy,
        int horasAvisoVisita,
        int horasCriticasVisita)
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
            Subtitulo: $"{r.PropietarioNombre} — {r.Motivo}",
            Fecha: r.FechaEmisionDetectada,
            TrabajadorId: null,
            CentroId: null,
            DocumentoId: r.DocumentoId,
            TipoDocumentoId: null,
            RequisitoId: null,
            CreadaEnUtc: r.CreadaEnUtc)));

        items.AddRange(requisitos.Select(rq => new ItemBandejaDto(
            Id: $"requisito-{rq.CentroId}-{rq.TrabajadorId}-{rq.TipoDocumentoId}",
            Tipo: TipoItemBandeja.RequisitoPendiente,
            Titulo: $"{rq.TipoDocumentoNombre} — {rq.TrabajadorNombre}",
            Subtitulo: rq.CentroNombre,
            Fecha: null,
            TrabajadorId: rq.TrabajadorId,
            CentroId: rq.CentroId,
            DocumentoId: null,
            TipoDocumentoId: rq.TipoDocumentoId,
            RequisitoId: null)));

        items.AddRange(visitasUrgentes
            .Where(v => v.NivelUrgencia is NivelUrgenciaVisita.Urgente or NivelUrgenciaVisita.Critica)
            .Select(v => new ItemBandejaDto(
                Id: $"visita-{v.Id}",
                Tipo: TipoItemBandeja.VisitaUrgente,
                Titulo: $"Visita {(v.NivelUrgencia == NivelUrgenciaVisita.Critica ? "crítica" : "urgente")}",
                Subtitulo: $"{v.CentroNombre} — {v.ClienteRazonSocial}",
                Fecha: v.FechaInicio,
                TrabajadorId: null,
                CentroId: v.CentroId,
                DocumentoId: null,
                TipoDocumentoId: null,
                RequisitoId: null)));

        // Sin fecha detectada la IA no pudo precisar cuándo es — se trata
        // como "visita sorpresa" (mismo día, sin margen) en vez de
        // descartarla por falta de dato: el pedido original explícito es
        // "avisar cuando llega una visita sorpresa para el mismo día".
        items.AddRange(sugerenciasVisita
            .Where(s => s.FechaInicioSugerida is null || CalculadoraUrgenciaVisita.Calcular(
                s.FechaInicioSugerida.Value, s.FechaInicioSugerida.Value, hoy, horasAvisoVisita, horasCriticasVisita)
                is not NivelUrgenciaVisita.Normal)
            .Select(s => new ItemBandejaDto(
                Id: $"sugerencia-visita-{s.Id}",
                Tipo: TipoItemBandeja.SugerenciaVisitaUrgente,
                Titulo: $"Visita sorpresa detectada ({(s.Canal == Domain.Comunicaciones.CanalConversacion.WhatsApp ? "WhatsApp" : "correo")})",
                Subtitulo: s.CentroNombre is null ? s.Resumen : $"{s.CentroNombre} — {s.Resumen}",
                Fecha: s.FechaInicioSugerida,
                TrabajadorId: null,
                CentroId: s.CentroId,
                DocumentoId: null,
                TipoDocumentoId: null,
                RequisitoId: null,
                SugerenciaVisitaId: s.Id,
                CreadaEnUtc: s.CreadaEnUtc)));

        items.AddRange(detecciones.Select(d => new ItemBandejaDto(
            Id: $"deteccion-{d.Id}",
            Tipo: TipoItemBandeja.DeteccionPendiente,
            Titulo: d.Tipo == TipoDeteccion.Nuevo ? "Alta detectada" : "Baja detectada",
            Subtitulo: $"{d.EmpresaRazonSocial} — {d.NombreCompleto}",
            Fecha: null,
            TrabajadorId: null,
            CentroId: null,
            DocumentoId: null,
            TipoDocumentoId: null,
            RequisitoId: null,
            EmpresaId: d.EmpresaId,
            CreadaEnUtc: d.CreadaEnUtc)));

        // Una sugerencia sin confirmar pesa más que cualquier otra cosa: sin
        // confirmarla no hay ni Visita ni documentación que verificar. Entre
        // el resto: Faltante/Vencido siguen siendo lo más urgente de lo ya
        // conocido; una Visita confirmada dentro de la ventana pesa más que
        // un Requisito bloqueante (tiene una fecha límite externa fija, el
        // Requisito no), que a su vez pesa más que un documento Urgente
        // individual o una revisión IA.
        return items
            .OrderBy(i => i.Tipo switch
            {
                TipoItemBandeja.SugerenciaVisitaUrgente => 0,
                TipoItemBandeja.Faltante => 1,
                TipoItemBandeja.Vencido => 2,
                TipoItemBandeja.VisitaUrgente => 3,
                TipoItemBandeja.RequisitoPendiente => 4,
                TipoItemBandeja.Urgente => 5,
                _ => 6
            })
            .ThenBy(i => i.Fecha)
            .ThenBy(i => i.Id)
            .ToList();
    }
}
