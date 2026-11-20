namespace CaeManager.Application.Integraciones;

/// <summary>
/// Puerto de lectura del Núcleo (Integraciones) por el que la tabla de Buzones obtiene, de Comunicaciones, los dos datos de
/// actividad de cada buzón: «Último correo» y «Abiertas» (lienzo «Comunicaciones multi-buzón», tablero de Buzones). Una sola
/// llamada para todos los buzones de la página: el coste no crece con su número.
///
/// <para>
/// <b>Por qué un puerto y no la petición.</b> La página es del Núcleo y los datos son de Comunicaciones (Outbound): referenciar
/// la petición desde la página sería una arista Núcleo -> Outbound nueva, y esa deuda solo baja (ADR-013 § 4.1, regla 3). El
/// puerto vive aquí y lo implementa Application.Comunicaciones (Outbound -> Núcleo está permitido), como
/// <c>IFuenteLiquidacionOperadorCae</c>. La implementación envía la petición de Comunicaciones por el mediador, así que pasa
/// por la guarda y el ámbito de Outbound: un Tenant solo Inbound no obtiene más de lo que la bandeja le enseña (D6=a).
/// </para>
///
/// <para>
/// <b>Visibilidad.</b> Los dos datos se calculan sobre las conversaciones que <c>ConversacionesVisibles.FiltrarAsync</c>
/// deja ver a quien pregunta (R-3a): la misma regla que la bandeja y que el detalle. Un buzón personal de otro Gestor CAE no
/// sale en la respuesta (la tabla enseña «—»), como tampoco un buzón que no se ha podido medir. No abre ningún hilo ni dato
/// que la bandeja no abra: solo agrega lo ya visible. No toca la autorización.
/// </para>
/// </summary>
public interface IActividadBuzones
{
    /// <returns>La actividad de cada buzón medido; la clave falta si el buzón no se ha medido (la tabla enseña «—»).</returns>
    Task<IReadOnlyDictionary<Guid, ActividadBuzonDto>> ObtenerAsync(
        IReadOnlyCollection<Guid> conexionIds, CancellationToken cancellationToken = default);
}

/// <param name="UltimoCorreoUtc">Instante del último correo recibido que quien pregunta puede ver; nulo si no hay ninguno.</param>
/// <param name="ConversacionesAbiertas">Conversaciones del buzón en estado «Abierta» que quien pregunta puede ver.</param>
public record ActividadBuzonDto(DateTime? UltimoCorreoUtc, int ConversacionesAbiertas);
