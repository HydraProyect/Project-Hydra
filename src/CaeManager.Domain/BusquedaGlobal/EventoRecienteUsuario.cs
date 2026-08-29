using CaeManager.Domain.Common;

namespace CaeManager.Domain.BusquedaGlobal;

/// <summary>
/// Un elemento seleccionado desde el Command Palette (Ctrl/Cmd+K) — entidad
/// abierta o acción ejecutada — que alimenta el grupo "Recientes" del propio
/// palette. Deliberadamente no se llama "visto": solo se escribe cuando el
/// usuario selecciona algo DESDE el palette (ver BuscadorGlobal.razor.cs), no
/// cuando navega directamente a una ficha por otro camino, así que "reciente"
/// describe uso del palette, no visitas a la app en general.
/// </summary>
public class EventoRecienteUsuario : EntidadConTenant
{
    public Guid UsuarioId { get; private set; }
    public string Tipo { get; private set; } = string.Empty;
    public Guid? EntidadId { get; private set; }
    public string Titulo { get; private set; } = string.Empty;
    public string? Subtitulo { get; private set; }
    public string UrlDestino { get; private set; } = string.Empty;
    public DateTime OcurridoEnUtc { get; private set; }

    private EventoRecienteUsuario()
    {
    }

    public EventoRecienteUsuario(Guid usuarioId, string tipo, Guid? entidadId, string titulo, string? subtitulo, string urlDestino)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El evento reciente debe pertenecer a un usuario.", nameof(usuarioId));
        if (string.IsNullOrWhiteSpace(tipo))
            throw new ArgumentException("El evento reciente debe tener un tipo.", nameof(tipo));
        if (string.IsNullOrWhiteSpace(titulo))
            throw new ArgumentException("El evento reciente debe tener un título.", nameof(titulo));
        if (string.IsNullOrWhiteSpace(urlDestino))
            throw new ArgumentException("El evento reciente debe tener una URL de destino.", nameof(urlDestino));

        UsuarioId = usuarioId;
        Tipo = tipo;
        EntidadId = entidadId;
        Titulo = titulo;
        Subtitulo = subtitulo;
        UrlDestino = urlDestino;
        OcurridoEnUtc = DateTime.UtcNow;
    }
}
