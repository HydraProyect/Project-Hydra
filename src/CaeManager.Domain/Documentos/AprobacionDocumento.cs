using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Registro de una decisión humana sobre la revisión de un Documento.
/// Las aprobaciones automáticas se conservan como registros históricos.
/// </summary>
public class AprobacionDocumento : EntidadConTenant
{
    public Guid DocumentoId { get; private set; }
    public TipoAprobacionDocumento Tipo { get; private set; }
    public int ConfianzaGeneral { get; private set; }

    /// <summary>Quién resolvió — solo tiene valor cuando Tipo es Manual; una aprobación Automática no tiene usuario detrás.</summary>
    public Guid? UsuarioId { get; private set; }

    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private AprobacionDocumento()
    {
    }

    private AprobacionDocumento(Guid documentoId, TipoAprobacionDocumento tipo, int confianzaGeneral, Guid? usuarioId)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La aprobación debe estar ligada a un documento.", nameof(documentoId));
        if (confianzaGeneral is < 0 or > 100)
            throw new ArgumentException("La confianza general debe estar entre 0 y 100.", nameof(confianzaGeneral));
        if (tipo == TipoAprobacionDocumento.Manual && usuarioId is null)
            throw new ArgumentException("Una aprobación manual debe indicar qué usuario la resolvió.", nameof(usuarioId));
        if (tipo == TipoAprobacionDocumento.Automatica && usuarioId is not null)
            throw new ArgumentException("Una aprobación automática no debe llevar usuario.", nameof(usuarioId));

        DocumentoId = documentoId;
        Tipo = tipo;
        ConfianzaGeneral = confianzaGeneral;
        UsuarioId = usuarioId;
    }

    public static AprobacionDocumento CrearAutomatica(Guid documentoId, int confianzaGeneral) =>
        new(documentoId, TipoAprobacionDocumento.Automatica, confianzaGeneral, usuarioId: null);

    public static AprobacionDocumento CrearManual(Guid documentoId, int confianzaGeneral, Guid usuarioId) =>
        new(documentoId, TipoAprobacionDocumento.Manual, confianzaGeneral, usuarioId);
}
