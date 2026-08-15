using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Firma habitual de un usuario, configurada una vez y reutilizada en cada
/// firma en campo (plan Fase A) — un usuario, una firma guardada: se
/// reemplaza al re-guardar, no se acumulan versiones. El origen de la
/// imagen (dibujada o subida) no se persiste: no aporta nada al uso
/// posterior, solo importa el PNG resultante.
/// </summary>
public class FirmaGuardadaUsuario : EntidadConTenant
{
    public const int LongitudMaximaImagenUrl = 500;

    public Guid UsuarioId { get; private set; }
    public string ImagenUrl { get; private set; } = string.Empty;
    public DateTime ActualizadaEnUtc { get; private set; }

    private FirmaGuardadaUsuario()
    {
    }

    public FirmaGuardadaUsuario(Guid usuarioId, string imagenUrl, DateTime actualizadaEnUtc)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("La firma guardada debe pertenecer a un usuario.", nameof(usuarioId));
        if (string.IsNullOrWhiteSpace(imagenUrl))
            throw new ArgumentException("La URL de la imagen no puede estar vacía.", nameof(imagenUrl));

        UsuarioId = usuarioId;
        ImagenUrl = imagenUrl;
        ActualizadaEnUtc = actualizadaEnUtc;
    }

    public void Reemplazar(string imagenUrl, DateTime actualizadaEnUtc)
    {
        if (string.IsNullOrWhiteSpace(imagenUrl))
            throw new ArgumentException("La URL de la imagen no puede estar vacía.", nameof(imagenUrl));

        ImagenUrl = imagenUrl;
        ActualizadaEnUtc = actualizadaEnUtc;
    }
}
