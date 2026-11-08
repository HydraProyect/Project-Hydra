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
/// hasta que el SDK atienda la cancelación. Una escritura en S3 ya enviada puede
/// completarse después, y Data Protection, que la da por fallida, generará otra clave;
/// las dos son válidas. Su excepción tardía se observa (no llega a
/// <see cref="TaskScheduler.UnobservedTaskException"/>) y su resultado tardío pasa por
/// <c>descartarResultadoTardio</c> para que quien llama pueda borrar material sensible.
/// Como la cancelación solo se pide después de vencer la espera, un límite vencido
/// siempre llega como <see cref="TimeoutException"/>, y cualquier otra excepción
/// (incluida una <see cref="OperationCanceledException"/> del SDK) es de la operación.
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

    internal static T Ejecutar<T>(
        Func<CancellationToken, Task<T>> operacion,
        TimeSpan limite,
        string descripcion,
        Action<T>? descartarResultadoTardio = null) =>
        Ejecutar(operacion, limite, descripcion, descartarResultadoTardio, TaskScheduler.Default);

    /// <param name="planificador">
    /// <see cref="TaskScheduler.Default"/> en producción (lo mismo que <c>Task.Run</c>);
    /// los tests pasan uno que retiene el trabajo para simular un pool saturado.
    /// </param>
    internal static T Ejecutar<T>(
        Func<CancellationToken, Task<T>> operacion,
        TimeSpan limite,
        string descripcion,
        Action<T>? descartarResultadoTardio,
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
            Abandonar(tarea, cancelacion, descartarResultadoTardio);
            throw Agotado(descripcion, limite, causa: null);
        }

        // La tarea terminó dentro del plazo y el origen no se llegó a cancelar: una
        // OperationCanceledException que salga aquí es de la propia operación.
        try
        {
            return tarea.GetAwaiter().GetResult();
        }
        finally
        {
            // Libera el evento del núcleo que creó AsyncWaitHandle.
            tarea.Dispose();
            cancelacion.Dispose();
        }
    }

    internal static void Ejecutar(Func<CancellationToken, Task> operacion, TimeSpan limite, string descripcion)
    {
        ArgumentNullException.ThrowIfNull(operacion);

        Ejecutar(
            async token =>
            {
                await operacion(token).ConfigureAwait(false);
                return true;
            },
            limite,
            descripcion);
    }

    /// <summary>
    /// Fuera del camino crítico del llamante: no espera a nada y no ejecuta callbacks
    /// ajenos en su hilo. El origen no se dispone: no tiene temporizador que liberar, y
    /// la operación o los callbacks de la cancelación pueden seguir usándolo.
    /// </summary>
    private static void Abandonar<T>(Task<T> tarea, CancellationTokenSource cancelacion, Action<T>? descartarResultadoTardio)
    {
        // Un callback del SDK que falle hace fallar esta tarea: se observa para que
        // no acabe en UnobservedTaskException.
        _ = cancelacion.CancelAsync().ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // descartarResultadoTardio no debe lanzar: correría aquí, sin nadie que lo observe.
        _ = tarea.ContinueWith(
            static (t, estado) =>
            {
                if (t.IsFaulted)
                {
                    // La excepción tardía se observa: el llamante ya recibió el
                    // TimeoutException y no hay nadie más que la espere.
                    _ = t.Exception;
                }
                else if (t.IsCompletedSuccessfully && estado is Action<T> descartar)
                {
                    descartar(t.Result);
                }

                // Libera el evento del núcleo que creó AsyncWaitHandle.
                t.Dispose();
            },
            descartarResultadoTardio,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static TimeoutException Agotado(string descripcion, TimeSpan limite, Exception? causa) =>
        new($"{descripcion} no respondió en {limite.TotalSeconds:0.###} s; se cancela para no retener el hilo del llamante.", causa);
}
