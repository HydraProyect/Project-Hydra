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

    /// <summary>Lo que se espera turno (<see cref="LimitadorProcesosAislados"/>) antes de pedir que se reintente.</summary>
    public TimeSpan EsperaMaximaTurno { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Lo que se espera al lanzador, pasado el tiempo de la operación, a que diga si el hijo terminó. Cubre lo que tarda en
    /// matarlo y cerrar sus tuberías (unos 10 s por paso, cuatro pasos). Si no vuelve en ese margen no consta que el hijo
    /// haya muerto: el turno no se suelta y la herramienta queda desactivada en la instancia (ver
    /// <see cref="LimitadorProcesosAislados"/>). Nunca suelta el turno con el hijo quizá vivo.
    /// </summary>
    public TimeSpan MargenConfirmacionTerminado { get; init; } = TimeSpan.FromMinutes(1);

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
/// El turno de <see cref="LimitadorProcesosAislados"/> solo se suelta con el hijo terminado. Si no se puede confirmar (no se
/// pudo matar, no salió en el margen o el lanzador no volvió), el turno se retiene, el limitador queda degradado hasta
/// reiniciar la instancia y esta unión y las siguientes piden reintentar sin lanzar ningún proceso.
/// </summary>
public sealed class UnidorPdfEnProcesoAparte(
    OpcionesProcesoHerramientasPdf opciones,
    LimitadorProcesosAislados limitador,
    ILanzadorProcesoAislado lanzador,
    ILogger<UnidorPdfEnProcesoAparte> logger) : IUnidorPdf
{
    private static readonly string[] VariablesHeredadas = ["PATH", "LANG", "LC_ALL", "SystemRoot", "windir"];

    public async Task<Result<InfoPdfParaUnir>> InspeccionarAsync(byte[] contenidoPdf, CancellationToken cancellationToken)
    {
        var salida = await EjecutarAsync(ProtocoloProcesoHerramientasPdf.OperacionInspeccionar, [contenidoPdf],
            opciones.TiempoMaximoInspeccionar, ProtocoloProcesoHerramientasPdf.MaximoBytesRespuestaInspeccion, cancellationToken);
        return Traducir(salida, ProtocoloProcesoHerramientasPdf.LeerInspeccion, "inspeccionar");
    }

    public async Task<Result<PdfUnido>> UnirAsync(IReadOnlyList<byte[]> pdfs, CancellationToken cancellationToken)
    {
        var salida = await EjecutarAsync(ProtocoloProcesoHerramientasPdf.OperacionUnir, pdfs,
            opciones.TiempoMaximoUnir, ProtocoloProcesoHerramientasPdf.MaximoBytesRespuestaUnion, cancellationToken);
        return Traducir(salida, ProtocoloProcesoHerramientasPdf.LeerUnion, "unir");
    }

    /// <summary>
    /// Lo que se lanza: el anfitrión <c>dotnet</c> con el ensamblado de la aplicación en modo proceso aparte, el entorno
    /// vaciado y el límite de memoria. GC de estación y sin GC concurrente: un solo montón y sin hilos de fondo.
    /// </summary>
    internal static ProcessStartInfo Inicio(OpcionesProcesoHerramientasPdf opciones)
    {
        var inicio = new ProcessStartInfo
        {
            FileName = opciones.Anfitrion,
            ArgumentList = { opciones.RutaEnsamblado, ProcesoHerramientasPdf.Argumento },
        };

        var heredadas = VariablesHeredadas
            .Select(nombre => (Nombre: nombre, Valor: Environment.GetEnvironmentVariable(nombre)))
            .Where(variable => variable.Valor is not null)
            .ToList();
        inicio.Environment.Clear();
        foreach (var (nombre, valor) in heredadas)
            inicio.Environment[nombre] = valor;

        inicio.Environment["DOTNET_GCHeapHardLimit"] = $"0x{opciones.MemoriaMaximaBytes:X}";
        inicio.Environment["DOTNET_gcServer"] = "0";
        inicio.Environment["DOTNET_gcConcurrent"] = "0";
        return inicio;
    }

    private async Task<SalidaProcesoAislado> EjecutarAsync(byte operacion, IReadOnlyList<byte[]> pdfs, TimeSpan tiempoMaximo,
        int maximoBytesSalida, CancellationToken cancellationToken)
    {
        // Sin turno a tiempo, o con el limitador degradado, es el mismo transitorio que un presupuesto de memoria lleno: la
        // pantalla pide reintentar más tarde, y no se lanza nada.
        using var turno = await limitador.EntrarAsync(opciones.EsperaMaximaTurno, cancellationToken)
            ?? throw new PresupuestoMemoriaAgotadoException();

        var ejecucion = lanzador.EjecutarAsync(Inicio(opciones),
            (entrada, token) => ProtocoloProcesoHerramientasPdf.EscribirPeticionAsync(entrada, operacion, pdfs, token),
            tiempoMaximo, maximoBytesSalida, cancellationToken);

        try
        {
            // Sin el token del llamante: al cancelar, el lanzador mata al hijo y vuelve, y el turno se suelta cuando ya está
            // muerto. Esta espera solo acota el caso en que el lanzador no vuelve, que no confirma nada.
            return await ejecucion.WaitAsync(tiempoMaximo + opciones.MargenConfirmacionTerminado);
        }
        catch (ProcesoAisladoSinTerminarException ex)
        {
            throw Degradar(turno, ex.IdProceso, cancellationToken);
        }
        catch (TimeoutException)
        {
            // La ejecución se abandona, con su excepción observada.
            _ = ejecucion.ContinueWith(static tarea => _ = tarea.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw Degradar(turno, null, cancellationToken);
        }
    }

    /// <summary>
    /// El hijo quizá sigue vivo con su límite de memoria: su turno no se devuelve y la herramienta queda desactivada en esta
    /// instancia hasta reiniciarla. Al llamante, su cancelación si la pidió; si no, el mismo «inténtalo más tarde».
    /// </summary>
    private Exception Degradar(TurnoProcesoAislado turno, int? idProceso, CancellationToken cancellationToken)
    {
        turno.RetenerPorHijoSinConfirmar();
        // Sin nada del Documento: solo el identificador del proceso, que es lo que hace falta para buscarlo en el host.
        logger.LogCritical(
            "Unir PDF queda desactivada en esta instancia hasta reiniciarla: no se pudo confirmar que terminara el proceso aislado {IdProceso}.",
            idProceso?.ToString(CultureInfo.InvariantCulture) ?? "desconocido (el lanzador no volvió)");
        return cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(cancellationToken)
            : new PresupuestoMemoriaAgotadoException();
    }

    private Result<T> Traducir<T>(SalidaProcesoAislado salida, Func<ReadOnlyMemory<byte>, Result<T>?> leer, string operacion)
    {
        switch (salida.Desenlace)
        {
            case DesenlaceProcesoAislado.TiempoAgotado:
                logger.LogWarning("PDF rechazado al {Operacion}: el proceso aparte agotó su tiempo y se mató.", operacion);
                return Fallo<T>(ErroresUnirPdf.DemasiadoComplejo);

            case DesenlaceProcesoAislado.SalidaDemasiadoGrande:
                logger.LogWarning("PDF rechazado al {Operacion}: el proceso aparte escribió de más y se mató.", operacion);
                return Fallo<T>(ErroresUnirPdf.DemasiadoComplejo);
        }

        if (salida.CodigoSalida == ProcesoHerramientasPdf.CodigoRespuestaEscrita)
        {
            if (leer(salida.Salida) is { } resultado)
                return resultado;

            logger.LogWarning("El proceso aparte respondió algo que no cuadra al {Operacion}.", operacion);
            return Fallo<T>(ErroresUnirPdf.Ilegible);
        }

        if (salida.CodigoSalida == ProcesoHerramientasPdf.CodigoPeticionInvalida)
        {
            logger.LogWarning("El proceso aparte no entendió la petición al {Operacion}.", operacion);
            return Fallo<T>(ErroresUnirPdf.Ilegible);
        }

        // Sin memoria (su límite duro), o muerto por otra causa mientras abría un PDF ajeno: el PDF no se analiza sin riesgo.
        logger.LogWarning("PDF rechazado al {Operacion}: el proceso aparte terminó con el código {Codigo}.", operacion, salida.CodigoSalida);
        return Fallo<T>(ErroresUnirPdf.DemasiadoComplejo);
    }

    private static Result<T> Fallo<T>(string codigo) =>
        Result.Fallo<T>(Error.Crear(codigo, ProtocoloProcesoHerramientasPdf.MensajeDe(codigo)));
}
