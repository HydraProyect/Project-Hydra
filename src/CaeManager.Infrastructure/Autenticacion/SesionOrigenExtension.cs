using System.Security.Claims;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// Las marcas de la sesión de origen que viajan en el token de la extensión
/// (<see cref="TokenSesionExtension"/>): su hora de inicio y su última interacción conocida al
/// emitir, las mismas que lleva la cookie de esa sesión, y el plazo de inactividad que regía al
/// emitir. Ese plazo se guarda para que ampliarlo después no reviva un token cuya sesión de origen
/// ya había vencido: rige el menor entre él y el vigente.
/// </summary>
public sealed record MarcasSesionOrigen(DateTimeOffset Inicio, DateTimeOffset UltimaInteraccion, TimeSpan PlazoInactividad);

/// <summary>
/// La vigencia de la sesión de origen de un token de extensión (issue #1891, ASVS 4.0.3 V3.3.2):
/// el token no sobrevive a la sesión interactiva de la que salió, ni a su tope desde el inicio
/// ni a su plazo de inactividad.
///
/// <para>
/// La regla la decide la caducidad de la cookie, en la capa web (<c>CaducidadSesion</c>), y aquí
/// solo se pregunta: así no hay un segundo sitio que decida lo mismo y pueda dejar de coincidir.
/// </para>
///
/// <para>
/// <b>Usar la extensión no es una interacción.</b> Nada de lo que hace el token mueve la última
/// interacción de la sesión de origen; solo la mueve la señal de la propia interfaz web. Si la
/// sesión de origen deja de tener actividad, el token deja de valer cuando vence su plazo,
/// aunque la extensión se siga usando.
/// </para>
/// </summary>
public interface ISesionOrigenExtension
{
    /// <summary>
    /// Las marcas de la sesión interactiva <paramref name="sesion"/> si ahora sigue vigente, o
    /// <c>null</c> si no: sin hora de inicio legible, pasada de su tope o caducada por inactividad.
    /// Es lo que el emisor guarda en el token.
    /// </summary>
    MarcasSesionOrigen? MarcasVigentes(ClaimsPrincipal sesion);

    /// <summary>
    /// ¿Sigue vigente ahora la sesión de origen <paramref name="sid"/> con las
    /// <paramref name="marcas"/> del token? El plazo de inactividad es el menor entre el de las
    /// marcas, que regía al emitir, y el de <paramref name="cuenta"/>, el principal recién
    /// construido desde la base para la cuenta del token, que lo lleva de su Tenant propietario;
    /// nunca uno que traiga la petición. Así acortarlo rige ya y ampliarlo no revive el token.
    /// </summary>
    bool SigueVigente(ClaimsPrincipal cuenta, Guid sid, MarcasSesionOrigen marcas);
}
