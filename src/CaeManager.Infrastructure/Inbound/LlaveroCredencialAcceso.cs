using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Inbound;

/// <summary>
/// Llavero de las claves ECDSA P-256 con las que se firma la credencial de acceso (AF-03). Una activa y, tras una
/// rotación, las anteriores que aún tienen credenciales vivas: una clave anterior solo verifica y se retira en la
/// primera rotación posterior a la mayor fecha máxima que firmó (plan AF-03: conviven «hasta la mayor fecha máxima
/// emitida»). Cada clave lleva su <c>kid</c>.
///
/// La clave privada vive fuera del repositorio y de la base de datos: en un fichero JSON dentro del directorio del
/// llavero de Data Protection, con la privada protegida por Data Protection (que a su vez se cifra en reposo con el
/// certificado o KMS que ya configura <c>RegistroDataProtection</c>). La pública va en claro (no es secreta), pero el
/// conjunto de claves de confianza lleva una marca de integridad protegida por Data Protection: sin Data Protection ni se
/// firma ni se verifica, y un JSON manipulado se rechaza (no impide volver a una versión anterior del fichero: tras restaurar un backup posterior a una rotación de emergencia hay que repetirla). Sin tabla nueva ni migración.
///
/// Varios procesos (durante un relevo azul/verde conviven dos contenedores y el migrador monta el mismo volumen):
/// toda escritura toma un cerrojo de fichero, relee el estado del disco y escribe con un temporal único; las lecturas
/// recargan si el fichero cambió. Límite conocido: sigue siendo un fichero local; con el llavero de Data Protection en
/// S3 o con réplicas en máquinas distintas hace falta otro almacén (un incremento propio).
///
/// Fallo cerrado: un fichero ilegible NO se regenera —se emitiría con otra clave y todo lo ya impreso dejaría de
/// valer sin que nadie lo supiera—; se lanza <see cref="InvalidOperationException"/> y la verificación da «no válida».
/// </summary>
public sealed class LlaveroCredencialAcceso
{
    /// <summary><b>No renombrar</b>: dejaría ilegibles las privadas ya guardadas.</summary>
    public const string PropositoProtector = "CaeManager.Inbound.CredencialAcceso.Llavero.v1";

    private static readonly JsonSerializerOptions OpcionesJson = new() { WriteIndented = true };

    private readonly string _ruta;
    private readonly IDataProtector _protector;
    private readonly ILogger? _registrador;
    private readonly object _cerrojo = new();
    private List<ClaveLlavero>? _claves;
    private (bool Existe, DateTime Escritura, long Longitud) _sello;
    private (bool Existe, DateTime Escritura, long Longitud) _selloAvisado;

    public LlaveroCredencialAcceso(string rutaFichero, IDataProtectionProvider proveedor, ILogger? registrador = null)
    {
        _ruta = rutaFichero;
        _protector = proveedor.CreateProtector(PropositoProtector);
        _registrador = registrador;
    }

    /// <summary><c>PrivadaProtegida</c> queda vacía al pasar a anterior: ya solo verifica.</summary>
    internal sealed record ClaveLlavero(
        string Kid, bool Activa, DateTime CreadaUtc, string SpkiBase64, string PrivadaProtegida, DateOnly? MaxFechaFirmada);

    internal sealed record FicheroLlavero(int Version, List<ClaveLlavero> Claves, string? Integridad);

    /// <summary>Claves que verifican hoy: la activa y las anteriores con credenciales vivas.</summary>
    internal IReadOnlyList<ClaveLlavero> Vigentes()
    {
        lock (_cerrojo)
            return [.. Refrescar()];
    }

    /// <summary>
    /// La clave activa y su privada en claro (el llamador la desecha), y anota que ha firmado hasta
    /// <paramref name="fechaMaxima"/>. Crea la primera clave si el llavero está vacío.
    /// </summary>
    internal (string Kid, ECDsa Privada) ActivaParaFirmar(DateOnly fechaMaxima)
    {
        lock (_cerrojo)
        {
            ClaveLlavero activa = null!;
            Mutar(claves =>
            {
                var actual = claves.FirstOrDefault(c => c.Activa);
                if (actual is null)
                {
                    actual = NuevaClave();
                    claves.Add(actual);
                    _registrador?.LogWarning(
                        "Se crea la primera clave de firma de credenciales de acceso (kid {Kid}). Si ya había credenciales emitidas, " +
                        "el llavero se perdió (¿volumen, ruta o restauración de backup?) y dejan de valer.", actual.Kid);
                }

                if (actual.MaxFechaFirmada is null || actual.MaxFechaFirmada < fechaMaxima)
                    actual = actual with { MaxFechaFirmada = fechaMaxima };

                claves[claves.FindIndex(c => c.Activa)] = actual;
                activa = actual;
                return true;
            });

            var ecdsa = ECDsa.Create();
            try
            {
                ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(_protector.Unprotect(activa.PrivadaProtegida)), out _);
                return (activa.Kid, ecdsa);
            }
            catch
            {
                ecdsa.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Nace una activa nueva; la activa pasa a anterior y se conservan las anteriores que aún firmaron fechas no
    /// vencidas (<paramref name="hoy"/>, el día de negocio de Europe/Madrid, el mismo con el que se compara la fecha máxima); el resto se retira.
    /// </summary>
    public void Rotar(DateOnly hoy)
    {
        lock (_cerrojo)
        {
            Mutar(claves =>
            {
                var conservadas = claves
                    .Where(c => c.Activa || c.MaxFechaFirmada is { } max && max >= hoy)
                    .Select(c => c with { Activa = false, PrivadaProtegida = "" });
                var nuevas = new List<ClaveLlavero> { NuevaClave() };
                nuevas.AddRange(conservadas);
                claves.Clear();
                claves.AddRange(nuevas);
                return true;
            });
        }
    }

    public void RotarDeEmergencia()
    {
        lock (_cerrojo)
        {
            // Aunque el fichero actual sea ilegible: la emergencia lo sustituye entero.
            using var candado = TomarCandado();
            Guardar([NuevaClave()]);
        }
    }

    private ClaveLlavero NuevaClave()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var kid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        return new ClaveLlavero(
            kid, Activa: true, DateTime.UtcNow,
            Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
            _protector.Protect(Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey())),
            MaxFechaFirmada: null);
    }

    /// <summary>Cerrojo entre procesos, relectura del disco, mutación sobre una copia y escritura; la caché solo se actualiza si se guardó.</summary>
    private void Mutar(Func<List<ClaveLlavero>, bool> cambio)
    {
        using var candado = TomarCandado();
        var copia = new List<ClaveLlavero>(LeerDeDisco());
        if (cambio(copia))
        {
            if (!CambioReal(copia))
                return;
            Guardar(copia);
        }
    }

    private bool CambioReal(List<ClaveLlavero> copia) =>
        _claves is null || !copia.SequenceEqual(_claves);

    private FileStream TomarCandado()
    {
        var directorio = Path.GetDirectoryName(_ruta);
        if (!string.IsNullOrEmpty(directorio))
            Directory.CreateDirectory(directorio);

        var limite = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(_ruta + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < limite)
            {
                Thread.Sleep(25);
            }
        }
    }

    private List<ClaveLlavero> Refrescar()
    {
        var info = new FileInfo(_ruta);
        var sello = (info.Exists, info.Exists ? info.LastWriteTimeUtc : default, info.Exists ? info.Length : 0L);
        if (_claves is not null && sello == _sello)
            return _claves;

        return LeerDeDisco();
    }

    private List<ClaveLlavero> LeerDeDisco()
    {
        var info = new FileInfo(_ruta);
        _sello = (info.Exists, info.Exists ? info.LastWriteTimeUtc : default, info.Exists ? info.Length : 0L);

        if (!info.Exists)
            return _claves = [];

        try
        {
            var fichero = JsonSerializer.Deserialize<FicheroLlavero>(File.ReadAllText(_ruta), OpcionesJson);
            if (fichero is null || fichero.Version != 1 || fichero.Claves is null
                || fichero.Claves.Any(c => c is null || c.Kid is null || c.SpkiBase64 is null || c.PrivadaProtegida is null
                                           || !FormatoTokenCredencial.EsKidValido(c.Kid))
                || fichero.Claves.Count(c => c.Activa) > 1
                || fichero.Claves.Select(c => c.Kid).Distinct().Count() != fichero.Claves.Count
                || !IntegridadValida(fichero))
                throw new InvalidOperationException("El llavero de credenciales de acceso tiene un contenido no válido.");

            return _claves = fichero.Claves;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or CryptographicException or FormatException)
        {
            _claves = null;
            if (_selloAvisado != _sello) // un aviso por versión ilegible del fichero, no uno por cada lectura de QR
            {
                _selloAvisado = _sello;
                _registrador?.LogError(ex,
                "El llavero de credenciales de acceso ({Ruta}) no se puede leer: toda verificación se deniega y no se firma hasta repararlo.",
                _ruta);
            }
            throw ex is InvalidOperationException
                ? ex
                : new InvalidOperationException("El llavero de credenciales de acceso no se puede leer.", ex);
        }
    }

    /// <summary>
    /// Resumen de lo que decide en quién se confía (kid, estado, pública y fecha). Se guarda protegido por Data Protection:
    /// quien solo pueda escribir el JSON no puede colar una pública ajena sin que la carga lo detecte (fallo cerrado).
    /// </summary>
    private static string ResumenDe(IEnumerable<ClaveLlavero> claves) =>
        Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n',
            claves.OrderBy(c => c.Kid, StringComparer.Ordinal).Select(c =>
                $"{c.Kid}|{c.Activa}|{c.SpkiBase64}|{c.MaxFechaFirmada?.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}")))));

    private bool IntegridadValida(FicheroLlavero fichero) =>
        fichero.Integridad is not null
        && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(_protector.Unprotect(fichero.Integridad)),
            System.Text.Encoding.UTF8.GetBytes(ResumenDe(fichero.Claves)));

    private void Guardar(List<ClaveLlavero> claves)
    {
        var temporal = _ruta + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + ".tmp";
        try
        {
            using (var salida = new FileStream(temporal, FileMode.CreateNew, FileAccess.Write))
            {
                salida.Write(System.Text.Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new FicheroLlavero(1, claves, _protector.Protect(ResumenDe(claves))), OpcionesJson)));
                salida.Flush(flushToDisk: true); // sin esto un corte de luz podría dejar el fichero vacío o con la versión anterior
            }

            File.Move(temporal, _ruta, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporal); } catch (IOException) { }
            throw;
        }

        _claves = claves;
        var info = new FileInfo(_ruta);
        _sello = (true, info.LastWriteTimeUtc, info.Length);
    }
}
