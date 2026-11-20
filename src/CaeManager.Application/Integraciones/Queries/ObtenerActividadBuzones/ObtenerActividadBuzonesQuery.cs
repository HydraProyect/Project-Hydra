using CaeManager.Application.Tenants.Encargo;
using MediatR;

namespace CaeManager.Application.Integraciones.Queries.ObtenerActividadBuzones;

/// <summary>
/// Los dos datos de actividad que la tabla de Buzones enseña por buzón (columnas «Último correo» y «Abiertas», lienzo
/// «Comunicaciones multi-buzón», tablero de Buzones). Una sola petición para todos los buzones de la página: el coste no
/// crece con su número (dos agregaciones, sin consulta por fila).
///
/// <para>
/// <b>Contrato de lectura del Núcleo.</b> La página de Buzones es del Núcleo (Integraciones) y los datos son de Comunicaciones
/// (Outbound): la petición y su respuesta viven aquí y las atiende <c>ObtenerActividadBuzonesQueryHandler</c>, en
/// Application.Comunicaciones. Así la página no referencia Outbound (ADR-013 § 4.1, regla 3: Núcleo -> Outbound no crece) y
/// Outbound -> Núcleo está permitido.
/// </para>
///
/// <para>
/// <b>Visibilidad.</b> Los dos datos se calculan sobre las conversaciones que <c>ConversacionesVisibles.FiltrarAsync</c>
/// deja ver a quien pregunta (R-3a): la misma regla que la bandeja y que el detalle. Quien ve la bandeja entera ve aquí la
/// cifra entera; un Gestor CAE con una cartera acotada, solo la de sus hilos; y un buzón personal de otro Gestor CAE no sale en
/// la respuesta (la regla lo cierra también para el Administrador; la tabla enseña «—»). Esta petición no abre ningún hilo ni ningún dato que la
/// bandeja no abra: solo agrega lo ya visible. No toca la autorización.
/// </para>
///
/// <para>
/// <b>«Abiertas»</b> son las conversaciones del buzón cuyo estado es «Abierta» (<c>EstadoConversacion.Abierta</c>, estricta): el
/// estado que la bandeja enseña y filtra (<c>ObtenerConversacionesQuery.Estado</c>). No cuentan las que esperan respuesta
/// externa o a un tercero, ni las resueltas ni las cerradas. <b>«Último correo»</b> es la fecha del último mensaje entrante
/// de esas conversaciones visibles, por la conexión por la que entran (<c>Conversacion.ConexionIntegracionId</c>); un acuse o
/// una respuesta nuestra no son un correo recibido.
/// </para>
/// </summary>
[AreaDelEncargo("Comunicaciones")]
public record ObtenerActividadBuzonesQuery(IReadOnlyCollection<Guid> ConexionIds)
    : IRequest<IReadOnlyDictionary<Guid, ActividadBuzonDto>>;

/// <param name="UltimoCorreoUtc">Instante del último correo recibido que quien pregunta puede ver; nulo si no hay ninguno.</param>
/// <param name="ConversacionesAbiertas">Conversaciones del buzón en estado «Abierta» que quien pregunta puede ver.</param>
public record ActividadBuzonDto(DateTime? UltimoCorreoUtc, int ConversacionesAbiertas);
