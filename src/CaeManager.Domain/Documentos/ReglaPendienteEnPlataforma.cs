namespace CaeManager.Domain.Documentos;

/// <summary>
/// <b>Punto único</b> de la regla «Pendiente en la plataforma» (decisión del propietario del producto, 2026-10-10):
/// un documento que todavía vale en TALVEG y que, en la plataforma CAE de un Centro, está sin subir
/// (<see cref="EstadoAcreditacion.PendienteDeSubir"/>) o subido y sin validar (<see cref="EstadoAcreditacion.Subida"/>)
/// cuenta como «Pendiente» en ese Centro.
///
/// <list type="bullet">
/// <item><b>Solo Centros con plataforma.</b> Las acreditaciones existen solo ante un acceso de tipo Plataforma de un
/// Centro (<c>AltaAcreditacionesPlataformaService</c>); un Centro sin plataforma nunca tiene un Pendiente.</item>
/// <item><b>Rechazada no es Pendiente</b>: es un «no» de la plataforma y sigue siendo causa propia del estado Bloqueado del
/// Centro (D-7). Validada en la plataforma (<see cref="EstadoAcreditacion.Aceptada"/>) y
/// <see cref="EstadoAcreditacion.NoRequerida"/> no son trabajo pendiente.</item>
/// <item><b>Solo un documento que todavía vale</b>: uno vencido (o que falta) ya es causa más grave por sí mismo, y
/// renovarlo reinicia la acreditación. «Sin confirmar» SÍ cuenta: el documento existe y viaja en el paquete de
/// acreditación (decisión del 2026-10-03), así que sin subir a la plataforma es trabajo pendiente igual.</item>
/// <item><b>Pone el Centro en «Pendiente»</b> (<see cref="Centros.EstadoCentro.Pendiente"/>) sea cual sea el tipo, pero
/// <b>bloquea a personas solo si el Centro marca el tipo como bloqueante</b> (<see cref="TipoDocumentoCentro.BloqueaAcceso"/>,
/// corrección del propietario del 2026-10-10; <see cref="ReglaBloqueoDeAcceso.AplicarPendienteEnPlataforma"/>): un RNT
/// bloqueante pendiente bloquea, una ISO opcional pendiente no. Con la marca, el de Trabajador bloquea a ese Trabajador en
/// ese Centro y el de Empresa a todos los Trabajadores de esa Empresa con Asignación activa en ese Centro. Nunca pone el
/// Centro en Bloqueado. Sin tolerancia: el Pendiente no tiene fecha a la que sumarle días.</item>
/// </list>
///
/// Es una función pura: quien consulta trae el estado de la acreditación y el del documento y la evalúa en memoria. Un
/// filtro SQL se construye con <see cref="EstadosPendientes"/>, nunca copiando la lista de estados.
/// </summary>
public static class ReglaPendienteEnPlataforma
{
    /// <summary>¿Es este estado de acreditación trabajo por hacer en la plataforma? Sin subir o subido sin validar.</summary>
    public static bool EstaPendiente(EstadoAcreditacion estado) =>
        estado is EstadoAcreditacion.PendienteDeSubir or EstadoAcreditacion.Subida;

    /// <summary>Los estados de acreditación que <see cref="EstaPendiente"/> acepta, para filtrar en SQL sin copiar la regla.</summary>
    public static IReadOnlyList<EstadoAcreditacion> EstadosPendientes { get; } =
        [.. Enum.GetValues<EstadoAcreditacion>().Where(EstaPendiente)];

    /// <summary>
    /// ¿Cuenta como Pendiente en el Centro? La acreditación está pendiente y el documento todavía vale en TALVEG (ni
    /// vencido, ni en tolerancia, ni ausente).
    /// </summary>
    public static bool CuentaEnElCentro(EstadoAcreditacion estadoAcreditacion, EstadoDocumento estadoDocumento) =>
        EstaPendiente(estadoAcreditacion)
        && estadoDocumento is not (EstadoDocumento.Vencido or EstadoDocumento.EnTolerancia or EstadoDocumento.Faltante);
}
