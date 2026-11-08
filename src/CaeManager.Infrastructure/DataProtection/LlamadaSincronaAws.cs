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
/// Por qué <see cref="Task.Run(Func{Task})"/>: Data Protection invoca estas interfaces
/// en el hilo de quien esté usando un protector cuando el anillo de claves se carga en
/// frío, se fuerza su recarga o se usa por primera vez una clave (su descriptor se
/// descifra de forma perezosa). En un circuito de Blazor Server ese hilo lleva el
/// SynchronizationContext del renderizador, que ejecuta un elemento de trabajo cada
/// vez: si se bloquea esperando una continuación que un <c>await</c> quiso devolver a
/// ese mismo contexto, la continuación no llega nunca y el circuito se queda colgado.
/// Ejecutar la operación en el pool, sin contexto, hace que ninguna continuación —ni
/// las nuestras ni las del SDK— dependa del hilo bloqueado.
///
/// Por qué un límite: sin él, una llamada a S3 o KMS que no responde retiene el hilo
/// del llamante tanto como dure la pila de reintentos del SDK. El token se pasa al SDK
/// para cancelar la petición de verdad; la espera acotada garantiza el límite aunque
/// alguna capa no lo atienda. Si salta, Data Protection recibe una excepción: en una
/// recarga conserva el anillo anterior, y en la carga en frío el error sale a la vista
/// en vez de quedarse esperando. Un límite vencido no garantiza que la operación no
/// haya ocurrido: una escritura en S3 ya enviada puede completarse después, y Data
/// Protection, que la da por fallida, generará otra clave; las dos son válidas. Y si
/// el token se cancela y el SDK responde con una excepción propia antes de que venza
/// la espera, sale esa excepción y no un <see cref="TimeoutException"/>; para Data
/// Protection es indiferente, porque trata igual cualquier fallo.
///
/// Esto elimina el interbloqueo, no el bloqueo: el hilo del llamante sigue esperando
/// (como mucho el límite), porque la interfaz de Data Protection es síncrona.
/// </summary>
internal static class LlamadaSincronaAws
{
    /// <summary>
    /// Holgado frente a la latencia normal de S3 y KMS (milisegundos), y corto frente al
    /// tiempo que un circuito puede quedarse esperando sin que el usuario lo dé por roto.
    /// </summary>
    internal static readonly TimeSpan LimitePorDefecto = TimeSpan.FromSeconds(30);

    internal static T Ejecutar<T>(Func<CancellationToken, Task<T>> operacion, TimeSpan limite, string descripcion)
    {
        ArgumentNullException.ThrowIfNull(operacion);

        using var cancelacion = new CancellationTokenSource(limite);
        // El token se toma antes de Task.Run: si el pool tarda en arrancar la
        // tarea y el origen ya se ha dispuesto, leerlo dentro lanzaría.
        var token = cancelacion.Token;
        var tarea = Task.Run(() => operacion(token), CancellationToken.None);

        try
        {
            return tarea.WaitAsync(limite, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (TimeoutException ex) when (!ReferenceEquals(ex, tarea.Exception?.InnerException))
        {
            // Venció la espera acotada (no es un TimeoutException de la propia
            // operación). Puede ganar al temporizador del origen: se cancela aquí
            // para que la petición al SDK se corte de verdad antes del Dispose.
            try
            {
                cancelacion.Cancel();
            }
            catch (AggregateException)
            {
                // Un callback de cancelación del SDK que falla no debe sustituir
                // al TimeoutException: la operación ya se da por vencida.
            }

            throw Agotado(descripcion, limite, ex);
        }
        catch (OperationCanceledException ex) when (cancelacion.IsCancellationRequested)
        {
            throw Agotado(descripcion, limite, ex);
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

    private static TimeoutException Agotado(string descripcion, TimeSpan limite, Exception causa) =>
        new($"{descripcion} no respondió en {limite.TotalSeconds:0.###} s; se cancela para no retener el hilo del llamante.", causa);
}
