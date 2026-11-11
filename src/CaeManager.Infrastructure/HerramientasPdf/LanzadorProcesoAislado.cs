using System.Diagnostics;
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
/// Lanza un proceso hijo, le escribe la entrada estándar, lee su salida estándar con tope y lo mata (con su árbol) si pasa
/// de su tiempo, si escribe de más o si el llamante cancela. Separado de quien decide qué proceso lanzar para poder probar
/// esa decisión con un doble y esta mecánica con un proceso real.
/// </summary>
public interface ILanzadorProcesoAislado
{
    /// <exception cref="OperationCanceledException">Si cancela el llamante; el proceso ya está muerto, salvo que no se pudiera matar (queda en el registro).</exception>
    Task<SalidaProcesoAislado> EjecutarAsync(ProcessStartInfo inicio, Func<Stream, CancellationToken, Task> escribirEntrada,
        TimeSpan tiempoMaximo, int maximoBytesSalida, CancellationToken cancellationToken);
}

/// <summary>
/// La mecánica real con <see cref="Process"/>, en la línea de <c>LibreOfficeConversorWordPdfService</c>: se mata siempre el
/// árbol entero y se espera a que muera antes de devolver el control. La salida de errores se lee y se descarta, porque la
/// traza de un fallo del hijo puede reproducir contenido del documento.
///
/// Si el sistema no deja matar al hijo, no se espera para siempre: cada espera tras el plazo está acotada, se registra un
/// aviso y se devuelve el control con el hijo quizá vivo. Retener al llamante (y su turno) bloquearía la herramienta para
/// todos; a cambio, la cota de memoria de <see cref="LimitadorProcesosAislados"/> deja de valer para ese hijo.
/// </summary>
public sealed class LanzadorProcesoAislado(ILogger<LanzadorProcesoAislado> logger) : ILanzadorProcesoAislado
{
    private static readonly TimeSpan EsperaDeMuerte = TimeSpan.FromSeconds(10);

    /// <summary>Para los tests: el identificador del hijo en cuanto arranca, también si luego se cancela.</summary>
    internal Action<int>? AlArrancar { get; init; }

    public async Task<SalidaProcesoAislado> EjecutarAsync(ProcessStartInfo inicio, Func<Stream, CancellationToken, Task> escribirEntrada,
        TimeSpan tiempoMaximo, int maximoBytesSalida, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        inicio.UseShellExecute = false;
        inicio.RedirectStandardInput = true;
        inicio.RedirectStandardOutput = true;
        inicio.RedirectStandardError = true;

        using var proceso = new Process { StartInfo = inicio };
        proceso.Start();
        var id = proceso.Id;
        AlArrancar?.Invoke(id);

        // Sin el token del llamante: estas tareas terminan solas cuando el proceso muere y sus tuberías se cierran.
        var errores = proceso.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);

        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Al vencer el plazo o cancelar el llamante se mata el árbol en el acto, sin esperar a que la lectura vea el token:
        // una lectura ya bloqueada en la tubería no siempre se interrumpe, y un hijo que gira sin escribir la dejaría
        // esperando. Con el hijo muerto la tubería se cierra y la lectura vuelve.
        using var matarAlVencer = plazo.Token.Register(() => MatarArbolSinEsperar(proceso));
        plazo.CancelAfter(tiempoMaximo);
        var escritura = EscribirAsync(proceso.StandardInput.BaseStream, escribirEntrada, plazo.Token);
        var lectura = LeerConTopeAsync(proceso.StandardOutput.BaseStream, maximoBytesSalida, plazo.Token);

        try
        {
            var salida = await HastaElPlazoAsync(lectura, plazo.Token);
            if (salida is null)
                return new SalidaProcesoAislado(DesenlaceProcesoAislado.SalidaDemasiadoGrande, null, ReadOnlyMemory<byte>.Empty, id);

            await proceso.WaitForExitAsync(plazo.Token);
            // La lectura pudo volver porque el plazo mató al hijo justo antes: eso no es terminar solo.
            plazo.Token.ThrowIfCancellationRequested();
            return new SalidaProcesoAislado(DesenlaceProcesoAislado.Terminado, proceso.ExitCode, salida.Value, id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SalidaProcesoAislado(DesenlaceProcesoAislado.TiempoAgotado, null, ReadOnlyMemory<byte>.Empty, id);
        }
        finally
        {
            // También al cancelar el llamante o ante cualquier excepción: no se deja un hijo vivo con el PDF abierto.
            await MatarArbolAsync(proceso);
            await ObservarAsync(lectura);
            await ObservarAsync(escritura);
            await ObservarAsync(errores);
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
    /// siempre vuelve y quien llama suelta su turno.
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

    private static void MatarArbolSinEsperar(Process proceso)
    {
        try
        {
            if (!proceso.HasExited)
                proceso.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Lo reintenta y lo registra MatarArbolAsync en el finally.
        }
    }

    private async Task MatarArbolAsync(Process proceso)
    {
        try
        {
            if (proceso.HasExited)
                return;

            proceso.Kill(entireProcessTree: true);
            using var espera = new CancellationTokenSource(EsperaDeMuerte);
            await proceso.WaitForExitAsync(espera.Token);
        }
        catch (Exception ex)
        {
            // Nunca se deja escapar: llegamos aquí ya cancelando o agotando el tiempo, y sustituir ese motivo por un fallo al
            // matar perdería la causa real.
            logger.LogWarning(ex, "No se pudo terminar un proceso aislado ({Id}); puede haber quedado huérfano.", proceso.Id);
        }
    }

    private static async Task ObservarAsync(Task tarea)
    {
        try
        {
            await tarea.WaitAsync(EsperaDeMuerte);
        }
        catch (Exception)
        {
            // Solo se observa para no dejar excepciones sin observar; el desenlace ya está decidido.
        }
    }
}
