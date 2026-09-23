using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
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
    DeteccionPendiente,
    /// <summary>
    /// La documentación está al día en Talveg pero no se ha subido/actualizado
    /// todavía en la plataforma del cliente (Dokify, Nalanda...) —
    /// <see cref="Documentos.Queries.ObtenerAcreditacionesPorProveedor.ObtenerAcreditacionesPorProveedorQuery"/>,
    /// solo <c>EstadoAcreditacion.PendienteDeSubir</c> (Rechazada es
    /// <see cref="PlataformaRechazada"/>, otra situación: el portal ya la
    /// evaluó y la devolvió, no "falta subirla por primera vez"). Distinto de
    /// Faltante: aquí no hay nada que reclamar a nadie, solo subir un archivo
    /// que Talveg ya tiene.
    /// </summary>
    PlataformaPendiente,
    /// <summary>
    /// La plataforma del cliente evaluó el documento y lo devolvió
    /// (<c>EstadoAcreditacion.Rechazada</c>): hay que corregirlo y volver a
    /// subirlo, con el motivo real del rechazo a la vista. Es trabajo del
    /// Gestor CAE —no una foto del estado que solo se mira en Documentos—, y
    /// pesa más que <see cref="PlataformaPendiente"/>: mientras no se
    /// corrija, la acreditación del documento en esa plataforma es negativa.
    /// </summary>
    PlataformaRechazada,
    /// <summary>
    /// <see cref="EstadoDocumento.Proximo"/> — deliberadamente FUERA de
    /// <see cref="ObtenerBandejaGestorQueryHandler.Fusionar"/> (ver el
    /// comentario de esa clase: es el umbral "todavía no urgente" que separa
    /// <c>/alertas</c> de la cola de trabajo real de Nivel 0). Existe como
    /// valor del enum solo para
    /// <c>ObtenerMiTrabajoAgregadoQueryHandler</c> (Mi trabajo Gen2,
    /// CONTRATO-MI-TRABAJO-GEN2-MULTI-TENANT-2026-09-22.md § 5/D-6: "el
    /// vencimiento próximo entra en Mi trabajo" a nivel agregado, sin
    /// cambiar qué ve <c>/bandeja</c> ni Inicio). <c>Fusionar</c> nunca
    /// produce este valor — /bandeja e Inicio no lo verán jamás.
    /// </summary>
    VencimientoProximo,
    /// <summary>
    /// <see cref="EstadoAcreditacion.Subida"/> — documentación ya
    /// enviada a la plataforma del cliente, a la espera de su respuesta.
    /// Igual que <see cref="VencimientoProximo"/>, deliberadamente fuera de
    /// <c>Fusionar</c> (ver su comentario: "Subida no entra... seguimiento,
    /// no cola") y solo emitido por
    /// <c>ObtenerMiTrabajoAgregadoQueryHandler</c> para el bucket
    /// "Seguimiento" del contrato Gen2 § 5/§ 7.
    /// </summary>
    EnPlataformaSeguimiento
}

/// <param name="CreadaEnUtc">
/// Cuándo apareció este ítem — solo lo tienen SugerenciaVisitaUrgente/DeteccionPendiente/RevisionIa
/// (docs/blueprints/OPERATIONAL-HOME.md § 6): un documento Vencido/Faltante/Urgente no "aparece",
/// su estado cambia, así que no tiene un momento de creación que registrar. Alimenta el resumen
/// de ausencia — null en el resto de tipos.
/// </param>
/// <param name="ClienteId">
/// Cliente de la situación — agrupador "por situación" del rediseño de
/// Inicio (GrupoCola, hallazgo P-03 de la auditoría de producto
/// 2026-08-16). Resuelto por cada Query de origen (ver AlertaDto,
/// RevisionIaDocumentoDto, DocumentacionBloqueantePendienteDto,
/// VisitaListaDto, SugerenciaVisitaCorreoPendienteDto); null cuando no se
/// pudo resolver un Centro/Cliente para el ítem (queda sin grupo de
/// Cliente — mira <paramref name="EmpresaId"/> antes de darlo por huérfano).
/// </param>
/// <param name="EmpresaNombre">
/// Solo para DeteccionPendiente y revisiones IA de Documento de Empresa: ahí
/// no hay Cliente que resolver, la Empresa ya es el grupo natural (una
/// detección de alta/baja de personal es un hecho de la Empresa, no de un
/// Cliente concreto).
/// </param>
/// <param name="TrabajadorNombre">
/// Solo cuando <paramref name="TrabajadorId"/> referencia un Trabajador real
/// (no una detección sin resolver) — sub-agrupación Empresa→Trabajador de
/// "Requiere atención" (GrupoCola): el nombre visible del segundo nivel, no
/// solo la clave de agrupación.
/// </param>
/// <param name="ProveedorNombre">
/// Solo PlataformaPendiente y PlataformaRechazada — nombre de la plataforma
/// del cliente (Dokify, Nalanda...) a la que falta subir el documento o que
/// lo rechazó. La acción primaria de estos tipos
/// tipo necesita el nombre concreto ("Subir a Dokify"), no un texto genérico
/// como el resto de tipos — ver TipoItemBandejaUi.TextoAccion.
/// </param>
/// <param name="EsAltaNueva">
/// Solo RequisitoPendiente — ver DocumentacionBloqueantePendienteDto.EsAltaNueva.
/// Cambia el badge/acción (TipoItemBandejaUi) para no alarmar como "bloqueo"
/// lo que en realidad es una alta que todavía no se ha completado.
/// </param>
/// <param name="EmpresaEsPropia">
/// Solo cuando el sujeto de la tarea es una Empresa y no una persona
/// (<paramref name="TrabajadorId"/> null, <paramref name="EmpresaId"/> no
/// null, fuera de DeteccionPendiente): true si es la Empresa propia del
/// Tenant propietario, false si es una Subcontrata. Lo rellena solo Mi
/// trabajo Gen2 (<c>ObtenerMiTrabajoAgregadoQueryHandler</c>), que rotula así
/// el sujeto (contrato de Mi trabajo Gen2, § 14). /bandeja no lo rellena: null
/// significa «no se sabe», nunca «Subcontrata».
/// </param>
/// <param name="RechazoBloqueaCentro">
/// Solo PlataformaRechazada: true si el cálculo de estado del Centro de
/// Trabajo (<c>ICalculoEstadoCentroService</c>, D-7 del piloto Outbound)
/// cuenta este documento como causa bloqueante de ESE Centro — la rechazada
/// es aplicable a él (su canal, Trabajador aún asignado, tipo que le aplica).
/// Una rechazada no aplicable sigue siendo trabajo en la cola, pero no cierra
/// ningún Centro, y se queda en false. Lo rellena solo
/// <c>ObtenerBandejaAgrupadaQueryHandler</c> (la cola agrupada que leen
/// /bandeja e Inicio). Ni <see cref="ObtenerBandejaGestorQuery"/> —que también
/// consume la vigilancia de visitas urgentes, a la que no le hace falta— ni
/// Mi trabajo agregada, que clasifica por severidad
/// (<c>ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo</c>) y no pinta el
/// «bloquea acceso» del grupo, lo rellenan: allí false significa «no
/// calculado», no «no bloquea».
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
    DateTime? CreadaEnUtc = null,
    Guid? ClienteId = null,
    string? ClienteNombre = null,
    string? EmpresaNombre = null,
    string? TrabajadorNombre = null,
    string? ProveedorNombre = null,
    bool EsAltaNueva = false,
    bool? EmpresaEsPropia = null,
    bool RechazoBloqueaCentro = false);

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
        var pendientesPlataforma = await mediator.Send(new ObtenerAcreditacionesPorProveedorQuery(), cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        return Fusionar(
            alertas, revisiones, requisitos, visitasUrgentes.Elementos, sugerenciasVisita, detecciones, pendientesPlataforma,
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
        IReadOnlyList<ProveedorAcreditacionesDto> pendientesPlataforma,
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
                CentroId: a.CentroId,
                DocumentoId: a.DocumentoId,
                TipoDocumentoId: a.TipoDocumentoId,
                RequisitoId: null,
                ClienteId: a.ClienteId,
                ClienteNombre: a.ClienteNombre,
                EmpresaId: a.EmpresaId,
                EmpresaNombre: a.EmpresaNombre,
                TrabajadorNombre: a.TrabajadorNombre)));

        items.AddRange(revisiones.Select(r => new ItemBandejaDto(
            Id: $"revision-{r.Id}",
            Tipo: TipoItemBandeja.RevisionIa,
            Titulo: r.TipoDocumentoNombre,
            Subtitulo: $"{r.PropietarioNombre} — {r.Motivo}",
            Fecha: r.FechaEmisionDetectada,
            TrabajadorId: r.TrabajadorId,
            CentroId: null,
            DocumentoId: r.DocumentoId,
            TipoDocumentoId: null,
            RequisitoId: null,
            CreadaEnUtc: r.CreadaEnUtc,
            EmpresaId: r.EmpresaId,
            ClienteId: r.ClienteId,
            ClienteNombre: r.ClienteNombre,
            EmpresaNombre: r.EmpresaNombre,
            // Documento de Trabajador: PropietarioNombre YA es "Nombre
            // Apellidos" (ver ObtenerRevisionesIaPendientesQueryHandler) —
            // Documento de Empresa no tiene Trabajador, se queda sin este dato.
            TrabajadorNombre: r.TrabajadorId is not null ? r.PropietarioNombre : null)));

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
            RequisitoId: null,
            ClienteId: rq.ClienteId,
            ClienteNombre: rq.ClienteNombre,
            EmpresaId: rq.EmpresaId,
            EmpresaNombre: rq.EmpresaNombre,
            TrabajadorNombre: rq.TrabajadorNombre,
            EsAltaNueva: rq.EsAltaNueva)));

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
                RequisitoId: null,
                ClienteId: v.ClienteId,
                ClienteNombre: v.ClienteRazonSocial)));

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
                CreadaEnUtc: s.CreadaEnUtc,
                ClienteId: s.ClienteId,
                ClienteNombre: s.ClienteNombre)));

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
            CreadaEnUtc: d.CreadaEnUtc,
            EmpresaNombre: d.EmpresaRazonSocial)));

        // PendienteDeSubir y Rechazada son dos tipos distintos porque piden dos
        // cosas distintas. PendienteDeSubir: la documentación está al día en
        // Talveg y solo falta replicarla en la plataforma del cliente, no hay
        // nada que reclamar. Rechazada: el portal ya la evaluó y la devolvió,
        // hay que corregir y volver a subir, y el motivo real del rechazo es
        // la parte que importa gestionar — por eso va en el subtítulo. Subida
        // no entra: está esperando la respuesta de la plataforma y no hay
        // nada que hacer hasta que llegue (seguimiento, no cola).
        var plataformas = pendientesPlataforma
            .SelectMany(proveedor => proveedor.Clientes.SelectMany(cliente => cliente.Documentos
                .Where(d => d.Estado is EstadoAcreditacion.PendienteDeSubir or EstadoAcreditacion.Rechazada)
                .Select(d => (proveedor, cliente, d))));

        items.AddRange(plataformas.Select(x => new ItemBandejaDto(
            Id: $"plataforma-{x.d.AcreditacionId}",
            Tipo: x.d.Estado == EstadoAcreditacion.Rechazada ? TipoItemBandeja.PlataformaRechazada : TipoItemBandeja.PlataformaPendiente,
            Titulo: x.d.TipoDocumentoNombre,
            Subtitulo: x.d.Estado == EstadoAcreditacion.Rechazada && !string.IsNullOrWhiteSpace(x.d.UltimoMotivoRechazo)
                ? $"{x.d.PropietarioNombre} — {x.d.UltimoMotivoRechazo}"
                : x.d.PropietarioNombre,
            Fecha: null,
            TrabajadorId: x.d.TrabajadorId,
            CentroId: x.d.CentroId,
            DocumentoId: x.d.DocumentoId,
            TipoDocumentoId: x.d.TipoDocumentoId,
            RequisitoId: null,
            ClienteId: x.cliente.ClienteId,
            ClienteNombre: x.cliente.ClienteNombre,
            EmpresaId: x.d.EmpresaId,
            TrabajadorNombre: x.d.TrabajadorId is not null ? x.d.PropietarioNombre : null,
            ProveedorNombre: x.proveedor.ProveedorNombre)));

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
                TipoItemBandeja.PlataformaRechazada => 3,
                TipoItemBandeja.VisitaUrgente => 4,
                TipoItemBandeja.RequisitoPendiente => 5,
                TipoItemBandeja.Urgente => 6,
                _ => 7
            })
            .ThenBy(i => i.Fecha)
            .ThenBy(i => i.Id)
            .ToList();
    }
}
