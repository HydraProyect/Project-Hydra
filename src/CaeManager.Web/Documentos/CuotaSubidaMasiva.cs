using Microsoft.Extensions.Options;

namespace CaeManager.Web.Documentos;

/// <summary>
/// Contabilidad compartida por todo el proceso de lo que retiene la Subida
/// múltiple: el tope global y el de cada Tenant de origen
/// (<see cref="OpcionesSubidaMasiva"/>). Cada pantalla abierta pide aquí su
/// <see cref="AlmacenTemporalSubidaMasiva"/>, que reserva contra estos topes antes
/// de escribir nada y lo devuelve al borrar.
///
/// <para>
/// <b>Temporales de un proceso que ya no existe.</b> Cada proceso escribe bajo su
/// propio subdirectorio de la raíz y mantiene abierto en exclusiva un fichero
/// testigo (<c>.vivo</c>) mientras vive. Al crearse, borra los subdirectorios de
/// la raíz cuyo testigo puede abrir en exclusiva: su proceso terminó sin limpiar
/// (caída, reinicio del contenedor). El de un proceso vivo —otra réplica o el
/// relevo azul/verde con la raíz compartida— no se toca porque su testigo sigue
/// bloqueado.
/// </para>
/// </summary>
public sealed class CuotaSubidaMasiva : IDisposable
{
    private const string NombreTestigo = ".vivo";

    private readonly OpcionesSubidaMasiva _opciones;
    private readonly object _cerrojo = new();
    private readonly Dictionary<Guid, long> _porTenant = [];
    private readonly FileStream _testigo;
    private long _global;

    public CuotaSubidaMasiva(IOptions<OpcionesSubidaMasiva> opciones)
    {
        _opciones = opciones.Value;
        var raiz = string.IsNullOrWhiteSpace(_opciones.DirectorioTemporal)
            ? Path.Combine(Path.GetTempPath(), "talveg-subida-masiva")
            : _opciones.DirectorioTemporal;

        CrearDirectorioPrivado(raiz);
        DirectorioProceso = Path.Combine(raiz, Guid.NewGuid().ToString("N"));
        CrearDirectorioPrivado(DirectorioProceso);
        _testigo = new FileStream(Path.Combine(DirectorioProceso, NombreTestigo),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        try
        {
            BorrarDirectoriosHuerfanos(raiz);
        }
        catch
        {
            // Sin esto, cada reintento dejaría un testigo bloqueado hasta que muera el proceso.
            Dispose();
            throw;
        }
    }

    /// <summary>Subdirectorio de este proceso: los almacenes de cada pantalla cuelgan de él.</summary>
    public string DirectorioProceso { get; }

    public long MaximoBytesPorCircuito => _opciones.MaximoBytesPorCircuito;

    /// <summary>Bytes reservados ahora mismo en todo el proceso.</summary>
    public long ReservadoGlobal
    {
        get { lock (_cerrojo) return _global; }
    }

    public long ReservadoDe(Guid tenantId)
    {
        lock (_cerrojo) return _porTenant.GetValueOrDefault(tenantId);
    }

    /// <summary>Un almacén por pantalla abierta; la pantalla lo desecha al cerrarse.</summary>
    public AlmacenTemporalSubidaMasiva CrearAlmacen(Guid? tenantOrigenId) =>
        new(this, tenantOrigenId ?? Guid.Empty);

    internal bool TryReservar(Guid tenantId, long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_cerrojo)
        {
            var delTenant = _porTenant.GetValueOrDefault(tenantId);
            if (_global + bytes > _opciones.MaximoBytesGlobal || delTenant + bytes > _opciones.MaximoBytesPorTenant)
                return false;

            _global += bytes;
            _porTenant[tenantId] = delTenant + bytes;
            return true;
        }
    }

    internal void Liberar(Guid tenantId, long bytes)
    {
        if (bytes <= 0) return;
        lock (_cerrojo)
        {
            _global = Math.Max(0, _global - bytes);
            var restante = Math.Max(0, _porTenant.GetValueOrDefault(tenantId) - bytes);
            if (restante == 0) _porTenant.Remove(tenantId);
            else _porTenant[tenantId] = restante;
        }
    }

    internal static void CrearDirectorioPrivado(string ruta)
    {
        // Solo el usuario del proceso: lo que se escribe son documentos de
        // Trabajador (a veces de naturaleza médica) pendientes de confirmar.
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(ruta);
        else
            Directory.CreateDirectory(ruta, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void BorrarDirectoriosHuerfanos(string raiz)
    {
        foreach (var directorio in Directory.EnumerateDirectories(raiz))
        {
            // Solo los subdirectorios que esta clase crea (un Guid en formato N). La raíz es
            // configurable: si apuntara por error a un volumen con otros datos, nada que no
            // sea nuestro se borra.
            if (string.Equals(directorio, DirectorioProceso, StringComparison.Ordinal)
                || !Guid.TryParseExact(Path.GetFileName(directorio), "N", out _))
                continue;

            try
            {
                // Si el testigo se abre en exclusiva, nadie lo tiene: su proceso
                // terminó. Un directorio sin testigo es igual de huérfano, salvo
                // si es reciente: puede ser otro proceso que arranca y aún no lo
                // ha creado.
                var testigo = Path.Combine(directorio, NombreTestigo);
                if (File.Exists(testigo))
                    using (new FileStream(testigo, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                else if (Directory.GetLastWriteTimeUtc(directorio) > DateTime.UtcNow.AddMinutes(-5))
                    continue;

                Directory.Delete(directorio, recursive: true);
            }
            catch (IOException)
            {
                // Testigo bloqueado: el proceso vive.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
        _testigo.Dispose();
        try
        {
            Directory.Delete(DirectorioProceso, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
