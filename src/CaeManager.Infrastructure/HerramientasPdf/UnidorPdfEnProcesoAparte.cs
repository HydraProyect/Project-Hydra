using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Límites del proceso aparte que abre PDF ajenos. Van en código y no en configuración: cambiarlos cambia cuánto puede
/// gastar un PDF hostil, y eso se revisa en una PR.
/// </summary>
public sealed record OpcionesProcesoHerramientasPdf
{
    /// <summary>
    /// Límite duro del montón de .NET del proceso aparte (<c>DOTNET_GCHeapHardLimit</c>): una asignación que lo pase lanza
    /// <see cref="OutOfMemoryException"/> en ese proceso. Una unión de 50 MB y 500 páginas cabe con holgura; el contenedor
    /// tiene 3 GB y el proceso web sigue con lo suyo.
    /// </summary>
    public long MemoriaMaximaBytes { get; init; } = 512L * 1024 * 1024;

    public TimeSpan TiempoMaximoInspeccionar { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan TiempoMaximoUnir { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Abrir un PDF para dividirlo: contar sus páginas y ver si lleva firma.</summary>
    public TimeSpan TiempoMaximoAbrirParaDividir { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Abrir el original de nuevo y escribir un tramo de hasta vez y media su tamaño.</summary>
    public TimeSpan TiempoMaximoExtraerTramo { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Lo que se espera turno (<see cref="LimitadorProcesosAislados"/>) antes de pedir que se reintente.</summary>
    public TimeSpan EsperaMaximaTurno { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Lo que se espera al lanzador, pasado el tiempo de la operación, a que diga si el hijo terminó. Cubre lo que tarda en
    /// matarlo y cerrar sus tuberías (unos 10 s por paso, cuatro pasos). Si no vuelve en ese margen no consta que el hijo
    /// haya muerto: el turno no se suelta y la herramienta queda desactivada en la instancia (ver
    /// <see cref="LimitadorProcesosAislados"/>). Nunca suelta el turno con el hijo quizá vivo.
    /// </summary>
    public TimeSpan MargenConfirmacionTerminado { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Lo que se espera al lanzador, una vez cancelada la operación, a que confirme que el hijo murió. Al cancelar, el
    /// lanzador mata el árbol del hijo en el acto (<c>Kill(entireProcessTree: true)</c>), y lo normal es confirmarlo en
    /// milisegundos, aunque el lanzador se da hasta 10 s por paso. Tiene que quedar bastante por debajo de lo que el cierre del
    /// circuito espera a la operación en vuelo (10 s, <c>LiberacionDeAccesoADatosAlCerrarCircuito.EsperaMaxima</c>): si no se
    /// confirma en este plazo, quien llama recibe <see cref="EntradaPdfRetenidaException"/> sin esperar más, antes de que el
    /// scope del circuito se disponga, y retiene su reserva. El turno se decide después, sin él: se suelta si el lanzador
    /// acaba confirmando la muerte dentro de <see cref="MargenConfirmacionTerminado"/>, y si no, la herramienta se desactiva.
    /// </summary>
    public TimeSpan EsperaConfirmacionAlCancelar { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>El ejecutable <c>dotnet</c> del runtime que ejecuta esta aplicación.</summary>
    public string Anfitrion { get; init; } = AnfitrionDotnet();

    /// <summary>El ensamblado de la aplicación, que sabe entrar en <see cref="ProcesoHerramientasPdf"/>.</summary>
    public string RutaEnsamblado { get; init; } = Assembly.GetEntryAssembly()?.Location ?? string.Empty;

    /// <summary>
    /// El runtime vive en <c>&lt;raíz&gt;/shared/Microsoft.NETCore.App/&lt;versión&gt;/</c> y el anfitrión en la raíz:
    /// así se encuentra el mismo <c>dotnet</c> en el contenedor, en CI y en local sin depender del PATH.
    /// </summary>
    internal static string AnfitrionDotnet()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;
        var raiz = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        return Path.Combine(raiz, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }
}

/// <summary>
/// <see cref="IUnidorPdf"/> del proceso web: no abre ningún PDF, lo manda a un proceso aparte
/// (<see cref="ProcesoHerramientasPdf"/>) con un límite duro de memoria y de tiempo, y traduce lo que devuelve a los mismos
/// códigos de <see cref="ErroresUnirPdf"/>. Así un PDF hostil (una bomba que pase el <see cref="AcotadorEstructuraPdf"/>)
/// agota ese proceso, que muere o se mata, y la aplicación responde «demasiado complejo» y sigue viva.
///
/// El proceso aparte no hereda el entorno (cadenas de conexión, claves): solo lo que necesita el runtime. No es un entorno
/// cerrado: corre con el mismo usuario y en el mismo contenedor. Lo que acota es el coste, que es el riesgo de abrir un PDF
/// con PDFsharp, código administrado.
///
/// El turno, el lanzamiento y la degradación son los de <see cref="EjecutorProcesoHerramientasPdf"/>, compartidos con Dividir:
/// si un hijo no consta terminado, el llamante recibe <see cref="EntradaPdfRetenidaException"/> para retener la memoria
/// reservada para esos PDF, y las uniones siguientes piden reintentar sin lanzar ningún proceso.
/// </summary>
public sealed class UnidorPdfEnProcesoAparte(
    OpcionesProcesoHerramientasPdf opciones,
    LimitadorProcesosAislados limitador,
    ILanzadorProcesoAislado lanzador,
    ILogger<UnidorPdfEnProcesoAparte> logger) : IUnidorPdf
{
    private static readonly CodigosProcesoAparte Codigos = new(ErroresUnirPdf.DemasiadoComplejo, ErroresUnirPdf.Ilegible);

    private readonly EjecutorProcesoHerramientasPdf _ejecutor = new(opciones, limitador, lanzador, logger);

    public async Task<Result<InfoPdfParaUnir>> InspeccionarAsync(byte[] contenidoPdf, CancellationToken cancellationToken)
    {
        var salida = await EjecutarAsync(ProtocoloProcesoHerramientasPdf.OperacionInspeccionar, [contenidoPdf],
            opciones.TiempoMaximoInspeccionar, ProtocoloProcesoHerramientasPdf.MaximoBytesRespuestaInspeccion, cancellationToken);
        return _ejecutor.Traducir(salida, ProtocoloProcesoHerramientasPdf.LeerInspeccion, "inspeccionar", Codigos);
    }

    public async Task<Result<PdfUnido>> UnirAsync(IReadOnlyList<byte[]> pdfs, CancellationToken cancellationToken)
    {
        var salida = await EjecutarAsync(ProtocoloProcesoHerramientasPdf.OperacionUnir, pdfs,
            opciones.TiempoMaximoUnir, ProtocoloProcesoHerramientasPdf.MaximoBytesRespuestaUnion, cancellationToken);
        return _ejecutor.Traducir(salida, ProtocoloProcesoHerramientasPdf.LeerUnion, "unir", Codigos);
    }

    /// <summary>Lo que se lanza: ver <see cref="EjecutorProcesoHerramientasPdf.Inicio"/>.</summary>
    internal static ProcessStartInfo Inicio(OpcionesProcesoHerramientasPdf opciones) => EjecutorProcesoHerramientasPdf.Inicio(opciones);

    private Task<SalidaProcesoAislado> EjecutarAsync(byte operacion, IReadOnlyList<byte[]> pdfs, TimeSpan tiempoMaximo,
        int maximoBytesSalida, CancellationToken cancellationToken) =>
        _ejecutor.EjecutarAsync(
            (entrada, token) => ProtocoloProcesoHerramientasPdf.EscribirPeticionAsync(entrada, operacion, pdfs, token),
            tiempoMaximo, maximoBytesSalida, cancellationToken);
}