using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Resuelve las credenciales con las que la siembra de demo crea sus usuarios
/// y sus claves de API.
///
/// <para>
/// <b>Por qué existe este tipo.</b> Los valores por defecto de abajo son
/// constantes de compilación en un repositorio <b>público</b>, y eso era
/// deliberado y correcto mientras la siembra solo corría en la máquina del
/// desarrollador y en CI: unas credenciales conocidas hacen que arrancar y
/// depurar sea trivial, y no protegen nada. Lo que cambió no es la decisión,
/// es su premisa: en cuanto la demo se hace sobre el portal de producción,
/// esas mismas credenciales abren tenants vivos en un servidor público de
/// internet, con la contraseña legible por cualquiera que abra el repo.
/// </para>
///
/// <para>
/// De ahí la regla: <b>en Producción no hay valor por defecto</b>. O las
/// credenciales vienen de configuración —variables de entorno del despliegue,
/// igual que las claves de IA— o la siembra <b>falla al arrancar</b>. Falla,
/// no degrada: sembrar en silencio con la contraseña publicada es exactamente
/// el modo de fallo que nadie ve hasta que es tarde.
/// </para>
/// </summary>
public sealed record CredencialesDemo(string Contrasena, string ClaveApiActiva, string ClaveApiRevocada)
{
    public const string ContrasenaPorDefecto = "Prueba#2026";

    public const string ClaveApiActivaPorDefecto =
        "hydra_dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    public const string ClaveApiRevocadaPorDefecto =
        "hydra_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    /// <summary>Sección de configuración, con la convención estándar de .NET
    /// para variables de entorno: <c>DatosPrueba__Contrasena</c>.</summary>
    public const string SeccionConfiguracion = "DatosPrueba";

    /// <summary>
    /// Fuera de Producción devuelve los valores públicos por defecto, que es
    /// lo que las suites E2E y el arranque local esperan. En Producción exige
    /// los tres valores por configuración y lanza si falta alguno.
    /// </summary>
    public static CredencialesDemo Resolver(IConfiguration configuration, IHostEnvironment entorno) =>
        new(ResolverCredencial(configuration, entorno, $"{SeccionConfiguracion}:Contrasena", ContrasenaPorDefecto),
            ResolverCredencial(configuration, entorno, $"{SeccionConfiguracion}:ClaveApiActiva", ClaveApiActivaPorDefecto),
            ResolverCredencial(configuration, entorno, $"{SeccionConfiguracion}:ClaveApiRevocada", ClaveApiRevocadaPorDefecto));

    /// <summary>
    /// El guardia suelto, para las siembras que solo tienen una credencial
    /// (ver <c>SegundoTenantSeeder</c>). La regla es la misma y vive en un
    /// único sitio a propósito: el defecto no era de un sembrador concreto,
    /// era de la clase entera de «constante pública que en Producción pasa a
    /// ser una credencial viva».
    /// </summary>
    public static string ResolverCredencial(
        IConfiguration configuration, IHostEnvironment entorno, string clave, string porDefecto)
    {
        var valor = configuration[clave];

        // Una cadena en blanco no es una credencial configurada: es el error
        // de despliegue más fácil de cometer, y tratarlo como válido caería
        // al valor público justo en el único entorno donde no puede.
        if (!string.IsNullOrWhiteSpace(valor))
            return valor;

        if (entorno.IsProduction())
            throw new InvalidOperationException(
                $"La siembra está encendida en Producción pero falta «{clave}» (variable de entorno " +
                $"{clave.Replace(':', '_').Replace("_", "__")}). El valor por defecto es una constante de " +
                "un repositorio público: usarlo en Producción publicaría el acceso a los tenants " +
                "sembrados. Configúralo o apaga la siembra.");

        return porDefecto;
    }
}
