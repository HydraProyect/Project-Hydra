using System.Globalization;

namespace CaeManager.Web.Services;

/// <summary>
/// Reloj del arnés E2E (solo Development): la hora del sistema más un desplazamiento en segundos
/// que el test escribe en un fichero. Existe porque la caducidad por inactividad
/// (<see cref="CaducidadSesion"/>) tiene un mínimo de producción de 5 minutos que ningún E2E puede
/// esperar de verdad, y rebajar ese mínimo para probarlo sería debilitar la regla que se prueba:
/// el test adelanta el reloj en vez de acortar el plazo.
///
/// <para>
/// <b>Inerte salvo configuración.</b> Sin <see cref="ClaveConfiguracion"/> no se registra nada y rige
/// <see cref="TimeProvider.System"/>, como antes. Con ella fuera de Development, el arranque lanza:
/// una clave puesta por error en un servidor real no puede mover el reloj de la sesión. Mismo
/// criterio que los demás interruptores de pruebas (<c>DatosPrueba:Activo</c>).
/// </para>
///
/// <para>
/// Solo cambia <see cref="GetUtcNow"/>; los temporizadores siguen en tiempo real. Pero sustituye el
/// <see cref="TimeProvider"/> de todo el proceso, no solo el de <see cref="CaducidadSesion"/> y
/// <see cref="ProveedorAutenticacionRevalidada"/>: también lo leen el TOTP de un solo uso, el firmante del
/// contexto RLS, la Sesión Privilegiada y los servicios de cambio de día. Un test que lo adelante no puede
/// iniciar sesión con segundo factor mientras esté adelantado, y debe devolver el reloj al terminar.
/// Un fichero ausente o ilegible deja el último desplazamiento leído (0 al empezar): nunca falla la
/// petición por culpa del arnés.
/// </para>
/// </summary>
public sealed class RelojDesplazadoPorFichero(string ruta) : TimeProvider
{
    public const string ClaveConfiguracion = "Pruebas:RelojDesplazamientoFichero";

    private long _ultimosSegundos;

    public static void Registrar(IServiceCollection servicios, IConfiguration configuracion, IHostEnvironment entorno)
    {
        var ruta = configuracion[ClaveConfiguracion];
        if (string.IsNullOrWhiteSpace(ruta))
            return;

        if (!entorno.IsDevelopment())
            throw new InvalidOperationException(
                $"{ClaveConfiguracion} solo se admite en Development: desplaza el reloj de la sesión.");

        servicios.AddSingleton<TimeProvider>(new RelojDesplazadoPorFichero(ruta));
    }

    public override DateTimeOffset GetUtcNow() =>
        TimeProvider.System.GetUtcNow().AddSeconds(Desplazamiento());

    private long Desplazamiento()
    {
        try
        {
            if (long.TryParse(File.ReadAllText(ruta).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var segundos))
                Interlocked.Exchange(ref _ultimosSegundos, segundos);
        }
        catch (IOException)
        {
            // El test está escribiendo el fichero: vale el desplazamiento anterior.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return Interlocked.Read(ref _ultimosSegundos);
    }
}
