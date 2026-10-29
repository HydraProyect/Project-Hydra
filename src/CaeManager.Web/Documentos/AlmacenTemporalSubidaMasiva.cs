using System.Buffers;

namespace CaeManager.Web.Documentos;

/// <summary>
/// Lo que retiene una pantalla de Subida múltiple mientras sus archivos esperan
/// a leerse o a que una persona los confirme, en ficheros temporales y no en
/// <c>byte[]</c> del circuito Blazor (S-C-3, auditoría del 2026-10-28; OWASP
/// API4:2023, ASVS 4.0.3 V12.1.1, CWE-400). Antes un lote podía dejar unos
/// 600 MiB vivos en memoria por circuito —y los lotes siguientes, otro tanto—,
/// en el mismo proceso que sirve a todos los Tenants.
///
/// <para>
/// <b>Reserva antes de leer.</b> Cada escritura reserva primero el tamaño
/// declarado contra el tope de la pantalla, el del Tenant de origen y el global
/// (<see cref="CuotaSubidaMasiva"/>) y solo después abre el origen: lo que no cabe
/// se rechaza sin leer ni un byte. Si el origen trae más de lo declarado, cada
/// trozo de más se reserva antes de escribirse.
/// </para>
///
/// <para>
/// <b>Borrado.</b> <see cref="Eliminar"/> borra el fichero y devuelve su reserva;
/// <see cref="Dispose"/> —la pantalla lo llama al cerrarse, también cuando el
/// circuito se cae— borra todo lo que quede y devuelve la reserva entera. Lo que
/// sobrevive a una caída del proceso lo recoge la <see cref="CuotaSubidaMasiva"/>
/// del proceso siguiente.
/// </para>
///
/// <para>
/// <see cref="TryReservar"/> y <see cref="Liberar"/> son también para memoria
/// que la pantalla retiene fuera de estos ficheros (las miniaturas): cuentan
/// contra los mismos topes.
/// </para>
///
/// <para>
/// <b>E/S de disco síncrona, a propósito.</b> Solo el origen (el navegador, una
/// entrada del .zip) se lee de forma asíncrona; el temporal se escribe y se lee
/// en trozos de 64 KiB contra disco local, que es lo mismo que hacía antes el
/// <c>MemoryStream</c> en coste de hilo. Así una operación cuyo origen ya está
/// en memoria termina sin ceder el hilo del circuito a mitad, igual que antes.
/// </para>
/// </summary>
public sealed class AlmacenTemporalSubidaMasiva : IDisposable
{
    private const int TamanoBufer = 64 * 1024;

    private readonly CuotaSubidaMasiva _cuota;
    private readonly Guid _tenantId;
    private readonly object _cerrojo = new();
    private readonly HashSet<string> _ficheros = [];
    private long _reservado;
    private bool _directorioCreado;
    private bool _desechado;

    internal AlmacenTemporalSubidaMasiva(CuotaSubidaMasiva cuota, Guid tenantId)
    {
        _cuota = cuota;
        _tenantId = tenantId;
        Directorio = Path.Combine(cuota.DirectorioProceso, Guid.NewGuid().ToString("N"));
    }

    /// <summary>Se crea con el primer fichero: una pantalla que no sube nada no deja rastro en disco.</summary>
    public string Directorio { get; }

    /// <summary>Bytes que esta pantalla tiene reservados ahora mismo.</summary>
    public long Reservado
    {
        get { lock (_cerrojo) return _reservado; }
    }

    public bool TryReservar(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_cerrojo)
        {
            if (_desechado || _reservado + bytes > _cuota.MaximoBytesPorCircuito)
                return false;
            if (!_cuota.TryReservar(_tenantId, bytes))
                return false;

            _reservado += bytes;
            return true;
        }
    }

    public void Liberar(long bytes)
    {
        lock (_cerrojo)
        {
            // Tras Dispose la reserva entera ya se devolvió.
            if (_desechado || bytes <= 0)
                return;

            var devuelto = Math.Min(bytes, _reservado);
            _reservado -= devuelto;
            _cuota.Liberar(_tenantId, devuelto);
        }
    }

    /// <summary>
    /// Copia a un temporal lo que dé <paramref name="abrirOrigen"/>, que solo se
    /// llama si <paramref name="tamanoDeclarado"/> cabe en la reserva.
    /// </summary>
    /// <exception cref="ArchivoTemporalDemasiadoGrandeException">El origen declara o trae más de <paramref name="maximoBytes"/>.</exception>
    /// <exception cref="CuotaSubidaMasivaSuperadaException">No queda reserva para lo declarado o para lo que llega de más.</exception>
    public Task<ArchivoTemporal> GuardarAsync(
        Func<Stream> abrirOrigen,
        long tamanoDeclarado,
        long maximoBytes,
        Action<long>? progreso = null,
        CancellationToken cancellationToken = default) =>
        GuardarDesdeOrigenAsync(abrirOrigen, tamanoDeclarado, maximoBytes, progreso, origenSincrono: false, cancellationToken);

    /// <summary>
    /// Igual que <see cref="GuardarAsync(Func{Stream}, long, long, Action{long}?, CancellationToken)"/>,
    /// leyendo el origen de forma síncrona: para un origen que ya está en este
    /// servidor (una entrada de un .zip guardado aquí), nunca para el navegador.
    /// </summary>
    public ArchivoTemporal Guardar(Func<Stream> abrirOrigen, long tamanoDeclarado, long maximoBytes) =>
        // Con origenSincrono no hay ningún await que ceda: la tarea vuelve ya terminada.
        GuardarDesdeOrigenAsync(abrirOrigen, tamanoDeclarado, maximoBytes, null, origenSincrono: true, CancellationToken.None)
            .GetAwaiter().GetResult();

    private async Task<ArchivoTemporal> GuardarDesdeOrigenAsync(
        Func<Stream> abrirOrigen,
        long tamanoDeclarado,
        long maximoBytes,
        Action<long>? progreso,
        bool origenSincrono,
        CancellationToken cancellationToken)
    {
        if (tamanoDeclarado > maximoBytes)
            throw new ArchivoTemporalDemasiadoGrandeException();

        var reservado = Math.Max(0, tamanoDeclarado);
        if (!TryReservar(reservado))
            throw new CuotaSubidaMasivaSuperadaException();

        string? ruta = null;
        try
        {
            ruta = NuevaRuta();
            long total = 0;
            var origen = abrirOrigen();
            try
            {
                using var destino = AbrirEscritura(ruta);
                var bufer = ArrayPool<byte>.Shared.Rent(TamanoBufer);
                try
                {
                    int leidos;
                    while ((leidos = origenSincrono
                               ? origen.Read(bufer, 0, TamanoBufer)
                               : await origen.ReadAsync(bufer.AsMemory(0, TamanoBufer), cancellationToken)) > 0)
                    {
                        if (total + leidos > maximoBytes)
                            throw new ArchivoTemporalDemasiadoGrandeException();

                        if (total + leidos > reservado)
                        {
                            var falta = total + leidos - reservado;
                            if (!TryReservar(falta))
                                throw new CuotaSubidaMasivaSuperadaException();
                            reservado += falta;
                        }

                        destino.Write(bufer, 0, leidos);
                        total += leidos;
                        progreso?.Invoke(total);
                    }
                }
                finally
                {
                    // clearArray: el búfer ha tenido contenido de un documento de
                    // Trabajador y vuelve a un pool compartido con el resto del proceso.
                    ArrayPool<byte>.Shared.Return(bufer, clearArray: true);
                }
            }
            finally
            {
                if (origenSincrono)
                    origen.Dispose();
                else
                    await origen.DisposeAsync();
            }

            if (reservado > total)
            {
                Liberar(reservado - total);
                reservado = total;
            }

            return new ArchivoTemporal(ruta, total);
        }
        catch
        {
            if (ruta is not null)
                BorrarFichero(ruta);
            Liberar(reservado);
            throw;
        }
    }

    /// <exception cref="CuotaSubidaMasivaSuperadaException">No queda reserva para <paramref name="contenido"/>.</exception>
    public Task<ArchivoTemporal> GuardarAsync(byte[] contenido, CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(Guardar(contenido, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ArchivoTemporal>(cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException<ArchivoTemporal>(ex);
        }
    }

    private ArchivoTemporal Guardar(byte[] contenido, CancellationToken cancellationToken)
    {
        if (!TryReservar(contenido.Length))
            throw new CuotaSubidaMasivaSuperadaException();

        string? ruta = null;
        try
        {
            ruta = NuevaRuta();
            cancellationToken.ThrowIfCancellationRequested();
            using (var destino = AbrirEscritura(ruta))
                destino.Write(contenido);
            return new ArchivoTemporal(ruta, contenido.Length);
        }
        catch
        {
            if (ruta is not null)
                BorrarFichero(ruta);
            Liberar(contenido.Length);
            throw;
        }
    }

    /// <summary>Lectura en flujo; el llamador la desecha. Admite que el fichero se borre mientras está abierta.</summary>
    public Stream AbrirLectura(ArchivoTemporal archivo) =>
        new FileStream(archivo.Ruta, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            TamanoBufer, FileOptions.SequentialScan);

    public Task<byte[]> LeerAsync(ArchivoTemporal archivo, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var flujo = AbrirLectura(archivo);
        var contenido = new byte[archivo.TamanoBytes];
        flujo.ReadExactly(contenido);
        return Task.FromResult(contenido);
    }

    /// <summary>Los primeros bytes (la firma), sin cargar el archivo entero.</summary>
    public Task<byte[]> LeerCabeceraAsync(ArchivoTemporal archivo, int bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var flujo = AbrirLectura(archivo);
        var cabecera = new byte[Math.Min(bytes, archivo.TamanoBytes)];
        flujo.ReadExactly(cabecera);
        return Task.FromResult(cabecera);
    }

    /// <summary>Borra el fichero y devuelve su reserva. Idempotente.</summary>
    public void Eliminar(ArchivoTemporal archivo)
    {
        if (Interlocked.Exchange(ref archivo.EliminadoFlag, 1) == 1)
            return;

        BorrarFichero(archivo.Ruta);
        Liberar(archivo.TamanoBytes);
    }

    public void Dispose()
    {
        List<string> pendientes;
        lock (_cerrojo)
        {
            if (_desechado)
                return;

            _desechado = true;
            pendientes = [.. _ficheros];
            _ficheros.Clear();
            _cuota.Liberar(_tenantId, _reservado);
            _reservado = 0;
        }

        foreach (var ruta in pendientes)
            BorrarEnDisco(ruta);
        BorrarDirectorio();
    }

    private string NuevaRuta()
    {
        lock (_cerrojo)
        {
            ObjectDisposedException.ThrowIf(_desechado, this);
            if (!_directorioCreado)
            {
                CuotaSubidaMasiva.CrearDirectorioPrivado(Directorio);
                _directorioCreado = true;
            }

            var ruta = Path.Combine(Directorio, Guid.NewGuid().ToString("N"));
            _ficheros.Add(ruta);
            return ruta;
        }
    }

    private static FileStream AbrirEscritura(string ruta)
    {
        var opciones = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Delete,
            BufferSize = TamanoBufer
        };
        if (!OperatingSystem.IsWindows())
            opciones.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(ruta, opciones);
    }

    private void BorrarFichero(string ruta)
    {
        bool desechado;
        lock (_cerrojo)
        {
            _ficheros.Remove(ruta);
            desechado = _desechado;
        }

        BorrarEnDisco(ruta);
        // Una escritura o una lectura que seguía en vuelo al desechar pudo
        // impedir borrar el directorio entonces.
        if (desechado)
            BorrarDirectorio();
    }

    private static void BorrarEnDisco(string ruta)
    {
        try
        {
            File.Delete(ruta);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void BorrarDirectorio()
    {
        try
        {
            if (Directory.Exists(Directorio))
                Directory.Delete(Directorio, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Un archivo retenido por <see cref="AlmacenTemporalSubidaMasiva"/>; no expone su ruta fuera de Web.</summary>
public sealed class ArchivoTemporal
{
    internal int EliminadoFlag;

    internal ArchivoTemporal(string ruta, long tamanoBytes)
    {
        Ruta = ruta;
        TamanoBytes = tamanoBytes;
    }

    internal string Ruta { get; }

    public long TamanoBytes { get; }

    public bool Eliminado => Volatile.Read(ref EliminadoFlag) == 1;
}

/// <summary>Lo retenido por la pantalla, por su Tenant de origen o por el proceso no deja sitio para más.</summary>
public sealed class CuotaSubidaMasivaSuperadaException : Exception;

/// <summary>El archivo declara o trae más que el máximo por archivo.</summary>
public sealed class ArchivoTemporalDemasiadoGrandeException : Exception;
