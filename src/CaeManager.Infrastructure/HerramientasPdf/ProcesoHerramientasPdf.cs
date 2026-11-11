using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// El lado del proceso aparte: <c>dotnet CaeManager.Web.dll --proceso-herramientas-pdf</c>. Program.cs entra aquí antes de
/// construir la aplicación (sin configuración, sin base de datos, sin registro), lee una petición de la entrada estándar,
/// abre los PDF con <see cref="PdfSharpUnidorPdf"/> y escribe la respuesta en la salida estándar
/// (<see cref="ProtocoloProcesoHerramientasPdf"/>). Corre con el entorno vacío y el límite de memoria que le pone
/// <see cref="UnidorPdfEnProcesoAparte"/>, que además lo mata si pasa de su tiempo.
/// </summary>
public static class ProcesoHerramientasPdf
{
    public const string Argumento = "--proceso-herramientas-pdf";

    public const int CodigoRespuestaEscrita = 0;

    /// <summary>La petición no cuadra con el protocolo: un fallo de TALVEG, no del PDF.</summary>
    public const int CodigoPeticionInvalida = 2;

    /// <summary>El análisis agotó el límite de memoria del proceso.</summary>
    public const int CodigoSinMemoria = 3;

    /// <summary>
    /// En Linux, se ofrece como primera víctima del OOM killer: el límite de .NET acota su montón, no el runtime ni la memoria
    /// nativa, y el proceso comparte el techo de memoria del contenedor con el proceso web. Si el contenedor se queda sin
    /// memoria, que muera este y no la aplicación. Subir el propio valor no necesita privilegios. Solo lo llama Program.cs al
    /// entrar en modo proceso aparte, nunca un test que corre dentro de su propio proceso.
    /// </summary>
    public static void PreferirComoVictimaDeFaltaDeMemoria()
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            File.WriteAllText("/proc/self/oom_score_adj", "1000");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mejor esfuerzo: sin esto sigue habiendo límite de montón y de tiempo.
        }
    }

    public static int Ejecutar(Stream entrada, Stream salida)
    {
        try
        {
            var peticion = ProtocoloProcesoHerramientasPdf.LeerPeticion(entrada);
            if (peticion is null)
                return CodigoPeticionInvalida;

            var motor = new PdfSharpUnidorPdf(NullLogger<PdfSharpUnidorPdf>.Instance);
            if (peticion.Operacion == ProtocoloProcesoHerramientasPdf.OperacionInspeccionar)
                ProtocoloProcesoHerramientasPdf.EscribirInspeccion(salida, motor.Inspeccionar(peticion.Pdfs[0]));
            else
                ProtocoloProcesoHerramientasPdf.EscribirUnion(salida, motor.Unir(peticion.Pdfs));

            return CodigoRespuestaEscrita;
        }
        catch (OutOfMemoryException)
        {
            // El motor deja pasar la falta de memoria a propósito: aquí es el límite duro haciendo su trabajo.
            return CodigoSinMemoria;
        }
    }
}
