using System.Globalization;
using OpenTelemetry.Trace;

namespace CaeManager.Web.Services;

/// <summary>
/// Muestreo de las trazas OpenTelemetry (MJ-06). Sin muestreo, una carga de Mi trabajo genera del orden de 1.700 spans
/// hacia un Seq sin tope de memoria en el VPS de PostgreSQL. La fracción se decide por configuración
/// (<see cref="Clave"/>, de 0 a 1); sin valor se conserva todo, que es el comportamiento anterior. El muestreo es por
/// traza raíz y las hijas siguen a su padre, de modo que nunca se parte una traza. No toca a las métricas, que son
/// agregadas y son las que alimentan los SLI.
/// </summary>
public static class MuestreoDeTrazas
{
    public const string Clave = "Observabilidad:MuestreoTrazas";

    /// <summary>La fracción configurada; sin valor, 1. Un valor ilegible o fuera de [0, 1] lanza al arrancar.</summary>
    public static double Leer(IConfiguration configuracion)
    {
        var texto = configuracion[Clave];
        if (string.IsNullOrWhiteSpace(texto)) return 1.0;

        if (!double.TryParse(texto, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraccion)
            || double.IsNaN(fraccion) || fraccion is < 0.0 or > 1.0)
            throw new InvalidOperationException(
                $"{Clave} debe ser una fracción entre 0 y 1 con punto decimal (por ejemplo 0.1); valor recibido: '{texto}'.");

        return fraccion;
    }

    /// <summary>
    /// Muestreador por traza raíz. Los padres remotos (cabecera <c>traceparent</c> de cualquier cliente) pasan por el mismo
    /// muestreador que las raíces: si no, un cliente podría forzar la grabación (flag 01) o apagarla (flag 00).
    /// </summary>
    public static Sampler Crear(double fraccion)
    {
        Sampler raiz = fraccion >= 1.0 ? new AlwaysOnSampler()
            : fraccion <= 0.0 ? new AlwaysOffSampler()
            : new TraceIdRatioBasedSampler(fraccion);

        return new ParentBasedSampler(raiz, remoteParentSampled: raiz, remoteParentNotSampled: raiz);
    }
}
