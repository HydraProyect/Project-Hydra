using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Satélite 1:1 de <see cref="ConexionIntegracion"/> con lo específico de una
/// línea de WhatsApp Cloud API — mismo patrón que
/// <see cref="CredencialIntegracion"/> lo es del buzón Microsoft 365.
///
/// TokenAcceso es un System User token de larga duración de Meta (no un
/// refresh token OAuth — no rota en cada uso, se sustituye manualmente):
/// por eso no reutiliza CredencialIntegracion, modelada y nombrada para el
/// canje OAuth de Graph. Igual criterio que CredencialAccesoPortal: el
/// dominio modela el valor en texto plano en memoria; el cifrado en reposo
/// es un ValueConverter de EF Core en Infrastructure.
///
/// PhoneNumberId es la clave con la que el webhook de Meta (una única URL de
/// callback para todas las líneas de la app) identifica la línea receptora —
/// su índice único es GLOBAL, sin TenantId, porque se usa para RESOLVER el
/// tenant (mismo criterio que ClaveApi.HashClave).
/// </summary>
public class LineaWhatsApp : EntidadConTenant
{
    public const int LongitudMaximaPhoneNumberId = 50;
    public const int LongitudMaximaWabaId = 50;
    public const int LongitudMaximaNumeroTelefono = 20;
    public const int LongitudMaximaMensajeAutoTriage = 1000;

    private readonly List<MiembroPoolLinea> _miembrosPool = [];

    public Guid ConexionIntegracionId { get; private set; }
    public string PhoneNumberId { get; private set; } = string.Empty;
    public string WabaId { get; private set; } = string.Empty;

    /// <summary>Número en formato E.164 (p. ej. +34686543364) — solo informativo/UI; la clave operativa es PhoneNumberId.</summary>
    public string NumeroTelefono { get; private set; } = string.Empty;

    public string TokenAcceso { get; private set; } = string.Empty;
    public ModoAsignacionLinea Modo { get; private set; }

    /// <summary>Comercial dueño de la línea — requerido en modo GestorFijo, null en PoolInbound.</summary>
    public Guid? ComercialAsignadoId { get; private set; }

    /// <summary>
    /// Texto que se envía automáticamente al contacto cuando una conversación
    /// nueva no identifica a ningún Cliente (pidiéndole indicar de qué cliente
    /// y consulta se trata). Null = auto-mensaje desactivado. Enviarlo es
    /// gratis: el mensaje entrante del contacto acaba de abrir la ventana de
    /// servicio de 24 h de Meta.
    /// </summary>
    public string? MensajeAutoTriage { get; private set; }

    public IReadOnlyList<MiembroPoolLinea> MiembrosPool => _miembrosPool.AsReadOnly();

    private LineaWhatsApp()
    {
    }

    public LineaWhatsApp(
        Guid conexionIntegracionId, string phoneNumberId, string wabaId, string numeroTelefono, string tokenAcceso,
        ModoAsignacionLinea modo, Guid? comercialAsignadoId = null, string? mensajeAutoTriage = null)
    {
        if (conexionIntegracionId == Guid.Empty)
            throw new ArgumentException("La línea debe pertenecer a una conexión.", nameof(conexionIntegracionId));

        ConexionIntegracionId = conexionIntegracionId;
        EstablecerPhoneNumberId(phoneNumberId);
        EstablecerWabaId(wabaId);
        EstablecerNumeroTelefono(numeroTelefono);
        ActualizarToken(tokenAcceso);
        ConfigurarAsignacion(modo, comercialAsignadoId);
        EstablecerMensajeAutoTriage(mensajeAutoTriage);
    }

    /// <summary>El System User token no rota solo — se sustituye entero cuando el administrador lo renueva en Meta.</summary>
    public void ActualizarToken(string nuevoToken)
    {
        if (string.IsNullOrWhiteSpace(nuevoToken))
            throw new ArgumentException("El token de acceso no puede estar vacío.", nameof(nuevoToken));

        TokenAcceso = nuevoToken.Trim();
    }

    public void ConfigurarAsignacion(ModoAsignacionLinea modo, Guid? comercialAsignadoId)
    {
        if (modo == ModoAsignacionLinea.GestorFijo && (comercialAsignadoId is null || comercialAsignadoId == Guid.Empty))
            throw new ArgumentException("Una línea en modo GestorFijo necesita un comercial dueño.", nameof(comercialAsignadoId));

        Modo = modo;
        ComercialAsignadoId = modo == ModoAsignacionLinea.GestorFijo ? comercialAsignadoId : null;
    }

    public void EstablecerMensajeAutoTriage(string? mensaje)
    {
        var normalizado = string.IsNullOrWhiteSpace(mensaje) ? null : mensaje.Trim();
        if (normalizado is not null && normalizado.Length > LongitudMaximaMensajeAutoTriage)
            throw new ArgumentException($"El mensaje automático no puede superar {LongitudMaximaMensajeAutoTriage} caracteres.", nameof(mensaje));

        MensajeAutoTriage = normalizado;
    }

    /// <summary>Reemplaza la lista completa de miembros del pool — la pantalla de edición envía siempre el conjunto final.</summary>
    public void ReemplazarMiembrosPool(IEnumerable<Guid> usuarioIds)
    {
        var unicos = usuarioIds.Where(id => id != Guid.Empty).Distinct().ToList();
        _miembrosPool.RemoveAll(m => !unicos.Contains(m.UsuarioId));
        foreach (var usuarioId in unicos.Where(id => _miembrosPool.All(m => m.UsuarioId != id)))
            _miembrosPool.Add(new MiembroPoolLinea(Id, usuarioId));
    }

    private void EstablecerPhoneNumberId(string phoneNumberId)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId))
            throw new ArgumentException("La línea debe tener el PhoneNumberId de Meta.", nameof(phoneNumberId));

        var normalizado = phoneNumberId.Trim();
        if (normalizado.Length > LongitudMaximaPhoneNumberId)
            throw new ArgumentException($"El PhoneNumberId no puede superar {LongitudMaximaPhoneNumberId} caracteres.", nameof(phoneNumberId));

        PhoneNumberId = normalizado;
    }

    private void EstablecerWabaId(string wabaId)
    {
        if (string.IsNullOrWhiteSpace(wabaId))
            throw new ArgumentException("La línea debe tener el WABA Id de Meta.", nameof(wabaId));

        var normalizado = wabaId.Trim();
        if (normalizado.Length > LongitudMaximaWabaId)
            throw new ArgumentException($"El WABA Id no puede superar {LongitudMaximaWabaId} caracteres.", nameof(wabaId));

        WabaId = normalizado;
    }

    private void EstablecerNumeroTelefono(string numeroTelefono)
    {
        if (string.IsNullOrWhiteSpace(numeroTelefono))
            throw new ArgumentException("La línea debe tener un número de teléfono.", nameof(numeroTelefono));

        var normalizado = numeroTelefono.Trim();
        if (normalizado.Length > LongitudMaximaNumeroTelefono)
            throw new ArgumentException($"El número no puede superar {LongitudMaximaNumeroTelefono} caracteres.", nameof(numeroTelefono));

        NumeroTelefono = normalizado;
    }
}
