using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.HerramientasPdf;

public enum DesenlaceProcesoAislado
{
    /// <summary>El proceso terminó solo; su código de salida dice cómo.</summary>
    Terminado,

    /// <summary>Pasó de su tiempo y se mató su árbol de procesos.</summary>
    TiempoAgotado,

    /// <summary>Escribió más de lo que se le admite y se mató su árbol de procesos.</summary>
    SalidaDemasiadoGrande,
}

/// <param name="CodigoSalida">Solo si <see cref="DesenlaceProcesoAislado.Terminado"/>.</param>
/// <param name="Salida">Lo que escribió en la salida estándar; vacío si no terminó solo.</param>
/// <param name="IdProceso">Para el registro y para que los tests comprueben que ya no existe.</param>
public sealed record SalidaProcesoAislado(DesenlaceProcesoAislado Desenlace, int? CodigoSalida, ReadOnlyMemory<byte> Salida, int IdProceso);

/// <summary>
/// El lanzador no pudo confirmar que el hijo terminara: falló <see cref="Process.Kill(bool)"/>, el hijo no salió en el margen
/// tras matarlo o una de sus tuberías no acabó (una escritura pendiente retiene la entrada). Puede seguir vivo con su límite de memoria, así que quien tenga su turno no debe soltarlo
/// (<see cref="TurnoProcesoAislado.RetenerPorHijoSinConfirmar"/>). Sustituye a cualquier otro
/// desenlace, también a la cancelación del llamante.
/// </summary>
public sealed class ProcesoAisladoSinTerminarException(int idProceso)
    : Exception($"No se pudo confirmar que terminara el proceso aislado {idProceso}.")
{
    public int IdProceso { get; } = idProceso;
}

/// <summary>
/// Lo que el lanzador publica de una ejecución mientras ocurre, para quien la supervisa sin esperar a que vuelva: el
/// identificador del hijo en cuanto arranca y la confirmación positiva de que ya no queda nada vivo de ella. Sin esa
/// confirmación, quien tenga su turno no lo suelta, aunque el lanzador haya devuelto una excepción o no haya vuelto.
/// </summary>
public sealed class SeguimientoProcesoAislado
{
    private int _idProceso;
    private int _constaTerminado;

    /// <summary>El hijo, en cuanto <see cref="Process.Start()"/> vuelve; <c>null</c> si no llegó a arrancar.</summary>
    public int? IdProceso => Volatile.Read(ref _idProceso) is var id and not 0 ? id : null;

    /// <summary>
    /// Verdadero solo si consta que no queda nada vivo de la ejecución: el hijo no llegó a arrancar, o salió y sus tuberías
    /// terminaron, de modo que ninguna escritura pendiente retiene la entrada. Falso mientras no conste, sea cual sea el motivo.
    /// </summary>
    public bool ConstaTerminado => Volatile.Read(ref _constaTerminado) == 1;

    public void Arrancado(int idProceso) => Volatile.Write(ref _idProceso, idProceso);

    /// <summary>Lo llama solo quien sabe que no queda nada vivo: el lanzador tras confirmarlo, o quien no llegó a lanzar.</summary>
    public void ConfirmarTerminado() => Volatile.Write(ref _constaTerminado, 1);
}

/// <summary>
/// Lanza un proceso hijo, le escribe la entrada estándar, lee su salida estándar con tope y lo mata (con su árbol) si pasa
/// de su tiempo, si escribe de más o si el llamante cancela. Separado de quien decide qué proceso lanzar para poder probar
/// esa decisión con un doble y esta mecánica con un proceso real.
/// </summary>
public interface ILanzadorProcesoAislado
{
    /// <param name="seguimiento">
    /// Donde se publica el identificador del hijo al arrancar y, solo si consta, que terminó (<see cref="SeguimientoProcesoAislado.ConfirmarTerminado"/>):
    /// también si luego se lanza una excepción. Quien retiene un turno por la ejecución decide con él, no con el tipo de excepción.
    /// </param>
    /// <exception cref="OperationCanceledException">Si cancela el llamante; el proceso ya está muerto.</exception>
    /// <exception cref="ProcesoAisladoSinTerminarException">Si no se pudo confirmar que el proceso terminara, sea cual sea el desenlace.</exception>
    Task<SalidaProcesoAislado> EjecutarAsync(ProcessStartInfo inicio, Func<Stream, CancellationToken, Task> escribirEntrada,
        TimeSpan tiempoMaximo, int maximoBytesSalida, CancellationToken cancellationToken, SeguimientoProcesoAislado? seguimiento = null);
}

/// <summary>
/// La mecánica real con <see cref="Process"/>, en la línea de <c>LibreOfficeConversorWordPdfService</c>: se mata siempre el
/// árbol entero y se espera a que muera antes de devolver el control. La salida de errores se lee y se descarta, porque la
/// traza de un fallo del hijo puede reproducir contenido del documento.
///
/// Si el sistema no deja matar al hijo, no se espera para siempre: cada espera tras el plazo está acotada y se devuelve el
/// control con <see cref="ProcesoAisladoSinTerminarException"/>, para que quien llama retenga su turno: la cota de memoria de
/// <see cref="LimitadorProcesosAislados"/> no vale con ese hijo quizá vivo.
/// </summary>
public sealed class LanzadorProcesoAislado(ILogger<LanzadorProcesoAislado> logger) : ILanzadorProcesoAislado
{
    /// <summary>Lo que se espera a que el hijo salga tras matarlo, y a cada tarea de sus tuberías. Para los tests, más corto.</summary>
    internal TimeSpan EsperaDeMuerte { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Para los tests: el identificador del hijo en cuanto arranca, también si luego se cancela.</summary>
    internal Action<int>? AlArrancar { get; init; }

    /// <summary>Para los tests: sustituye a matar el árbol, para simular un sistema que no deja matar al hijo.</summary>
    internal Action<Process>? Matar { get; init; }

    public async Task<SalidaProcesoAislado> EjecutarAsync(ProcessStartInfo inicio, Func<Stream, CancellationToken, Task> escribirEntrada,
        TimeSpan tiempoMaximo, int maximoBytesSalida, CancellationToken cancellationToken, SeguimientoProcesoAislado? seguimiento = null)
    {
        seguimiento ??= new SeguimientoProcesoAislado();
        if (cancellationToken.IsCancellationRequested)
        {
            seguimiento.ConfirmarTerminado();
            cancellationToken.ThrowIfCancellationRequested();
        }

        inicio.UseShellExecute = false;
        inicio.RedirectStandardInput = true;
        inicio.RedirectStandardOutput = true;
        inicio.RedirectStandardError = true;

        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var proceso = new Process { StartInfo = inicio };
        try
        {
            proceso.Start();
        }
        catch (Exception) when (!HaArrancado(proceso))
        {
            // Sin hijo (el ejecutable no existe, sin permiso): no queda nada vivo que retenga el turno.
            seguimiento.ConfirmarTerminado();
            throw;
        }

        var id = proceso.Id;
        seguimiento.Arrancado(id);

        Task errores = Task.CompletedTask, escritura = Task.CompletedTask, lectura = Task.CompletedTask;
        var matarAlVencer = default(CancellationTokenRegistration);
        SalidaProcesoAislado? desenlace = null;
        ExceptionDispatchInfo? fallo = null;
        // Todo lo que sigue a Start va dentro: cualquier fallo, también antes de leer, pasa por confirmar la muerte del hijo.
        try
        {
            AlArrancar?.Invoke(id);

            // Sin el token del llamante: estas tareas terminan solas cuando el proceso muere y sus tuberías se cierran.
            errores = proceso.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);

            // Al vencer el plazo o cancelar el llamante se mata el árbol en el acto, sin esperar a que la lectura vea el
            // token: una lectura ya bloqueada en la tubería no siempre se interrumpe, y un hijo que gira sin escribir la
            // dejaría esperando. Con el hijo muerto la tubería se cierra y la lectura vuelve.
            matarAlVencer = plazo.Token.Register(() => MatarArbolSinEsperar(proceso));
            plazo.CancelAfter(tiempoMaximo);
            escritura = EscribirAsync(proceso.StandardInput.BaseStream, escribirEntrada, plazo.Token);
            var leyendo = LeerConTopeAsync(proceso.StandardOutput.BaseStream, maximoBytesSalida, plazo.Token);
            lectura = leyendo;

            desenlace = await DesenlaceAsync(proceso, leyendo, plazo.Token, cancellationToken, id);
        }
        catch (Exception ex)
        {
            // Incluida la cancelación del llamante: se relanza tal cual, pero solo después de confirmar la muerte del hijo.
            fallo = ExceptionDispatchInfo.Capture(ex);
        }

        // También al cancelar el llamante o ante cualquier excepción: no se deja un hijo vivo con el PDF abierto. Solo cuenta
        // como terminado lo que se confirma: el hijo salió y cada tubería acabó. Una escritura que sigue pendiente retiene la
        // entrada (los PDF) y quizá la tubería sigue abierta en otro proceso: no consta terminado.
        var terminado = await MatarArbolAsync(proceso, id);
        var tuberias = await TerminaEnAsync(lectura, EsperaDeMuerte);
        tuberias &= await TerminaEnAsync(escritura, EsperaDeMuerte);
        tuberias &= await TerminaEnAsync(errores, EsperaDeMuerte);
        matarAlVencer.Dispose();

        // Sustituye al desenlace que hubiera: sin la muerte confirmada, el turno de quien llama no puede soltarse.
        if (!(terminado && tuberias))
            throw new ProcesoAisladoSinTerminarException(id);

        seguimiento.ConfirmarTerminado();
        fallo?.Throw();
        return desenlace!;
    }

    /// <summary>Si <see cref="Process.Start()"/> lanzó habiendo creado ya el hijo, este sigue ahí y no consta terminado.</summary>
    private static bool HaArrancado(Process proceso)
    {
        try
        {
            _ = proceso.Id;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<SalidaProcesoAislado> DesenlaceAsync(Process proceso, Task<ReadOnlyMemory<byte>?> lectura,
        CancellationToken plazo, CancellationToken cancellationToken, int id)
    {
        try
        {
            var salida = await HastaElPlazoAsync(lectura, plazo);
            if (salida is null)
                return new SalidaProcesoAislado(DesenlaceProcesoAislado.SalidaDemasiadoGrande, null, ReadOnlyMemory<byte>.Empty, id);

            await proceso.WaitForExitAsync(plazo);
            // La lectura pudo volver porque el plazo mató al hijo justo antes: eso no es terminar solo.
            plazo.ThrowIfCancellationRequested();
            return new SalidaProcesoAislado(DesenlaceProcesoAislado.Terminado, proceso.ExitCode, salida.Value, id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SalidaProcesoAislado(DesenlaceProcesoAislado.TiempoAgotado, null, ReadOnlyMemory<byte>.Empty, id);
        }
    }

    private static async Task EscribirAsync(Stream entrada, Func<Stream, CancellationToken, Task> escribirEntrada, CancellationToken cancellationToken)
    {
        try
        {
            await escribirEntrada(entrada, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // El hijo murió o cerró la entrada antes de leerla entera: su desenlace lo dice el código de salida.
        }
        finally
        {
            try
            {
                entrada.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // La tubería ya estaba rota.
            }
        }
    }

    /// <summary>
    /// <paramref name="lectura"/>, sin esperarla más allá de <paramref name="plazo"/>: si vence o el llamante cancela, lanza
    /// <see cref="OperationCanceledException"/> en el acto aunque la lectura no atienda el token (bloqueada en la tubería de
    /// un hijo que no se pudo matar). La lectura se abandona y la observa el finally con espera acotada; así el lanzador
    /// siempre vuelve y quien llama sabe si puede soltar su turno.
    /// </summary>
    internal static Task<T> HastaElPlazoAsync<T>(Task<T> lectura, CancellationToken plazo) => lectura.WaitAsync(plazo);

    /// <summary>La salida entera, o <c>null</c> en cuanto pasa de <paramref name="maximo"/> bytes.</summary>
    internal static async Task<ReadOnlyMemory<byte>?> LeerConTopeAsync(Stream origen, int maximo, CancellationToken cancellationToken)
    {
        using var leido = new MemoryStream();
        var bloque = new byte[81_920];
        int bytes;
        while ((bytes = await origen.ReadAsync(bloque, cancellationToken)) > 0)
        {
            if (leido.Length + bytes > maximo)
                return null;
            leido.Write(bloque, 0, bytes);
        }

        // Sin copiar: el búfer queda vivo mientras lo retenga quien lea la respuesta.
        return new ReadOnlyMemory<byte>(leido.GetBuffer(), 0, (int)leido.Length);
    }

    private void MatarArbolSinEsperar(Process proceso)
    {
        try
        {
            if (!proceso.HasExited)
                MatarArbol(proceso);
        }
        catch (Exception)
        {
            // Lo reintenta y lo registra MatarArbolAsync al confirmar la muerte.
        }
    }

    private void MatarArbol(Process proceso)
    {
        if (Matar is { } matar)
            matar(proceso);
        else
            proceso.Kill(entireProcessTree: true);
    }

    private Task<bool> MatarArbolAsync(Process proceso, int id) =>
        ConfirmarTerminadoAsync(
            () => proceso.HasExited,
            () => MatarArbol(proceso),
            proceso.WaitForExitAsync,
            EsperaDeMuerte,
            ex => logger.LogWarning(ex, "No se pudo confirmar que terminara un proceso aislado ({Id}); puede seguir vivo.", id));

    /// <summary>
    /// <c>true</c> solo si el hijo consta terminado: ya había salido, o salió dentro de <paramref name="margen"/> tras
    /// <paramref name="matar"/>. Lo que cuenta es la salida, no que matar no falle: un hijo que ya estaba saliendo puede hacer
    /// que matar lance, y entonces la espera vuelve en el acto. Los dos <c>true</c> son los únicos caminos confirmados; todo
    /// lo demás, incluido un fallo al registrar, es <c>false</c>. Ningún fallo se deja escapar, porque llegamos aquí ya
    /// cancelando o agotando el tiempo y el motivo real lo decide el llamante.
    /// </summary>
    internal static async Task<bool> ConfirmarTerminadoAsync(Func<bool> haTerminado, Action matar,
        Func<CancellationToken, Task> esperarSalida, TimeSpan margen, Action<Exception> registrar)
    {
        Exception? motivo = null;
        try
        {
            if (haTerminado())
                return true;

            try
            {
                matar();
            }
            catch (Exception ex)
            {
                motivo = ex;
            }

            using var espera = new CancellationTokenSource(margen);
            await esperarSalida(espera.Token);
            return true;
        }
        catch (Exception ex)
        {
            motivo ??= ex;
        }

        try
        {
            registrar(motivo);
        }
        catch (Exception)
        {
            // Que el registro falle no cambia nada: el hijo sigue sin constar terminado.
        }

        return false;
    }

    /// <summary>
    /// <c>true</c> si <paramref name="tarea"/> termina (bien o con excepción) dentro de <paramref name="espera"/>. Si no, se
    /// abandona con una continuación que observa su excepción cuando llegue, y <c>false</c>: sigue viva y puede retener lo que
    /// referencia.
    /// </summary>
    internal static async Task<bool> TerminaEnAsync(Task tarea, TimeSpan espera)
    {
        try
        {
            await tarea.WaitAsync(espera);
            return true;
        }
        catch (Exception) when (tarea.IsCompleted)
        {
            // Terminó, quizá con excepción, y el desenlace lo decide el llamante. Lo capturado puede ser el vencimiento de la
            // espera si la tarea falló justo después: su excepción se observa aquí igualmente, para que no quede sin observar.
            _ = tarea.Exception;
            return true;
        }
        catch (Exception)
        {
            _ = tarea.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return false;
        }
    }
}
