namespace CaeManager.Domain.Blindaje42;

/// <summary>
/// Respuesta de la TGSS a una <see cref="SolicitudCertificacionTgss"/> del
/// art. 42.1 ET. "Certificación negativa por descubiertos" es el término
/// legal — negativa significa que NO hay deuda (favorable a la empresa
/// principal); de ahí que <see cref="SinDescubiertos"/> sea el resultado que
/// exonera y <see cref="ConDescubiertos"/> el que no.
/// </summary>
public enum ResultadoCertificacionTgss
{
    SinDescubiertos,
    ConDescubiertos
}
