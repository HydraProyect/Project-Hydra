using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Bandeja;

/// <summary>
/// Traduce TipoItemBandeja a color/etiqueta/texto de la acción primaria —
/// mismo espíritu que EstadoDocumentoUi, un solo sitio de traducción.
/// </summary>
public static class TipoItemBandejaUi
{
    /// <summary>
    /// Recibe el ítem completo, no solo el tipo — un RequisitoPendiente con
    /// EsAltaNueva (ver DocumentacionBloqueantePendienteDto.EsAltaNueva) no es
    /// un bloqueo que haya que corregir, es una alta que todavía no se ha
    /// completado: mismo tono que un documento simplemente pendiente, no el
    /// rojo de "algo se rompió".
    /// </summary>
    public static TonoBadge Tono(ItemBandejaDto item) => item.Tipo switch
    {
        TipoItemBandeja.SugerenciaVisitaUrgente => TonoBadge.Peligro,
        TipoItemBandeja.Faltante => TonoBadge.Peligro,
        TipoItemBandeja.Vencido => TonoBadge.Peligro,
        // Mismo tono que EstadoAcreditacion.Rechazada en PlataformaTab.razor.
        TipoItemBandeja.PlataformaRechazada => TonoBadge.Peligro,
        TipoItemBandeja.RequisitoPendiente => item.EsAltaNueva ? TonoBadge.Advertencia : TonoBadge.Peligro,
        TipoItemBandeja.VisitaUrgente => TonoBadge.Advertencia,
        TipoItemBandeja.Urgente => TonoBadge.Advertencia,
        TipoItemBandeja.RevisionIa => TonoBadge.Advertencia,
        TipoItemBandeja.DeteccionPendiente => TonoBadge.Advertencia,
        // Mismo tono que ya usa PlataformaTab.razor para EstadoAcreditacion.PendienteDeSubir.
        TipoItemBandeja.PlataformaPendiente => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro
    };

    /// <summary>Overload por tipo puro — usado donde no hay un ítem concreto a mano (p. ej. agrupar recuentos por severidad en GrupoCola).</summary>
    public static TonoBadge Tono(TipoItemBandeja tipo) => Tono(new ItemBandejaDto(
        Id: "", Tipo: tipo, Titulo: "", Subtitulo: "", Fecha: null,
        TrabajadorId: null, CentroId: null, DocumentoId: null, TipoDocumentoId: null, RequisitoId: null));

    public static string Texto(ItemBandejaDto item) => item.Tipo switch
    {
        TipoItemBandeja.SugerenciaVisitaUrgente => "Visita sorpresa",
        TipoItemBandeja.Faltante => "Falta",
        TipoItemBandeja.Vencido => "Vencido",
        // "Bloquea el centro" implica una regresión a corregir; una alta
        // nueva nunca llegó a completarse, así que "Alta pendiente" describe
        // mejor la situación real (ver EsAltaNueva).
        TipoItemBandeja.RequisitoPendiente => item.EsAltaNueva ? "Alta pendiente" : "Bloquea el centro",
        TipoItemBandeja.VisitaUrgente => "Visita próxima",
        TipoItemBandeja.Urgente => "Urgente",
        TipoItemBandeja.RevisionIa => "Revisión IA",
        TipoItemBandeja.DeteccionPendiente => "Detección de personal",
        // No "Falta": la documentación existe y está al día en Talveg, solo
        // falta replicarla en la plataforma del cliente — un badge "Falta"
        // sugeriría (incorrectamente) que hay que reclamarla a alguien.
        TipoItemBandeja.PlataformaPendiente => "Pendiente de envío",
        // La plataforma ya evaluó el documento y lo devolvió: no es «falta»
        // ni «pendiente», es una respuesta negativa que hay que atender.
        TipoItemBandeja.PlataformaRechazada => "Rechazada por plataforma",
        _ => "—"
    };

    /// <summary>
    /// Recibe el ítem completo, no solo el tipo — PlataformaPendiente necesita
    /// el nombre real de la plataforma ("Subir a Dokify"), que es un dato del
    /// ítem (ItemBandejaDto.ProveedorNombre), no un texto fijo por tipo como
    /// el resto de acciones; RequisitoPendiente con EsAltaNueva ofrece
    /// directamente "Adjuntar" el documento que falta, en vez de mandar a ver
    /// un requisito que en realidad nunca se llegó a cumplir.
    /// </summary>
    public static string TextoAccion(ItemBandejaDto item) => item.Tipo switch
    {
        TipoItemBandeja.Faltante => "Subir documento",
        TipoItemBandeja.RevisionIa => "Revisar",
        TipoItemBandeja.RequisitoPendiente => item.EsAltaNueva ? "Adjuntar" : "Ver requisito",
        TipoItemBandeja.SugerenciaVisitaUrgente => "Confirmar visita",
        TipoItemBandeja.VisitaUrgente => "Ver visita",
        TipoItemBandeja.DeteccionPendiente => "Revisar detección",
        TipoItemBandeja.PlataformaPendiente => $"Subir a {item.ProveedorNombre}",
        TipoItemBandeja.PlataformaRechazada => $"Corregir en {item.ProveedorNombre}",
        _ => "Gestionar"
    };

    /// <summary>
    /// U-2 (plan de sesiones nocturnas 2026-09-02): Bandeja hoy "solo navega"
    /// (AccionesBandeja), a diferencia de Centro 360/Trabajador 360/Documentos,
    /// que sí ofrecen reclamar — este es el gate que decide cuándo tiene
    /// sentido el botón "Reclamar" en un ítem. Solo Faltante/Vencido/Urgente
    /// son documentos de Trabajador reclamables por email hoy (ver
    /// ObtenerLoteReclamacionQuery); el resto de tipos (RevisionIa,
    /// RequisitoPendiente, visitas, detecciones, PlataformaPendiente) no son
    /// "algo que se le pide a alguien por correo", son otra clase de trabajo.
    /// </summary>
    public static bool EsReclamable(ItemBandejaDto item) =>
        item.Tipo is TipoItemBandeja.Faltante or TipoItemBandeja.Vencido or TipoItemBandeja.Urgente
        && item.TrabajadorId is not null
        && item.TipoDocumentoId is not null;

    /// <summary>
    /// «Bloquea acceso» solo cuando de verdad bloquea.
    /// <see cref="GrupoColaDto.BloqueaAcceso"/> significa «el grupo tiene algún
    /// RequisitoPendiente», y un requisito de ALTA NUEVA no es un bloqueo que
    /// corregir sino un alta sin completar (mismo criterio que
    /// <see cref="Tono(ItemBandejaDto)"/>, que ya le da otro tono). Pintar la
    /// banda, el badge o contarlo en «N bloquean acceso» por él le dice al
    /// Gestor CAE que un Centro está cerrado cuando no lo está.
    ///
    /// <para>
    /// Vive aquí, y no en <c>GrupoCola</c>, porque tiene ya dos lectores que no
    /// pueden divergir: la tarjeta del grupo (badge y banda, en «Mi trabajo» y
    /// en Inicio) y el recuento «N grupos · M bloquean acceso» de la cabecera
    /// de «Requiere atención» en Inicio. Un grupo marcado en la tarjeta y no
    /// contado arriba —o al revés— sería peor que no decir nada.
    /// </para>
    /// </summary>
    public static bool BloqueaAccesoDeVerdad(GrupoColaDto grupo) =>
        grupo.BloqueaAcceso
        && grupo.Items.Any(i => i.Tipo == TipoItemBandeja.RequisitoPendiente && !i.EsAltaNueva);

    /// <summary>
    /// Gate del hallazgo de P9 (2026-09-18, CAPA-USUARIO-AVANZADO-TALVEG.md
    /// § 6.1 quinquies): «solo la vigencia es copiable», con la excepción
    /// acotada de Detección/Revisión IA para la fecha del suceso. Antes de
    /// este gate, <c>PanelResolverItem</c> envolvía <c>Item.Fecha</c> en
    /// <c>TextoFechaCopiable</c> para TODOS los tipos sin mirar cuál era, y el
    /// hallazgo documentó el defecto sin corregirlo.
    ///
    /// <para>
    /// <b>Medido contra <see cref="ObtenerBandejaGestorQueryHandler"/>
    /// (2026-09-18), no asumido de la redacción del hallazgo</b> — que decía
    /// "todos los tipos salvo RevisionIa/DeteccionPendiente" sin comprobar qué
    /// representa <c>Fecha</c> en cada uno. La comprobación real:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>Faltante</c>/<c>Vencido</c>/<c>Urgente</c>: <c>Fecha = FechaVencimiento</c> — ES vigencia, copiable bajo la regla estricta sin necesitar la excepción de P9.</description></item>
    ///   <item><description><c>RevisionIa</c>: <c>Fecha = FechaEmisionDetectada</c> — la excepción de P9.</description></item>
    ///   <item><description><c>VisitaUrgente</c>: <c>Fecha = FechaInicio</c> — NI vigencia NI P9. El defecto real.</description></item>
    ///   <item><description><c>SugerenciaVisitaUrgente</c>: <c>Fecha = FechaInicioSugerida</c> — NI vigencia NI P9. El defecto real.</description></item>
    ///   <item><description><c>RequisitoPendiente</c>/<c>DeteccionPendiente</c>/<c>PlataformaPendiente</c>/<c>PlataformaRechazada</c>: <c>Fecha = null</c> hoy — sin fecha no hay nada que copiar, el propio componente ya lo trata como texto plano.</description></item>
    /// </list>
    /// <para>
    /// Restringir a solo RevisionIa/DeteccionPendiente, como decía la
    /// redacción literal del hallazgo, habría sido un cambio de más: le habría
    /// quitado la copia de vigencia a Faltante/Vencido/Urgente, que es
    /// legítima. El gate excluye únicamente los dos tipos donde <c>Fecha</c>
    /// no es ni vigencia ni la excepción.
    /// </para>
    /// </summary>
    public static bool EsFechaCopiable(ItemBandejaDto item) =>
        item.Tipo is not (TipoItemBandeja.VisitaUrgente or TipoItemBandeja.SugerenciaVisitaUrgente);
}
