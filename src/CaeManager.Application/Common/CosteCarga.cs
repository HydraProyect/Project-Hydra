namespace CaeManager.Application.Common;

/// <summary>
/// Acumulador del coste de acceso a datos de un scope de DI: una petición HTTP
/// (la respuesta SSR de una página) o un circuito de Blazor. Lo alimentan los
/// interceptores de medición de EF Core (comandos y aperturas de conexión), la
/// espera en la cola de <see cref="PuertaAccesoDatos"/> y
/// <see cref="CosteCargaBehavior{TRequest,TResponse}"/> (despachos de MediatR).
/// Lo leen la cabecera <c>Server-Timing</c> (Web) y el registro por carga de
/// página del circuito interactivo.
///
/// <para>
/// Solo cuenta números: ni texto SQL, ni ids, ni Tenant, ni ruta. Esa es la
/// condición para poder enseñárselo al navegador (OWASP ASVS 4.0.3 V14.3: no
/// divulgar información del servidor más allá de lo necesario).
/// </para>
///
/// <para>
/// Coste despreciable a propósito: contadores propios del scope actualizados
/// con <see cref="Interlocked"/>, sin locks ni estado compartido entre scopes.
/// Un circuito acumula durante toda su vida; quien mide una carga concreta
/// toma una <see cref="Leer">instantánea</see> al empezar y resta (ver
/// <see cref="InstantaneaCosteCarga.Menos"/>), así que nadie tiene que
/// reiniciar el acumulador ni coordinarse con otro lector.
/// </para>
/// </summary>
public sealed class CosteCarga
{
    private long _ticksBaseDatos;
    private long _comandos;
    private long _conexiones;
    private long _ticksEsperaPuerta;
    private long _despachos;

    /// <summary>Un comando SQL ejecutado por EF Core y lo que tardó en ejecutarse.</summary>
    public void RegistrarComando(TimeSpan duracion)
    {
        if (duracion > TimeSpan.Zero)
            Interlocked.Add(ref _ticksBaseDatos, duracion.Ticks);
        Interlocked.Increment(ref _comandos);
    }

    /// <summary>Una apertura de conexión física o del pool hecha por EF Core.</summary>
    public void RegistrarConexion() => Interlocked.Increment(ref _conexiones);

    /// <summary>Tiempo esperado en la cola de <see cref="PuertaAccesoDatos"/> antes de entrar.</summary>
    public void RegistrarEsperaPuerta(TimeSpan espera)
    {
        if (espera > TimeSpan.Zero)
            Interlocked.Add(ref _ticksEsperaPuerta, espera.Ticks);
    }

    /// <summary>Un despacho de MediatR (Command o Query, también los anidados).</summary>
    public void RegistrarDespacho() => Interlocked.Increment(ref _despachos);

    /// <summary>Lo acumulado hasta ahora en este scope.</summary>
    public InstantaneaCosteCarga Leer() => new(
        TimeSpan.FromTicks(Interlocked.Read(ref _ticksBaseDatos)),
        Interlocked.Read(ref _comandos),
        Interlocked.Read(ref _conexiones),
        TimeSpan.FromTicks(Interlocked.Read(ref _ticksEsperaPuerta)),
        Interlocked.Read(ref _despachos));
}

/// <summary>
/// Lectura puntual de <see cref="CosteCarga"/>. Solo números, por el mismo
/// motivo que el acumulador.
/// </summary>
public readonly record struct InstantaneaCosteCarga(
    TimeSpan BaseDatos,
    long Comandos,
    long Conexiones,
    TimeSpan EsperaPuerta,
    long Despachos)
{
    /// <summary>
    /// Lo ocurrido entre <paramref name="anterior"/> y esta instantánea. Los dos
    /// tienen que venir del mismo acumulador; como solo crece, ningún campo sale
    /// negativo.
    /// </summary>
    public InstantaneaCosteCarga Menos(InstantaneaCosteCarga anterior) => new(
        BaseDatos - anterior.BaseDatos,
        Comandos - anterior.Comandos,
        Conexiones - anterior.Conexiones,
        EsperaPuerta - anterior.EsperaPuerta,
        Despachos - anterior.Despachos);
}
