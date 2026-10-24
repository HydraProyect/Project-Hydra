using System.Net;
using CaeManager.Application.Seguridad.InicioSesion;
using MaxMind.Db;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Seguridad;

/// <summary>Ubicación aproximada de una IP.</summary>
public sealed record UbicacionIp(string PaisIso, string? Ciudad, double? Latitud, double? Longitud);

public interface IGeolocalizadorIp
{
    /// <summary>Nulo si no hay base, si la IP no es pública o si la base no la ubica.</summary>
    UbicacionIp? Ubicar(IPAddress? ip);
}

/// <summary>
/// Geolocalización en el propio servidor, sin llamar a terceros, sobre la base
/// libre «IP to City Lite» de DB-IP en formato MMDB.
///
/// <para>
/// <b>Licencia.</b> La base es de DB-IP.com y se distribuye con licencia
/// Creative Commons Attribution 4.0 International: uso libre, también
/// comercial, con atribución a DB-IP.com y, en una aplicación web, un enlace a
/// db-ip.com en las páginas que muestran o usan sus resultados
/// (<see cref="Atribucion"/>). No necesita clave ni cuenta. El lector es el
/// paquete <c>MaxMind.Db</c> (Apache-2.0), que solo lee el formato y no exige
/// clave: la clave de MaxMind es para descargar GeoLite2, que aquí no se usa.
/// </para>
///
/// <para>
/// <b>Fichero.</b> No va en el repositorio ni en la imagen (unos 130 MB, y
/// cambia cada mes): lo deja en el volumen de datos
/// (<c>/data/geoip/dbip-city-lite.mmdb</c> por defecto) el guion
/// <c>deploy/local/actualizar-base-geolocalizacion.sh</c>. Si el fichero no
/// está, todo sigue funcionando y el país queda como desconocido. Si cambia en
/// disco, se vuelve a abrir en la siguiente consulta pasados unos minutos.
/// </para>
/// </summary>
public sealed class GeolocalizadorIpDbIp(
    IOptions<OpcionesInicioSesionSeguro> opciones, ILogger<GeolocalizadorIpDbIp> logger) : IGeolocalizadorIp, IDisposable
{
    public const string Atribucion = "Geolocalización IP de DB-IP (https://db-ip.com), licencia CC BY 4.0";

    private static readonly TimeSpan IntervaloComprobacion = TimeSpan.FromMinutes(10);

    private readonly object _cerrojo = new();
    private Reader? _lector;
    private DateTime _modificadoEnUtc;
    private DateTime _comprobadoEnUtc = DateTime.MinValue;

    public UbicacionIp? Ubicar(IPAddress? ip)
    {
        if (ip is null || IpClienteDeConfianza.EsNoPublica(ip))
            return null;

        lock (_cerrojo)
        {
            AsegurarLector();
            if (_lector is null)
                return null;

            try
            {
                var datos = _lector.Find<Dictionary<string, object>>(ip);
                return datos is null ? null : Leer(datos);
            }
            catch (Exception ex) when (ex is InvalidDatabaseException or InvalidCastException or KeyNotFoundException)
            {
                logger.LogWarning(ex, "La base de geolocalización no pudo leer una dirección; se trata como país desconocido.");
                return null;
            }
        }
    }

    private void AsegurarLector()
    {
        var ahora = DateTime.UtcNow;
        if (ahora - _comprobadoEnUtc < IntervaloComprobacion)
            return;
        _comprobadoEnUtc = ahora;

        var ruta = opciones.Value.RutaBaseGeolocalizacion;
        if (string.IsNullOrWhiteSpace(ruta) || !File.Exists(ruta))
        {
            if (_lector is not null)
                logger.LogWarning("La base de geolocalización ya no está en {Ruta}; el país queda como desconocido.", ruta);
            _lector?.Dispose();
            _lector = null;
            return;
        }

        var modificado = File.GetLastWriteTimeUtc(ruta);
        if (_lector is not null && modificado == _modificadoEnUtc)
            return;

        try
        {
            var nuevo = new Reader(ruta, FileAccessMode.MemoryMapped);
            _lector?.Dispose();
            _lector = nuevo;
            _modificadoEnUtc = modificado;
            logger.LogInformation("Base de geolocalización abierta: {Ruta} ({Tipo}, {Fecha:yyyy-MM-dd}).",
                ruta, nuevo.Metadata.DatabaseType, nuevo.Metadata.BuildDate);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo abrir la base de geolocalización {Ruta}; el país queda como desconocido.", ruta);
        }
    }

    private static UbicacionIp? Leer(Dictionary<string, object> datos)
    {
        if (!datos.TryGetValue("country", out var paisObj) || paisObj is not Dictionary<string, object> pais
            || !pais.TryGetValue("iso_code", out var isoObj) || isoObj is not string iso || iso.Length != 2)
            return null;

        string? ciudad = null;
        if (datos.TryGetValue("city", out var ciudadObj) && ciudadObj is Dictionary<string, object> c
            && c.TryGetValue("names", out var nombresObj) && nombresObj is Dictionary<string, object> nombres)
            ciudad = (nombres.TryGetValue("es", out var es) ? es : nombres.TryGetValue("en", out var en) ? en : null) as string;

        double? lat = null, lon = null;
        if (datos.TryGetValue("location", out var locObj) && locObj is Dictionary<string, object> loc)
        {
            lat = loc.TryGetValue("latitude", out var la) ? Convert.ToDouble(la, System.Globalization.CultureInfo.InvariantCulture) : null;
            lon = loc.TryGetValue("longitude", out var lo) ? Convert.ToDouble(lo, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        return new UbicacionIp(iso.ToUpperInvariant(), ciudad, lat, lon);
    }

    public void Dispose()
    {
        lock (_cerrojo)
        {
            _lector?.Dispose();
            _lector = null;
        }
    }
}
