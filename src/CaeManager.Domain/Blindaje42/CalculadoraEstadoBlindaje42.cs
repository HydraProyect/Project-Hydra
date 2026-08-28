namespace CaeManager.Domain.Blindaje42;

/// <summary>
/// Calcula el <see cref="EstadoBlindaje42"/> de una solicitud, a partir de su
/// resultado (si ya llegó) y del transcurso del plazo de 30 días. Lógica
/// pura, nunca almacenada — mismo patrón que <c>CalculadoraEstadoSupervision</c>.
/// </summary>
public static class CalculadoraEstadoBlindaje42
{
    public static EstadoBlindaje42 Calcular(ResultadoCertificacionTgss? resultado, DateOnly fechaSolicitud, DateOnly hoy)
    {
        if (resultado == ResultadoCertificacionTgss.ConDescubiertos)
            return EstadoBlindaje42.NoExonerada;

        if (resultado == ResultadoCertificacionTgss.SinDescubiertos)
            return EstadoBlindaje42.ExoneradaPorCertificacion;

        var fechaLimite = fechaSolicitud.AddDays(SolicitudCertificacionTgss.PlazoDiasTgss);
        return hoy > fechaLimite ? EstadoBlindaje42.ExoneradaPorSilencio : EstadoBlindaje42.PendienteRespuesta;
    }
}
