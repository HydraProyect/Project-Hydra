using System.Diagnostics;
using System.Globalization;
using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>Los dos códigos con los que una herramienta traduce lo que el proceso aparte no respondió.</summary>
/// <param name="DemasiadoComplejo">Sin memoria, sin tiempo, escribió de más o murió: el PDF no se abre sin riesgo.</param>
/// <param name="Ilegible">Respondió algo que no cuadra o no entendió la petición.</param>
internal sealed record CodigosProcesoAparte(string DemasiadoComplejo, string Ilegible);

/// <summary>
/// Lo común de las herramientas que abren PDF ajenos en el proceso aparte (<see cref="ProcesoHerramientasPdf"/>): Unir
/// (<see cref="UnidorPdfEnProcesoAparte"/>) y Dividir (<see cref="DivisorPdfEnProcesoAparte"/>). Pide turno al
/// <see cref="LimitadorProcesosAislados"/>, lanza el hijo con el entorno vaciado y su límite duro de memoria y de tiempo, y
/// traduce lo que devuelve. Las dos herramientas comparten el mismo limitador: un solo hijo a la vez en la instancia, y si
/// uno no consta terminado, las dos quedan desactivadas hasta reiniciarla.
///
/// El turno solo se suelta cuando el lanzador confirma, en su <see cref="SeguimientoProcesoAislado"/>, que no queda nada vivo
/// de la ejecución. Si no consta, el turno se retiene, el limitador queda degradado y el llamante recibe
/// <see cref="EntradaPdfRetenidaException"/> para retener la memoria reservada para esos PDF.
/// </summary>
internal sealed class EjecutorProcesoHerramientasPdf
{
    private static readonly string[] VariablesHeredadas = ["PATH", "LANG", "LC_ALL", "SystemRoot", "windir"];

    private readonly OpcionesProcesoHerramientasPdf _opciones;
    private readonly LimitadorProcesosAislados _limitador;
    private readonly ILanzadorProcesoAislado _lanzador;
    private readonly ILogger _logger;

    /// <summary>
    /// Solo con capacidad 1. Con más, otro turno podría degradar la instancia después de que este pasara la comprobación de
    /// <see cref="LimitadorProcesosAislados.EntrarAsync"/> y antes de lanzar, y se lanzaría un proceso con la herramienta ya
    /// desactivada. Hacer atómicas la comprobación y el arranque obligaría a sostener un candado mientras el lanzador arranca
    /// el hijo, que es justo el tramo que puede bloquearse. Subir la capacidad exige rediseñar esto antes; por eso falla aquí.
    /// </summary>
    public EjecutorProcesoHerramientasPdf(OpcionesProcesoHerramientasPdf opciones, LimitadorProcesosAislados limitador,
        ILanzadorProcesoAislado lanzador, ILogger logger)
    {
        _limitador = limitador.Capacidad == 1
            ? limitador
            : throw new ArgumentOutOfRangeException(nameof(limitador), limitador.Capacidad,
                "Herramientas PDF solo admite un proceso aislado a la vez: con más, degradar la instancia no impide un lanzamiento ya en curso.");
        _opciones = opciones;
        _lanzador = lanzador;
        _logger = logger;
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

    public async Task<SalidaProcesoAislado> EjecutarAsync(Func<Stream, CancellationToken, Task> escribirPeticion,
        TimeSpan tiempoMaximo, int maximoBytesSalida, CancellationToken cancellationToken)
    {
        // Antes del turno: un fallo al preparar el arranque no tiene hijo y no debe degradar la instancia.
        var inicio = Inicio(_opciones);

        // Sin turno a tiempo, o con el limitador degradado, es el mismo transitorio que un presupuesto de memoria lleno: la
        // pantalla pide reintentar más tarde, y no se lanza nada.
        var turno = await _limitador.EntrarAsync(_opciones.EsperaMaximaTurno, cancellationToken)
            ?? throw new PresupuestoMemoriaAgotadoException();

        // El turno no va en un using: se suelta solo en el camino en que consta que no queda nada vivo de la ejecución.
        var seguimiento = new SeguimientoProcesoAislado();
        // En otro hilo: el tramo síncrono del lanzador (Process.Start, registrar la cancelación) también queda dentro de la
        // espera acotada de abajo, aunque se bloquee antes de devolver su tarea.
        // Con capacidad 1 (ver el constructor) nadie más tiene turno mientras este se usa, así que nadie puede degradar la
        // instancia entre la última comprobación de EntrarAsync y el lanzamiento: no hace falta volver a mirarlo aquí.
        var ejecucion = Task.Run(() => _lanzador.EjecutarAsync(inicio, escribirPeticion,
            tiempoMaximo, maximoBytesSalida, cancellationToken, seguimiento), CancellationToken.None);

        // Al cancelar, el lanzador mata al hijo y vuelve. Esta espera solo acota el caso en que el lanzador no vuelve, que no
        // confirma nada: sin cancelar, hasta el tiempo de la operación más su margen; cancelada, solo
        // EsperaConfirmacionAlCancelar más, porque quien cancela es el cierre del circuito, que espera 10 s y luego dispone el
        // scope. Sin lanzar: lo que decide es el seguimiento, no el tipo de excepción.
        var tope = tiempoMaximo + _opciones.MargenConfirmacionTerminado;
        var inicioEspera = Stopwatch.GetTimestamp();
        using (var alCancelar = new CancellationTokenSource())
        using (cancellationToken.Register(() => alCancelar.CancelAfter(_opciones.EsperaConfirmacionAlCancelar)))
        {
            await ((Task)ejecucion.WaitAsync(tope, alCancelar.Token))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // Si lo que venció fue el tope de siempre, y no la espera de cancelación, se decide ya, como sin cancelar.
        var restante = tope - Stopwatch.GetElapsedTime(inicioEspera);
        if (!ejecucion.IsCompleted && cancellationToken.IsCancellationRequested && restante > TimeSpan.Zero)
        {
            // Cancelada y sin confirmar aún: quien llama no espera más (el scope del circuito se va a disponer) y retiene su
            // reserva, porque la escritura del lanzador quizá sigue con los PDF. El turno no se decide aquí: se suelta si el
            // lanzador acaba confirmando la muerte del hijo antes de que venza el tope de siempre, y si no, se degrada como siempre.
            // Así una muerte lenta (de 3 a 10 s) no deja la instancia sin Unir ni Dividir hasta reiniciarla.
            _ = DecidirTurnoTrasAbandonarAsync(ejecucion, turno, seguimiento, restante);
            throw new EntradaPdfRetenidaException();
        }

        if (!seguimiento.ConstaTerminado)
        {
            // La ejecución se abandona (si no volvió), con su excepción observada.
            _ = ejecucion.ContinueWith(static tarea => _ = tarea.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw Degradar(turno, seguimiento.IdProceso);
        }

        turno.Dispose();
        // Devuelve la salida o relanza lo que lanzó el lanzador (la cancelación del llamante, entre otras), ya con el turno suelto.
        return await ejecucion;
    }

    /// <summary>
    /// Tras abandonar una ejecución cancelada: espera al lanzador, sin nadie esperando ya el resultado, lo que queda del tope
    /// de siempre. Si confirma que no queda nada vivo, suelta el turno; si no vuelve o no lo confirma, degrada como siempre. No
    /// toca nada del scope de quien llamó: solo el turno, el seguimiento y el registro, que no son del circuito.
    /// </summary>
    private async Task DecidirTurnoTrasAbandonarAsync(Task<SalidaProcesoAislado> ejecucion, TurnoProcesoAislado turno,
        SeguimientoProcesoAislado seguimiento, TimeSpan tope)
    {
        await ((Task)ejecucion.WaitAsync(tope)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (ejecucion.IsCompleted)
            _ = ejecucion.Exception;
        else
            _ = ejecucion.ContinueWith(static tarea => _ = tarea.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        if (ejecucion.IsCompleted && seguimiento.ConstaTerminado)
            turno.Dispose();
        else
            _ = Degradar(turno, seguimiento.IdProceso);
    }

    /// <summary>
    /// El hijo quizá sigue vivo con su límite de memoria, o una escritura suya retiene los PDF: su turno no se devuelve y las
    /// herramientas que usan el proceso aparte quedan desactivadas en esta instancia hasta reiniciarla. Al llamante, siempre
    /// <see cref="EntradaPdfRetenidaException"/>, también si canceló: así sabe que no debe devolver la memoria reservada
    /// para esos PDF.
    /// </summary>
    private EntradaPdfRetenidaException Degradar(TurnoProcesoAislado turno, int? idProceso)
    {
        turno.RetenerPorHijoSinConfirmar();
        try
        {
            // Sin nada del Documento: solo el identificador del proceso, que es lo que hace falta para buscarlo en el host.
            _logger.LogCritical(
                "Unir PDF y Dividir PDF quedan desactivadas en esta instancia hasta reiniciarla: no se pudo confirmar que terminara el proceso aislado {IdProceso}.",
                idProceso?.ToString(CultureInfo.InvariantCulture) ?? "desconocido (no consta que llegara a arrancar)");
        }
        catch (Exception)
        {
            // Un registro que falla no cambia el desenlace: el turno ya está retenido.
        }

        return new EntradaPdfRetenidaException();
    }

    public Result<T> Traducir<T>(SalidaProcesoAislado salida, Func<ReadOnlyMemory<byte>, Result<T>?> leer, string operacion,
        CodigosProcesoAparte codigos)
    {
        switch (salida.Desenlace)
        {
            case DesenlaceProcesoAislado.TiempoAgotado:
                _logger.LogWarning("PDF rechazado al {Operacion}: el proceso aparte agotó su tiempo y se mató.", operacion);
                return Fallo<T>(codigos.DemasiadoComplejo);

            case DesenlaceProcesoAislado.SalidaDemasiadoGrande:
                _logger.LogWarning("PDF rechazado al {Operacion}: el proceso aparte escribió de más y se mató.", operacion);
                return Fallo<T>(codigos.DemasiadoComplejo);
        }

        if (salida.CodigoSalida == ProcesoHerramientasPdf.CodigoRespuestaEscrita)
        {
            if (leer(salida.Salida) is { } resultado)
                return resultado;

            _logger.LogWarning("El proceso aparte respondió algo que no cuadra al {Operacion}.", operacion);
            return Fallo<T>(codigos.Ilegible);
        }

        if (salida.CodigoSalida == ProcesoHerramientasPdf.CodigoPeticionInvalida)
        {
            _logger.LogWarning("El proceso aparte no entendió la petición al {Operacion}.", operacion);
            return Fallo<T>(codigos.Ilegible);
        }

        // Sin memoria (su límite duro), o muerto por otra causa mientras abría un PDF ajeno: el PDF no se analiza sin riesgo.
        _logger.LogWarning("PDF rechazado al {Operacion}: el proceso aparte terminó con el código {Codigo}.", operacion, salida.CodigoSalida);
        return Fallo<T>(codigos.DemasiadoComplejo);
    }

    private static Result<T> Fallo<T>(string codigo) =>
        Result.Fallo<T>(Error.Crear(codigo, ProtocoloProcesoHerramientasPdf.MensajeDe(codigo)));
}
