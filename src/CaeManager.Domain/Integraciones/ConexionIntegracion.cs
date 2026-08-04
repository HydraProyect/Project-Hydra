using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Un buzón de Microsoft 365 conectado a Hydra (P3-33 de docs/business/MATURITY_REVIEW.md
/// — primer conector real, ver ARQUITECTURA-INTEGRACIONES.md § 12). ClienteId
/// null significa que el buzón pertenece al propio Tenant (caso Cliente
/// Directo); poblado significa que es el buzón específico de un Cliente
/// Delegante dentro de una Consultora — el correo que llegue por esta
/// conexión ya tiene Cliente resuelto sin ambigüedad, a diferencia del
/// pipeline de triage de § 12.4 (pensado para un buzón compartido entre
/// varios clientes, escenario que este slice no cubre).
///
/// Simplificación deliberada frente al diseño completo de § 3: no hay
/// ProveedorIntegracion/VersionApiProveedor como catálogo — un único
/// proveedor (Microsoft 365) no lo justifica todavía (YAGNI, ver el propio
/// § 11 del documento: "no construir capacidades especulativas").
/// </summary>
public class ConexionIntegracion : EntidadBase
{
    public const int LongitudMaximaBuzonEmail = 320;
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaUltimoError = 1000;

    public Guid? ClienteId { get; private set; }

    /// <summary>Identificador de display de la conexión: el buzón en Microsoft 365, el número E.164 en WhatsApp (deuda nominal documentada — la clave operativa WhatsApp es LineaWhatsApp.PhoneNumberId).</summary>
    public string BuzonEmail { get; private set; } = string.Empty;

    public string Nombre { get; private set; } = string.Empty;
    public EstadoConexionIntegracion Estado { get; private set; }
    public string? UltimoError { get; private set; }
    public DateTime FechaConectadaUtc { get; private set; }

    /// <summary>Default Microsoft365: todas las conexiones existentes antes del segundo proveedor (WhatsApp, 2026-08-04) eran de Graph.</summary>
    public ProveedorIntegracion Proveedor { get; private set; }

    private ConexionIntegracion()
    {
    }

    public ConexionIntegracion(
        string buzonEmail, string nombre, Guid? clienteId = null,
        ProveedorIntegracion proveedor = ProveedorIntegracion.Microsoft365)
    {
        EstablecerBuzonEmail(buzonEmail);
        EstablecerNombre(nombre);
        ClienteId = clienteId;
        Estado = EstadoConexionIntegracion.Habilitada;
        Proveedor = proveedor;
        FechaConectadaUtc = DateTime.UtcNow;
    }

    public void MarcarConError(string mensaje)
    {
        if (string.IsNullOrWhiteSpace(mensaje))
            throw new ArgumentException("El error debe describir qué falló.", nameof(mensaje));

        Estado = EstadoConexionIntegracion.ConError;
        UltimoError = mensaje.Length > LongitudMaximaUltimoError ? mensaje[..LongitudMaximaUltimoError] : mensaje;
    }

    public void Deshabilitar() => Estado = EstadoConexionIntegracion.Deshabilitada;

    public void Rehabilitar()
    {
        Estado = EstadoConexionIntegracion.Habilitada;
        UltimoError = null;
    }

    private void EstablecerBuzonEmail(string buzonEmail)
    {
        if (string.IsNullOrWhiteSpace(buzonEmail))
            throw new ArgumentException("La conexión debe tener un buzón.", nameof(buzonEmail));

        var normalizado = buzonEmail.Trim();
        if (normalizado.Length > LongitudMaximaBuzonEmail)
            throw new ArgumentException($"El buzón no puede superar {LongitudMaximaBuzonEmail} caracteres.", nameof(buzonEmail));

        BuzonEmail = normalizado;
    }

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("La conexión debe tener un nombre.", nameof(nombre));

        var normalizado = nombre.Trim();
        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException($"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }
}
