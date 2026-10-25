using System.Net;
using CaeManager.Application.Seguridad.InicioSesion;

namespace CaeManager.Infrastructure.Seguridad;

/// <summary>Sección <c>Seguridad:InicioSesion</c> de la configuración.</summary>
public sealed class OpcionesInicioSesionSeguro
{
    public const string Seccion = "Seguridad:InicioSesion";

    /// <summary>
    /// Redes desde las que se cree <c>X-Forwarded-For</c> (el proxy de borde).
    /// Vacía = no se cree nunca y la IP es la del par TCP. Por defecto
    /// (<c>appsettings.json</c>) las privadas y loopback: Caddy habla desde la
    /// red de Docker y la aplicación no publica puerto, así que nadie de fuera
    /// es par directo; lo que un cliente escriba en la cabecera queda a la
    /// izquierda de la entrada que añade Caddy y no se lee.
    /// </summary>
    public string[] ProxiesDeConfianza { get; set; } = [];

    /// <summary>Fichero MMDB de DB-IP «IP to City Lite». Si no existe, el país es desconocido.</summary>
    public string RutaBaseGeolocalizacion { get; set; } = "/data/geoip/dbip-city-lite.mmdb";

    public int DiasPaisReciente { get; set; } = 90;

    public string PaisHabitualPorDefecto { get; set; } = "ES";

    public double DistanciaMinimaViajeKm { get; set; } = 500;

    public double VelocidadMaximaKmH { get; set; } = 1000;

    public int UmbralRafagaFallos { get; set; } = 10;

    public int MinutosVentanaRafaga { get; set; } = 15;

    /// <summary>Vigencia del enlace «no he sido yo» del correo de aviso.</summary>
    public int DiasVigenciaEnlaceNoHeSidoYo { get; set; } = 7;

    private IReadOnlyList<IPNetwork>? _redes;

    public IReadOnlyList<IPNetwork> RedesDeConfianza() => _redes ??= IpClienteDeConfianza.LeerRedes(ProxiesDeConfianza);

    public OpcionesDeteccionInicioSesion Deteccion() => new()
    {
        DiasPaisReciente = DiasPaisReciente,
        PaisHabitualPorDefecto = PaisHabitualPorDefecto,
        DistanciaMinimaViajeKm = DistanciaMinimaViajeKm,
        VelocidadMaximaKmH = VelocidadMaximaKmH,
        UmbralRafagaFallos = UmbralRafagaFallos,
        MinutosVentanaRafaga = MinutosVentanaRafaga,
    };
}

/// <summary>
/// Par TCP y <c>X-Forwarded-For</c> de la petición tal como llegaron, antes de
/// que <c>UseForwardedHeaders</c> los reescriba. Lo deja en las features de la
/// petición el middleware que <c>Program.cs</c> monta justo antes.
/// </summary>
public sealed record OrigenConexionCapturado(IPAddress? ParConexion, string? XForwardedFor);
