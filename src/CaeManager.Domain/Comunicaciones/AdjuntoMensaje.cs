using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Un archivo adjunto a un <see cref="Mensaje"/> — entrante (recibido
/// de verdad por Graph) o saliente (subido por un Gestor CAE al responder).
/// El contenido en sí vive en <c>IFileStorageService</c> (mismo servicio que
/// los PDFs de Documento, ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 12.3: "no un
/// storage paralelo") — <see cref="ArchivoUrl"/> es el identificador opaco
/// que devuelve ese servicio, nunca la ruta real ni una URL pública.
/// </summary>
public class AdjuntoMensaje : EntidadConTenant, IVersionable
{
    public const int LongitudMaximaNombreArchivo = 260;
    public const int LongitudMaximaTipoContenido = 150;
    public const int LongitudMaximaArchivoUrl = 500;
    public const int LongitudMaximaMotivoRechazo = 500;

    public Guid MensajeId { get; private set; }
    public string NombreArchivo { get; private set; } = string.Empty;
    public string TipoContenido { get; private set; } = string.Empty;
    public long TamanoBytes { get; private set; }
    public string ArchivoUrl { get; private set; } = string.Empty;

    /// <summary>Quien reparte y el marcado tras el alta pueden coincidir: la transición de estado no pisa a la otra.</summary>
    public Guid Version { get; private set; } = Guid.NewGuid();

    public EstadoRepartoAdjunto EstadoReparto { get; private set; }

    /// <summary>Destino propuesto: un Trabajador o una Empresa, nunca los dos. Asignar lo convierte en Documento.</summary>
    public Guid? DestinoTrabajadorId { get; private set; }
    public Guid? DestinoEmpresaId { get; private set; }
    public Guid? TipoDocumentoPropuestoId { get; private set; }

    public string? MotivoRechazo { get; private set; }
    public Guid? ResueltoPorUsuarioId { get; private set; }
    public DateTime? ResueltoEnUtc { get; private set; }

    /// <summary>El Documento que salió de este adjunto, si se asignó.</summary>
    public Guid? DocumentoId { get; private set; }

    public bool EstaPendiente => EstadoReparto == EstadoRepartoAdjunto.Pendiente;

    private AdjuntoMensaje()
    {
    }

    public AdjuntoMensaje(Guid mensajeId, string nombreArchivo, string tipoContenido, long tamanoBytes, string archivoUrl)
    {
        if (mensajeId == Guid.Empty)
            throw new ArgumentException("El adjunto debe pertenecer a un mensaje.", nameof(mensajeId));
        if (string.IsNullOrWhiteSpace(nombreArchivo))
            throw new ArgumentException("El adjunto debe tener un nombre de archivo.", nameof(nombreArchivo));
        if (nombreArchivo.Length > LongitudMaximaNombreArchivo)
            throw new ArgumentException($"El nombre del archivo no puede superar {LongitudMaximaNombreArchivo} caracteres.", nameof(nombreArchivo));
        if (string.IsNullOrWhiteSpace(tipoContenido))
            throw new ArgumentException("El adjunto debe tener un tipo de contenido.", nameof(tipoContenido));
        if (tipoContenido.Length > LongitudMaximaTipoContenido)
            throw new ArgumentException($"El tipo de contenido no puede superar {LongitudMaximaTipoContenido} caracteres.", nameof(tipoContenido));
        if (tamanoBytes < 0)
            throw new ArgumentException("El tamaño no puede ser negativo.", nameof(tamanoBytes));
        if (string.IsNullOrWhiteSpace(archivoUrl))
            throw new ArgumentException("El adjunto debe tener un archivo guardado.", nameof(archivoUrl));
        if (archivoUrl.Length > LongitudMaximaArchivoUrl)
            throw new ArgumentException($"La referencia de archivo no puede superar {LongitudMaximaArchivoUrl} caracteres.", nameof(archivoUrl));

        MensajeId = mensajeId;
        NombreArchivo = nombreArchivo.Trim();
        TipoContenido = tipoContenido.Trim();
        TamanoBytes = tamanoBytes;
        ArchivoUrl = archivoUrl;
    }

    public void FijarDestino(Guid? trabajadorId, Guid? empresaId, Guid? tipoDocumentoId)
    {
        ExigirPendiente();
        if (trabajadorId is not null && empresaId is not null)
            throw new InvalidOperationException("El destino es un Trabajador o una Empresa, no los dos.");

        DestinoTrabajadorId = trabajadorId;
        DestinoEmpresaId = empresaId;
        TipoDocumentoPropuestoId = tipoDocumentoId;
    }

    public void MarcarAsignado(Guid documentoId, Guid? usuarioId, DateTime ahoraUtc)
    {
        ExigirPendiente();
        if (documentoId == Guid.Empty)
            throw new ArgumentException("El adjunto asignado debe apuntar a un Documento.", nameof(documentoId));

        EstadoReparto = EstadoRepartoAdjunto.Asignado;
        DocumentoId = documentoId;
        RegistrarResolucion(usuarioId, ahoraUtc);
    }

    public void Rechazar(string motivo, Guid usuarioId, DateTime ahoraUtc)
    {
        ExigirPendiente();
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Rechazar un adjunto exige un motivo.", nameof(motivo));
        if (motivo.Trim().Length > LongitudMaximaMotivoRechazo)
            throw new ArgumentException($"El motivo no puede superar {LongitudMaximaMotivoRechazo} caracteres.", nameof(motivo));

        EstadoReparto = EstadoRepartoAdjunto.Rechazado;
        MotivoRechazo = motivo.Trim();
        RegistrarResolucion(usuarioId, ahoraUtc);
    }

    public void Descartar(Guid usuarioId, DateTime ahoraUtc)
    {
        ExigirPendiente();
        EstadoReparto = EstadoRepartoAdjunto.Descartado;
        RegistrarResolucion(usuarioId, ahoraUtc);
    }

    /// <summary>Deshace un rechazo o un descarte. Un adjunto asignado no se reabre: su Documento ya existe.</summary>
    public void Reabrir()
    {
        if (EstadoReparto is not (EstadoRepartoAdjunto.Rechazado or EstadoRepartoAdjunto.Descartado))
            throw new InvalidOperationException("Solo se reabre un adjunto rechazado o descartado.");

        EstadoReparto = EstadoRepartoAdjunto.Pendiente;
        MotivoRechazo = null;
        ResueltoPorUsuarioId = null;
        ResueltoEnUtc = null;
    }

    private void ExigirPendiente()
    {
        if (!EstaPendiente)
            throw new InvalidOperationException("Este adjunto ya está resuelto.");
    }

    private void RegistrarResolucion(Guid? usuarioId, DateTime ahoraUtc)
    {
        ResueltoPorUsuarioId = usuarioId;
        ResueltoEnUtc = ahoraUtc;
    }
}
