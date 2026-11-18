using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Infrastructure.Inbound;

/// <summary>
/// Llavero de las claves ECDSA P-256 con las que se firma la credencial de acceso (AF-03). Una activa y, tras una
/// rotación, la anterior (solo verifica, hasta la siguiente rotación). Cada clave lleva su <c>kid</c>.
///
/// La clave privada vive fuera del repositorio y de la base de datos: en un fichero JSON dentro del directorio del
/// llavero de Data Protection, con la privada protegida por Data Protection (que a su vez se cifra en reposo con el
/// certificado o KMS que ya configura <c>RegistroDataProtection</c>). La pública va en claro: no es secreta y permite
/// verificar aunque Data Protection no pudiera descifrar la privada. Sin tabla nueva ni migración.
///
/// Límite conocido: el fichero es local. Con el llavero de Data Protection en S3 o con varias réplicas hace falta otro
/// almacén (un incremento propio); hoy hay una réplica.
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
    private readonly object _cerrojo = new();
    private List<ClaveLlavero>? _claves;

    public LlaveroCredencialAcceso(string rutaFichero, IDataProtectionProvider proveedor)
    {
        _ruta = rutaFichero;
        _protector = proveedor.CreateProtector(PropositoProtector);
    }

    internal sealed record ClaveLlavero(string Kid, bool Activa, DateTime CreadaUtc, string SpkiBase64, string PrivadaProtegida);

    private sealed record FicheroLlavero(int Version, List<ClaveLlavero> Claves);

    /// <summary>Claves que verifican hoy: la activa y, si existe, la anterior.</summary>
    internal IReadOnlyList<ClaveLlavero> Vigentes()
    {
        lock (_cerrojo)
            return [.. Cargar()];
    }

    /// <summary>La clave activa y su privada en claro (el llamador la desecha); crea la primera si el llavero está vacío.</summary>
    internal (string Kid, ECDsa Privada) ActivaParaFirmar()
    {
        lock (_cerrojo)
        {
            var claves = Cargar();
            var activa = claves.FirstOrDefault(c => c.Activa);
            if (activa is null)
            {
                activa = NuevaClave();
                claves.Add(activa);
                Guardar(claves);
            }

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

    public void Rotar()
    {
        lock (_cerrojo)
        {
            var claves = Cargar();
            var nueva = new List<ClaveLlavero> { NuevaClave() };
            nueva.AddRange(claves.Where(c => c.Activa).Select(c => c with { Activa = false }));
            Guardar(nueva);
        }
    }

    public void RotarDeEmergencia()
    {
        lock (_cerrojo)
        {
            // Aunque el fichero actual sea ilegible: la emergencia lo sustituye entero.
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
            _protector.Protect(Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey())));
    }

    private List<ClaveLlavero> Cargar()
    {
        if (_claves is not null)
            return _claves;

        if (!File.Exists(_ruta))
            return _claves = [];

        try
        {
            var fichero = JsonSerializer.Deserialize<FicheroLlavero>(File.ReadAllText(_ruta), OpcionesJson);
            if (fichero is null || fichero.Version != 1 || fichero.Claves is null
                || fichero.Claves.Count(c => c.Activa) > 1
                || fichero.Claves.Any(c => !FormatoTokenCredencial.EsKidValido(c.Kid)))
                throw new InvalidOperationException("El llavero de credenciales de acceso tiene un contenido no válido.");

            return _claves = fichero.Claves;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("El llavero de credenciales de acceso no se puede leer.", ex);
        }
    }

    private void Guardar(List<ClaveLlavero> claves)
    {
        var directorio = Path.GetDirectoryName(_ruta);
        if (!string.IsNullOrEmpty(directorio))
            Directory.CreateDirectory(directorio);

        var temporal = _ruta + ".tmp";
        File.WriteAllText(temporal, JsonSerializer.Serialize(new FicheroLlavero(1, claves), OpcionesJson));
        File.Move(temporal, _ruta, overwrite: true);
        _claves = claves;
    }
}
