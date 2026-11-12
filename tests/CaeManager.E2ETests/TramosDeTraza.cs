namespace CaeManager.E2ETests;

/// <summary>
/// Las operaciones de la traza de Playwright que usa <see cref="GrabacionDeTraza"/>, como delegados para poder probarla
/// sin navegador. En <c>TrazaAlFallar</c> son las de <c>IBrowserContext.Tracing</c>.
/// </summary>
/// <param name="Empezar">Empieza a grabar (la primera vez, con capturas e instantáneas).</param>
/// <param name="EmpezarTramo">Empieza un tramo nuevo tras un corte.</param>
/// <param name="CortarTramo">Corta el tramo en curso y lo escribe en la ruta dada, sin dejar de tener la traza iniciada.</param>
/// <param name="DetenerGuardando">Detiene la traza y escribe el tramo en curso en la ruta dada.</param>
/// <param name="DetenerDescartando">Detiene la traza sin escribir nada.</param>
internal sealed record OperacionesDeTraza(
    Func<Task> Empezar,
    Func<Task> EmpezarTramo,
    Func<string, Task> CortarTramo,
    Func<string, Task> DetenerGuardando,
    Func<Task> DetenerDescartando);

/// <summary>
/// Cuándo graba la traza de <c>TrazaAlFallar</c>, sin navegador: no graba hasta acabar el primer inicio de sesión, deja
/// de grabar antes de cada inicio de sesión siguiente (aunque no se pueda guardar el tramo), no vuelve a grabar tras
/// una avería y, al acabar, solo guarda el tramo en curso si el test falló y se estaba grabando. Lo que Playwright
/// grabara mientras no se graba (p. ej. si un corte falló y la traza siguió en marcha durante un inicio de sesión) solo
/// se detiene descartándolo: nunca llega a un fichero.
/// </summary>
internal sealed class GrabacionDeTraza(TramosDeTraza tramos, OperacionesDeTraza operaciones)
{
    private bool _iniciada;
    private bool _grabando;
    private bool _superada;

    public bool Grabando => _grabando;

    /// <summary>El test llegó al final: la traza se descarta.</summary>
    public void Superada() => _superada = true;

    /// <summary>Antes de un inicio de sesión: corta el tramo en curso, si lo hay, y deja de grabar.</summary>
    public async Task PausarAsync()
    {
        if (!_grabando) return;
        // Deja de grabar ANTES de cortar: si el corte falla o se cancela, lo que no puede pasar es que se guarde el acceso.
        _grabando = false;
        await tramos.CortarAsync(operaciones.CortarTramo, $"tramo{tramos.Completos.Count + 1}");
    }

    /// <summary>Al acabar un inicio de sesión: empieza (o sigue, en un tramo nuevo) a grabar, salvo tras una avería.</summary>
    public async Task ReanudarAsync()
    {
        // Tras una avería no se sabe si la traza sigue en marcha ni qué lleva: no se vuelve a grabar en este contexto.
        if (_grabando || tramos.Averia is not null) return;
        try
        {
            if (_iniciada) await operaciones.EmpezarTramo();
            else await operaciones.Empezar();
            _iniciada = true;
            _grabando = true;
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            tramos.Averiar($"no se pudo empezar a grabar tras el inicio de sesión: {excepcion.GetType().Name}: {excepcion.Message}");
        }
    }

    /// <summary>Al liberar el contexto: detiene la traza y publica o borra sus tramos (ver <see cref="TramosDeTraza"/>).</summary>
    public async Task TerminarAsync()
    {
        try
        {
            await DetenerAsync();
        }
        finally
        {
            tramos.Concluir(_superada, _iniciada
                ? "el test falló sin que quedara ningún tramo grabado"
                : "el test falló antes de acabar el inicio de sesión, y la traza no graba hasta entonces");
        }
    }

    private async Task DetenerAsync()
    {
        if (!_iniciada) return;
        if (!_superada && _grabando)
        {
            _grabando = false;
            await tramos.CortarAsync(operaciones.DetenerGuardando, "final");
            return;
        }

        try
        {
            // Sin ruta, Playwright descarta el tramo en curso (o no hay ninguno, si se cortó para otro inicio de sesión).
            await operaciones.DetenerDescartando();
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            // El contexto o el navegador ya se cerraron (p. ej. el test falló porque se cayó el navegador): tapar con esto
            // la excepción original del test sería peor que perder la traza. Si el test falló, Concluir lo anota.
            if (!_superada) tramos.Averiar($"no se pudo detener la traza: {excepcion.GetType().Name}: {excepcion.Message}");
        }
    }
}

/// <summary>
/// Qué se hace con los tramos de la traza de Playwright de <c>TrazaAlFallar</c>, sin navegador: dónde se escribe
/// cada tramo, qué se borra, qué se publica y cuándo se deja un <c>…-sin-traza.txt</c> (#2194). Va aparte de
/// <c>TrazaAlFallar</c> para poder probarlo sin servidor ni navegador: CaeManager.Web.Tests enlaza este fichero
/// (ver su csproj) y lo prueba en TramosDeTrazaTests y GrabacionDeTrazaTests, que corren en el job de bUnit.
/// <para>
/// Cada tramo se escribe primero en un directorio provisional que CI no publica (bajo obj/ del proyecto de E2E, en el
/// mismo sistema de ficheros que TestResults para que el movimiento final sea un renombrado) y solo pasa al directorio
/// publicado si el test falla y el tramo quedó completo. Si el corte falla, la ruta se borra: un ZIP a medio escribir no
/// llega nunca al artefacto.
/// </para>
/// </summary>
internal sealed class TramosDeTraza
{
    private readonly string _nombre;
    private readonly string _directorioPublicado;
    private readonly string _directorioProvisional;
    private readonly string _marca;
    private readonly TextWriter _salida;
    private readonly TextWriter _error;
    private readonly List<(string Ruta, string Sufijo)> _completos = [];
    private string? _averia;

    /// <param name="nombre">Nombre del test, que encabeza el de cada fichero.</param>
    /// <param name="directorioPublicado">El que CI sube como artefacto (TestResults/trazas-playwright).</param>
    /// <param name="directorioProvisional">Uno que CI no sube; esta instancia trabaja en un subdirectorio propio.</param>
    public TramosDeTraza(string nombre, string directorioPublicado, string directorioProvisional, TextWriter? salida = null, TextWriter? error = null)
    {
        _nombre = nombre;
        _directorioPublicado = directorioPublicado;
        var instancia = Guid.NewGuid().ToString("N");
        // La instancia va también en el nombre publicado: dos trazas del mismo test en el mismo segundo no se pisan.
        _marca = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{instancia[..8]}";
        // Un subdirectorio por instancia: las colecciones de xUnit corren en paralelo y dos tests pueden llamarse igual.
        _directorioProvisional = Path.Combine(directorioProvisional, $"{nombre}-{instancia}");
        _salida = salida ?? Console.Out;
        _error = error ?? Console.Error;
    }

    /// <summary>Motivo por el que la traza quedará incompleta, o null si todo fue bien hasta ahora.</summary>
    public string? Averia => _averia;

    /// <summary>Tramos completos que esperan, en el directorio provisional, a que el test acabe.</summary>
    public IReadOnlyList<string> Completos => _completos.Select(t => t.Ruta).ToList();

    /// <summary>Anota por qué la traza quedará incompleta; se conserva el primer motivo.</summary>
    public void Averiar(string motivo) => _averia ??= motivo;

    /// <summary>
    /// Corta un tramo con <paramref name="cortar"/> (que recibe la ruta en la que escribir) en el directorio provisional.
    /// Devuelve si el tramo quedó completo. Ante cualquier fallo borra la ruta y anota la avería; una cancelación también
    /// la borra, y se propaga.
    /// </summary>
    public async Task<bool> CortarAsync(Func<string, Task> cortar, string sufijo)
    {
        string? ruta = null;
        try
        {
            Directory.CreateDirectory(_directorioProvisional);
            ruta = Path.Combine(_directorioProvisional, $"{_completos.Count + 1:D2}-{sufijo}.zip");
            await cortar(ruta);
            if (!File.Exists(ruta)) throw new FileNotFoundException("el corte terminó sin escribir el tramo", ruta);
            _completos.Add((ruta, sufijo));
            return true;
        }
        catch (OperationCanceledException)
        {
            Borrar(ruta);
            throw;
        }
        catch (Exception excepcion)
        {
            Borrar(ruta);
            Averiar($"no se pudo cortar el tramo «{sufijo}»: {excepcion.GetType().Name}: {excepcion.Message}");
            return false;
        }
    }

    /// <summary>
    /// Al acabar el test. Superado: borra los tramos y no publica nada. Fallido: mueve los tramos completos al directorio
    /// publicado y, si hubo una avería o no queda ningún tramo, deja el motivo en un <c>…-sin-traza.txt</c> (y en la
    /// salida de error). Nunca lanza, salvo una cancelación. Devuelve las rutas publicadas.
    /// </summary>
    /// <param name="motivoSinTramos">Qué decir si el test falló sin que se llegara a grabar ningún tramo.</param>
    public IReadOnlyList<string> Concluir(bool superada, string motivoSinTramos)
    {
        var publicadas = new List<string>();
        try
        {
            if (superada)
            {
                foreach (var (ruta, _) in _completos) Borrar(ruta);
                return publicadas;
            }

            foreach (var (ruta, sufijo) in _completos)
            {
                var destino = Path.Combine(_directorioPublicado, $"{_nombre}-{_marca}-{sufijo}.zip");
                var existiaAntes = File.Exists(destino);
                try
                {
                    Directory.CreateDirectory(_directorioPublicado);
                    File.Move(ruta, destino);
                    publicadas.Add(destino);
                    _salida.WriteLine($"Traza de Playwright del test fallido: {destino}");
                }
                catch (Exception excepcion) when (excepcion is not OperationCanceledException)
                {
                    // Un movimiento entre sistemas de ficheros es copia y borrado: si falla a medias, la copia del
                    // directorio publicado puede estar incompleta.
                    if (!existiaAntes) Borrar(destino);
                    Borrar(ruta);
                    Averiar($"no se pudo publicar el tramo «{sufijo}» ({destino}): {excepcion.GetType().Name}: {excepcion.Message}");
                }
            }

            if (_averia is not null || publicadas.Count == 0) AnotarSinTraza(_averia ?? motivoSinTramos);
            return publicadas;
        }
        finally
        {
            _completos.Clear();
            try
            {
                if (Directory.Exists(_directorioProvisional)) Directory.Delete(_directorioProvisional, recursive: true);
            }
            catch (Exception excepcion) when (excepcion is not OperationCanceledException) { }
        }
    }

    private void AnotarSinTraza(string motivo)
    {
        var mensaje = $"Traza de Playwright de {_nombre} incompleta o ausente: {motivo}";
        _error.WriteLine(mensaje);
        try
        {
            Directory.CreateDirectory(_directorioPublicado);
            File.WriteAllText(Path.Combine(_directorioPublicado, $"{_nombre}-{_marca}-sin-traza.txt"), mensaje + Environment.NewLine);
        }
        catch (Exception excepcionAlAnotar) when (excepcionAlAnotar is not OperationCanceledException)
        {
            _error.WriteLine($"Tampoco se pudo anotar en {_directorioPublicado}: {excepcionAlAnotar.Message}");
        }
    }

    private static void Borrar(string? ruta)
    {
        if (ruta is null) return;
        try { File.Delete(ruta); }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException) { }
    }
}
