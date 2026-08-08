using CaeManager.Domain.Common;

namespace CaeManager.Domain.Cumplimiento;

/// <summary>
/// Registro inmutable de que un usuario aceptó una versión concreta de los
/// Términos y Condiciones + Política de Privacidad. Extiende <see cref="Entity"/>,
/// no <see cref="EntidadConTenant"/>: la aceptación es un hecho sobre el
/// usuario frente a la plataforma, no un dato que pertenezca a un tenant —
/// mismo tratamiento que <see cref="Tenants.DelegacionTenant"/> (catálogo
/// global de autorización, sin <c>HasQueryFilter</c>). Necesario porque un
/// usuario puede operar más de un tenant (Operador Delegado, ADR-004): la
/// aceptación no debe repetirse una vez por cada tenant que visite.
///
/// Nunca se edita ni se borra — un cambio material en los documentos legales
/// (ver <see cref="VersionTerminos"/>) crea una fila nueva por cada
/// aceptación posterior, nunca sobreescribe la anterior; es el mismo
/// criterio append-only que <c>RechazoAcreditacionDocumentoPlataforma</c>.
/// </summary>
public class AceptacionTerminos : Entity
{
    public const int LongitudMaximaVersionDocumento = 20;

    public Guid UsuarioId { get; private set; }
    public string VersionDocumento { get; private set; } = string.Empty;
    public DateTime FechaAceptacionUtc { get; private set; }

    private AceptacionTerminos()
    {
    }

    public AceptacionTerminos(Guid usuarioId, string versionDocumento, DateTime fechaAceptacionUtc)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("La aceptación debe pertenecer a un usuario.", nameof(usuarioId));
        if (string.IsNullOrWhiteSpace(versionDocumento))
            throw new ArgumentException("La versión del documento aceptado es obligatoria.", nameof(versionDocumento));

        var normalizada = versionDocumento.Trim();
        if (normalizada.Length > LongitudMaximaVersionDocumento)
            throw new ArgumentException($"La versión no puede superar {LongitudMaximaVersionDocumento} caracteres.", nameof(versionDocumento));

        UsuarioId = usuarioId;
        VersionDocumento = normalizada;
        FechaAceptacionUtc = fechaAceptacionUtc;
    }
}
