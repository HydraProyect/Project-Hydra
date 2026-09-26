namespace CaeManager.Application.Common;

/// <summary>
/// Marcador: este <see cref="ICommand"/> es el acto que una Sesión Privilegiada
/// con capacidad <c>RestablecimientoSegundoFactor</c> puede ejecutar sobre su
/// Tenant objetivo (ADR-011 § 8.7, punto 3). Hoy, y por diseño, uno solo:
/// <c>RestablecerSegundoFactorCommand</c> de P0-8.
///
/// <para>
/// Mismo régimen que <see cref="IComandoDeAprovisionamiento"/>, y separado de él a
/// propósito: una sesión de Aprovisionamiento no restablece una 2FA y una de
/// restablecimiento no da de alta contenido. Una sola interfaz para las dos
/// convertiría cada capacidad en la llave de los comandos de la otra.
/// </para>
///
/// <para>
/// Sin miembros. <c>MarcadorDeRestablecimientoSegundoFactorTests</c> congela el
/// inventario: un segundo comando que lo implemente tiene que verse en la revisión
/// de ese test.
/// </para>
/// </summary>
public interface IComandoDeRestablecimientoSegundoFactor;
