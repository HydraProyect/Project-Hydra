namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Puente entre las interfaces síncronas de Data Protection
/// (<see cref="Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository"/>,
/// <see cref="Microsoft.AspNetCore.DataProtection.XmlEncryption.IXmlEncryptor"/> y
/// <see cref="Microsoft.AspNetCore.DataProtection.XmlEncryption.IXmlDecryptor"/>) y el SDK
/// de AWS, que en su destino net8.0 (el que usa esta aplicación) solo expone métodos
/// asíncronos para S3 y KMS: no hay API síncrona a la que llamar, que es lo que hacen
/// los repositorios oficiales de Azure.
///
/// Por qué en el pool: Data Protection invoca estas interfaces en el hilo de quien esté
/// usando un protector cuando el anillo de claves se carga en frío, se fuerza su recarga
/// o se usa por primera vez una clave (su descriptor se descifra de forma perezosa). En
/// un circuito de Blazor Server ese hilo lleva el SynchronizationContext del renderizador,
/// que ejecuta un elemento de trabajo cada vez: si se bloquea esperando una continuación
/// que un <c>await</c> quiso devolver a ese mismo contexto, la continuación no llega
/// nunca y el circuito se queda colgado. Ejecutar la operación en el pool, sin contexto,
/// hace que ninguna continuación —ni las nuestras ni las del SDK— dependa del hilo
/// bloqueado.
///
/// Por qué un límite, y qué garantiza: sin él, una llamada a S3 o KMS que no responde
/// retiene el hilo del llamante tanto como dure la pila de reintentos del SDK. El límite
/// es por petición al SDK, no por operación de Data Protection (ver
/// <see cref="S3XmlRepository"/>). La espera es un bloqueo del núcleo con plazo, que no
/// necesita ningún hilo del pool para vencer; al vencer se pide la cancelación con
/// <see cref="CancellationTokenSource.CancelAsync"/>, que marca el token antes de volver
/// y ejecuta en el pool los callbacks del SDK, de modo que ninguno corre en el hilo del
/// llamante. Lo que esto no garantiza: que la operación arranque a tiempo si el pool está
/// saturado (entonces vence el límite y la operación ya no se inicia), que el sistema
/// operativo despierte al hilo justo en el plazo, ni que la petición remota no haya
/// ocurrido. Si salta, Data Protection recibe un <see cref="TimeoutException"/>: en una
/// recarga conserva el anillo anterior, y en la carga en frío el error sale a la vista en
/// vez de quedarse esperando.
///
/// Operaciones abandonadas: una operación que no terminó en el límite sigue su curso
/// hasta que el SDK atienda la cancelación. Una escritura en S3 cuyo cuerpo ya se leyó
/// entero puede completarse después, y Data Protection, que la da por fallida, generará
/// otra clave; las dos son válidas. Una cuyo cuerpo aún no se había leído entero falla,
/// porque <see cref="S3XmlRepository"/> revoca el cuerpo al volver (ver
/// <see cref="S3XmlRepository.CuerpoRevocable"/>): S3 no llega a recibirlo completo.
/// Cuando la operación abandonada termina, sea cual sea su estado, una continuación
/// encolada en el pool (nunca en el hilo del llamante, tampoco si la operación terminó
/// justo al vencer la espera) observa su excepción tardía (no llega a
/// <see cref="TaskScheduler.UnobservedTaskException"/>), pasa su resultado tardío por
/// <c>descartarResultadoTardio</c> y, como último paso, llama a <c>liberarEntrada</c>.
/// Como la cancelación solo se pide después de vencer la
/// espera, un límite vencido siempre llega como <see cref="TimeoutException"/>, y
/// cualquier otra excepción (incluida una <see cref="OperationCanceledException"/> del
/// SDK) es de la operación.
///
/// Esto elimina el interbloqueo, no el bloqueo: el hilo del llamante sigue esperando
/// (hasta el límite), porque la interfaz de Data Protection es síncrona.
/// </summary>
internal static class LlamadaSincronaAws
{
    /// <summary>
    /// Holgado frente a la latencia normal de una petición a S3 o KMS (milisegundos), y
    /// corto frente al tiempo que un circuito puede quedarse esperando sin que el usuario
    /// lo dé por roto.
    /// </summary>
    internal static readonly TimeSpan LimitePorDefecto = TimeSpan.FromSeconds(30);

    /// <param name="descartarResultadoTardio">
    /// Recibe el resultado de una operación que terminó con éxito después del límite, que
    /// ya nadie usará. Corre en el pool; si lanza, la excepción se observa y se descarta.
    /// </param>
    /// <param name="liberarEntrada">
    /// Se llama una vez cuando la operación ya no puede usar lo que recibió, sea cual sea
    /// su desenlace: al volver, si terminó dentro del plazo, o desde el pool cuando la
    /// operación abandonada termina, o cuando el pool llega a una que no había arrancado y
    /// la descarta por cancelada (con el pool saturado, eso ocurre al recuperarse). Es el
    /// momento seguro para borrar una entrada que la operación aún podría estar enviando y
    /// que no se pueda revocar antes. Si lanza, la excepción se observa y se descarta.
    /// Hoy ningún llamador de producción lo usa: el material sensible se borra por otras
    /// vías (S3 revoca su cuerpo al volver, el cifrado pone a ceros su entrada al salir y
    /// el descifrado borra la respuesta tardía con <c>descartarResultadoTardio</c>). Los
    /// tests lo usan como señal de que la limpieza tardía terminó.
    /// </param>
    internal static T Ejecutar<T>(
        Func<CancellationToken, Task<T>> operacion,
        TimeSpan limite,
        string descripcion,
        Action<T>? descartarResultadoTardio = null,
        Action? liberarEntrada = null) =>
        Ejecutar(operacion, limite, descripcion, descartarResultadoTardio, liberarEntrada, TaskScheduler.Default);

    /// <param name="planificador">
    /// <see cref="TaskScheduler.Default"/> en producción (lo mismo que <c>Task.Run</c>);
    /// los tests pasan uno que retiene el trabajo para simular un pool saturado, o uno
    /// que la arranca en el acto (sin contexto) para que un vencimiento no dependa de
    /// cuándo llegue el pool a la operación.
    /// </param>
    internal static T Ejecutar<T>(
        Func<CancellationToken, Task<T>> operacion,
        TimeSpan limite,
        string descripcion,
        Action<T>? descartarResultadoTardio,
        Action? liberarEntrada,
        TaskScheduler planificador)
    {
        ArgumentNullException.ThrowIfNull(operacion);
        ArgumentNullException.ThrowIfNull(planificador);

        // Sin temporizador propio: el plazo lo pone la espera de abajo, y la única
        // cancelación es la de Abandonar. Un temporizador del origen dispararía en
        // un hilo del pool (con retraso si está saturado) y en carrera con la espera.
        var cancelacion = new CancellationTokenSource();
        // El token se toma antes de encolar: la tarea puede arrancar cuando el
        // origen ya no deba tocarse.
        var token = cancelacion.Token;

        // El token va también a StartNew: si la cancelación llega antes de que el
        // pool arranque la tarea, la tarea no se ejecuta. La comprobación de dentro
        // cubre la carrera entre ese aviso y el arranque: con el token ya cancelado
        // no se llega a invocar al SDK.
        var tarea = Task.Factory.StartNew(
                () => token.IsCancellationRequested ? Task.FromCanceled<T>(token) : operacion(token),
                token,
                TaskCreationOptions.DenyChildAttach,
                planificador)
            .Unwrap();

        if (!((IAsyncResult)tarea).AsyncWaitHandle.WaitOne(limite))
        {
            Abandonar(tarea, cancelacion, descartarResultadoTardio, liberarEntrada);
            throw Agotado(descripcion, limite);
        }

        // La tarea terminó dentro del plazo y el origen no se llegó a cancelar: una
        // OperationCanceledException que salga aquí es de la propia operación.
        try
        {
            return tarea.GetAwaiter().GetResult();
        }
        finally
        {
            try
            {
                // Libera el evento del núcleo que creó AsyncWaitHandle.
                tarea.Dispose();
                cancelacion.Dispose();
            }
            finally
            {
                InvocarSinLanzar(liberarEntrada);
            }
        }
    }

    internal static void Ejecutar(
        Func<CancellationToken, Task> operacion,
        TimeSpan limite,
        string descripcion,
        Action? liberarEntrada = null) =>
        Ejecutar(operacion, limite, descripcion, liberarEntrada, TaskScheduler.Default);

    internal static void Ejecutar(
        Func<CancellationToken, Task> operacion,
        TimeSpan limite,
        string descripcion,
        Action? liberarEntrada,
        TaskScheduler planificador)
    {
        ArgumentNullException.ThrowIfNull(operacion);

        Ejecutar(
            async token =>
            {
                await operacion(token).ConfigureAwait(false);
                return true;
            },
            limite,
            descripcion,
            descartarResultadoTardio: null,
            liberarEntrada,
            planificador);
    }

    /// <summary>
    /// Fuera del camino crítico del llamante: no espera a nada y no ejecuta en su hilo ni
    /// callbacks ajenos ni la limpieza. El origen no se dispone: no tiene temporizador que
    /// liberar, y la operación o los callbacks de la cancelación pueden seguir usándolo.
    /// </summary>
    internal static void Abandonar<T>(
        Task<T> tarea,
        CancellationTokenSource cancelacion,
        Action<T>? descartarResultadoTardio,
        Action? liberarEntrada)
    {
        // Un callback del SDK que falle hace fallar esta tarea: se observa para que
        // no acabe en UnobservedTaskException.
        _ = cancelacion.CancelAsync().ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

        // Sin ExecuteSynchronously, a propósito: si la tarea terminó entre el vencimiento
        // de la espera y este registro, ContinueWith la encola en el pool en vez de
        // ejecutarla aquí, y el llamante recibe el TimeoutException sin esperar a la
        // limpieza (que escribe en flujos y libera recursos que podrían bloquear).
        _ = tarea.ContinueWith(
            static (t, estado) =>
            {
                var (descartar, liberar) = ((Action<T>?, Action?))estado!;
                try
                {
                    if (t.IsFaulted)
                    {
                        // La excepción tardía se observa: el llamante ya recibió el
                        // TimeoutException y no hay nadie más que la espere.
                        _ = t.Exception;
                    }
                    else if (t.IsCompletedSuccessfully && descartar is not null)
                    {
                        descartar(t.Result);
                    }
                }
                catch (Exception)
                {
                    // Un descartador que falla no puede dejar esta continuación con una
                    // excepción que nadie observaría.
                }
                finally
                {
                    try
                    {
                        // Libera el evento del núcleo que creó AsyncWaitHandle.
                        t.Dispose();
                    }
                    finally
                    {
                        // Lo último, y aunque lo anterior fallara: quien espere a la
                        // liberación de la entrada encuentra la limpieza ya hecha.
                        InvocarSinLanzar(liberar);
                    }
                }
            },
            (descartarResultadoTardio, liberarEntrada),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private static void InvocarSinLanzar(Action? accion)
    {
        try
        {
            accion?.Invoke();
        }
        catch (Exception)
        {
            // Limpieza de mejor esfuerzo: no sustituye al resultado ni a la excepción de
            // la operación, y en el pool no habría nadie que la observase.
        }
    }

    private static TimeoutException Agotado(string descripcion, TimeSpan limite) =>
        new($"{descripcion} no respondió en {limite.TotalSeconds:0.###} s; se cancela para no retener el hilo del llamante.");
}
