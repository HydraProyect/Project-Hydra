using CaeManager.Domain.Common;

namespace CaeManager.Domain.Notificaciones;

/// <summary>
/// Aviso persistente para un usuario concreto, mostrado como pop-up hasta
/// que lo descarta — a diferencia de un Toast (ToastService, efímero), este
/// sobrevive a recargas de página y a cerrar/reabrir sesión. Dos usos hoy,
/// ambos disparados al reasignar Cliente.EjecutivoUsuarioId (ver
/// ReasignarEjecutivoClienteCommand): aviso de cambio de cartera, y aviso de
/// que un Cliente recién asignado tiene algún TipoDocumento con lectura IA
/// desactivada (con UrlAccion apuntando a la pantalla de configuración).
/// </summary>
public class NotificacionUsuario : EntidadConTenant
{
    public const int LongitudMaximaTitulo = 200;
    public const int LongitudMaximaMensaje = 1000;

    /// <summary>Guid suelto hacia ApplicationUser (Infrastructure.Identity) — mismo patrón que EliminadoPorUsuarioId: Domain no puede referenciarlo directamente.</summary>
    public Guid UsuarioDestinatarioId { get; private set; }

    public string Titulo { get; private set; } = string.Empty;
    public string Mensaje { get; private set; } = string.Empty;
    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;
    public bool Leida { get; private set; }

    /// <summary>Ruta relativa a la que lleva el botón "Gestionar" — null para un aviso puramente informativo (solo "Entendido").</summary>
    public string? UrlAccion { get; private set; }

    public string? TextoAccion { get; private set; }

    private NotificacionUsuario()
    {
    }

    public NotificacionUsuario(Guid usuarioDestinatarioId, string titulo, string mensaje, string? urlAccion = null, string? textoAccion = null)
    {
        if (string.IsNullOrWhiteSpace(titulo))
            throw new ArgumentException("El título de la notificación es obligatorio.", nameof(titulo));
        if (string.IsNullOrWhiteSpace(mensaje))
            throw new ArgumentException("El mensaje de la notificación es obligatorio.", nameof(mensaje));

        UsuarioDestinatarioId = usuarioDestinatarioId;
        Titulo = titulo.Length > LongitudMaximaTitulo ? titulo[..LongitudMaximaTitulo] : titulo;
        Mensaje = mensaje.Length > LongitudMaximaMensaje ? mensaje[..LongitudMaximaMensaje] : mensaje;
        UrlAccion = urlAccion;
        TextoAccion = textoAccion;
    }

    public void MarcarComoLeida() => Leida = true;
}
