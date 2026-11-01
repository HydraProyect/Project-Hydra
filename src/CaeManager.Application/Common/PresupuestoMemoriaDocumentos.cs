namespace CaeManager.Application.Common;

/// <summary>
/// Tope de los bytes de documento que el proceso entero puede tener a la vez en
/// memoria mientras los lee, los convierte, los analiza, los muestra o los cifra
/// (sección <c>PresupuestoMemoriaDocumentos</c> de la configuración). Seguimiento
/// de S-C-3 de la auditoría del 2026-10-28 (OWASP API4:2023, ASVS 4.0.3 V12.1.1,
/// CWE-400): los topes de la Subida múltiple acotan lo que se retiene en disco por
/// pantalla, por Tenant y por proceso, pero no cuántos documentos están cargados en
/// memoria a la vez entre todas las pantallas y peticiones del mismo proceso, que es
/// lo que tumba la réplica para todos los Tenants.
///
/// <para>
/// <b>Por qué estas cifras.</b> El contenedor de la aplicación tiene un techo de
/// 3 GiB (<c>LIMITE_MEMORIA_APP</c> en los <c>docker-compose</c> de despliegue) y
/// durante un relevo azul/verde hay dos a la vez en el mismo servidor. 512 MiB
/// —una sexta parte del techo— deja el resto al runtime, a EF Core y a los circuitos
/// Blazor, y aun así admite a la vez unos doce archivos del tamaño máximo
/// (<see cref="LimitesArchivoSubido.TamanoMaximoBytes"/>, 10 MiB) en su paso más caro
/// de la Subida múltiple, o más de cien documentos de 1 MiB, que es el tamaño
/// habitual. Diez segundos de espera absorben una ráfaga sin que la pantalla parezca
/// colgada; pasado ese tiempo el documento se rechaza de forma controlada en vez de
/// arriesgar que el proceso se quede sin memoria. Es una elección técnica prudente,
/// no una decisión de producto, y se valida al arrancar.
/// </para>
///
/// <para>
/// <b>Cota aproximada.</b> Cada punto reserva una estimación de su pico —un múltiplo
/// del tamaño del documento—, no lo que el runtime asigna de verdad: copias internas
/// de Data Protection, del proveedor de IA o del GC no se miden. Por eso las
/// estimaciones se redondean hacia arriba y el presupuesto se deja muy por debajo del
/// techo del contenedor.
/// </para>
/// </summary>
public sealed class OpcionesPresupuestoMemoriaDocumentos
{
    public const string SeccionConfiguracion = "PresupuestoMemoriaDocumentos";

    /// <summary>Bytes de documento en memoria a la vez en todo el proceso.</summary>
    public long MaximoBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Lo que una reserva espera a que haya hueco antes de rechazarse.</summary>
    public TimeSpan EsperaMaxima { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Suelo del presupuesto: ocho archivos del máximo permitido (80 MiB). Por debajo,
    /// una sola operación sobre un archivo de 10 MiB puede no caber junto a lo que ella
    /// misma ya reservó —la lectura de la Subida múltiple pide cuatro veces el original;
    /// un guardado cuyo origen no dice su longitud reserva por crecimientos (1, 1, 2, 4
    /// y 8 MiB) antes de pedir el claro y el cifrado— y esperaría a su propia reserva
    /// hasta rechazarse.
    /// </summary>
    public const long MinimoBytes = 8L * LimitesArchivoSubido.TamanoMaximoBytes;

    /// <summary>
    /// El presupuesto tiene que admitir sin bloquearse la operación más cara sobre un
    /// archivo del máximo permitido (<see cref="MinimoBytes"/>), y la espera tiene que
    /// existir y estar acotada.
    /// </summary>
    public bool SonValidas() =>
        MaximoBytes >= MinimoBytes
        && EsperaMaxima > TimeSpan.Zero
        && EsperaMaxima <= TimeSpan.FromMinutes(5);
}

/// <summary>
/// Contabilidad por proceso de los bytes de documento cargados en memoria
/// (<see cref="OpcionesPresupuestoMemoriaDocumentos"/>). Cada punto que va a cargar
/// un documento entero reserva antes y desecha la reserva en un <c>finally</c>, salvo las
/// lecturas del almacenamiento (<see cref="IFileStorageService.AbrirAsync"/>): el flujo
/// descifrado que devuelven lleva su reserva y la devuelve al cerrarse.
/// </summary>
public interface IPresupuestoMemoriaDocumentos
{
    /// <summary>
    /// Reserva <paramref name="bytes"/> del presupuesto. Si no hay hueco, espera como
    /// mucho <see cref="OpcionesPresupuestoMemoriaDocumentos.EsperaMaxima"/>, en orden de
    /// llegada. Una petición mayor que el presupuesto entero no se rechaza por serlo:
    /// espera a tenerlo para ella sola, así nunca hay más de una a la vez.
    /// </summary>
    /// <exception cref="PresupuestoMemoriaAgotadoException">Pasó la espera máxima sin hueco.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> se canceló esperando.</exception>
    ValueTask<ReservaMemoriaDocumentos> ReservarAsync(long bytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// Bytes reservados del presupuesto; <see cref="Dispose"/> devuelve lo que quede una sola vez, y
/// <see cref="ReducirA"/> devuelve antes la parte que ya no se usa.
/// </summary>
public sealed class ReservaMemoriaDocumentos : IDisposable
{
    private readonly PresupuestoMemoriaDocumentos? _presupuesto;
    private long _bytes;

    internal ReservaMemoriaDocumentos(PresupuestoMemoriaDocumentos? presupuesto, long bytes)
    {
        _presupuesto = presupuesto;
        _bytes = bytes;
    }

    /// <summary>
    /// Lo que descuenta ahora del presupuesto: lo pedido, recortado al presupuesto entero, menos lo
    /// que ya se devolvió.
    /// </summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>
    /// Devuelve al presupuesto lo que pase de <paramref name="bytes"/>, para quien retiene el
    /// documento más allá de su pico (un flujo descifrado que aún se está sirviendo). Nunca amplía la
    /// reserva; sobre una reserva ya desechada no hace nada.
    /// </summary>
    public void ReducirA(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        while (true)
        {
            var actual = Interlocked.Read(ref _bytes);
            if (bytes >= actual)
                return;
            if (Interlocked.CompareExchange(ref _bytes, bytes, actual) == actual)
            {
                _presupuesto?.Devolver(actual - bytes);
                return;
            }
        }
    }

    public void Dispose()
    {
        var restante = Interlocked.Exchange(ref _bytes, 0);
        if (restante > 0)
            _presupuesto?.Devolver(restante);
    }
}

/// <summary>
/// No hubo hueco en el presupuesto de memoria de documentos dentro de la espera máxima. Es
/// transitorio: una petición HTTP la recibe como 503 con <c>Retry-After</c> y una pantalla
/// Blazor como aviso con <see cref="MensajeUsuario"/>, nunca como fallo sin controlar.
/// </summary>
public sealed class PresupuestoMemoriaAgotadoException() : Exception(MensajeUsuario)
{
    /// <summary>Lo que ve el usuario: no lleva datos del documento ni del Tenant.</summary>
    public const string MensajeUsuario =
        "El servidor tiene demasiados documentos en memoria ahora mismo; vuelve a intentarlo en unos segundos.";
}

/// <inheritdoc cref="IPresupuestoMemoriaDocumentos"/>
public sealed class PresupuestoMemoriaDocumentos : IPresupuestoMemoriaDocumentos
{
    private readonly object _cerrojo = new();
    // Orden de llegada: sin cola, un flujo de reservas pequeñas dejaría esperando para
    // siempre a una grande, que nunca encontraría hueco suficiente.
    private readonly LinkedList<Espera> _cola = new();
    private readonly TimeProvider _reloj;
    private long _reservado;

    public PresupuestoMemoriaDocumentos(
        Microsoft.Extensions.Options.IOptions<OpcionesPresupuestoMemoriaDocumentos> opciones, TimeProvider reloj)
    {
        Maximo = opciones.Value.MaximoBytes;
        EsperaMaxima = opciones.Value.EsperaMaxima;
        _reloj = reloj;
    }

    public long Maximo { get; }

    public TimeSpan EsperaMaxima { get; }

    /// <summary>Bytes reservados ahora mismo en todo el proceso.</summary>
    public long Reservado
    {
        get { lock (_cerrojo) return _reservado; }
    }

    /// <summary>Reservas que esperan hueco ahora mismo.</summary>
    public int EnEspera
    {
        get { lock (_cerrojo) return _cola.Count; }
    }

    public async ValueTask<ReservaMemoriaDocumentos> ReservarAsync(long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        cancellationToken.ThrowIfCancellationRequested();

        var pedido = Math.Min(bytes, Maximo);
        if (pedido == 0)
            return new ReservaMemoriaDocumentos(null, 0);

        Espera espera;
        LinkedListNode<Espera> nodo;
        lock (_cerrojo)
        {
            if (_cola.Count == 0 && _reservado + pedido <= Maximo)
            {
                _reservado += pedido;
                return new ReservaMemoriaDocumentos(this, pedido);
            }

            espera = new Espera(pedido);
            nodo = _cola.AddLast(espera);
        }

        try
        {
            await espera.Concedida.Task.WaitAsync(EsperaMaxima, _reloj, cancellationToken).ConfigureAwait(false);
            return new ReservaMemoriaDocumentos(this, pedido);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            bool concedidaEntretanto;
            lock (_cerrojo)
            {
                concedidaEntretanto = espera.Concedida.Task.IsCompletedSuccessfully;
                if (!concedidaEntretanto)
                {
                    _cola.Remove(nodo);
                    // Si esta era la primera de la cola, las de detrás pueden caber ya.
                    Conceder();
                }
            }

            // Se concedió justo cuando vencía la espera: el hueco ya es suyo, así que se
            // devuelve; quedárselo sin que nadie lo use lo perdería para siempre.
            if (concedidaEntretanto)
                Devolver(pedido);

            if (ex is TimeoutException)
                throw new PresupuestoMemoriaAgotadoException();
            throw;
        }
    }

    internal void Devolver(long bytes)
    {
        lock (_cerrojo)
        {
            _reservado = Math.Max(0, _reservado - bytes);
            Conceder();
        }
    }

    /// <summary>Concede, en orden, las esperas que caben. Solo con el cerrojo tomado.</summary>
    private void Conceder()
    {
        while (_cola.First is { } primera && _reservado + primera.Value.Bytes <= Maximo)
        {
            _cola.RemoveFirst();
            _reservado += primera.Value.Bytes;
            primera.Value.Concedida.TrySetResult();
        }
    }

    private sealed class Espera(long bytes)
    {
        public long Bytes { get; } = bytes;

        // Asíncronas: la continuación del que esperaba no puede correr dentro del cerrojo.
        public TaskCompletionSource Concedida { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
