using CaeManager.Domain.Documentos;

namespace CaeManager.Application.ContratosAcceso.LecturaInbound;

/// <summary>
/// Lectura entre Tenants de Inbound M1 (PR 3): lo que el Tenant activo, el del Cliente empresarial, puede leer de los
/// Documentos originales del Tenant de la Empresa contratista por un contrato de acceso suyo. La implementación llama a
/// las funciones <c>SECURITY DEFINER</c> <c>app_inbound_*</c> de la base, que son las que garantizan las dos mitades del
/// apretón de manos: el contrato es del Tenant lector del contexto firmado, está vigente hoy y está vinculado con el
/// Tenant de la Empresa contratista, y ese Tenant concedió una autorización no revocada y vigente hoy a este lector y para
/// este contrato. El Tenant lector y el usuario salen del contexto firmado de la conexión, nunca de un parámetro.
///
/// <para>
/// <b>La función no sustituye a la cartera.</b> Quien llama decide antes la autoridad del usuario dentro de su Tenant
/// (rol y cartera sobre el Cliente empresarial del contrato, <see cref="LecturaInboundAutoridad"/>); la función garantiza el
/// Tenant, el contrato y la autorización. Sin contexto firmado la base responde 42501 (excepción), que es distinto de
/// «sin autorización» (vacío o null): un lector vacío en silencio podría alimentar una baja por ausencia.
/// </para>
///
/// <para>
/// Cada listado y cada apertura que dan lectura dejan una fila en <c>AccesosLecturaInbound</c> del Tenant de la Empresa
/// contratista, escrita por la propia función. Se llama fuera de transacciones de escritura de negocio, para que un
/// rollback del llamante no borre la traza.
/// </para>
/// </summary>
public interface ILectorDocumentosInbound
{
    /// <summary>
    /// La autorización del Tenant de la Empresa contratista para este contrato (la vigente si la hay; si no, la última
    /// concedida), para saber si se puede validar. Null si el contrato no es del Tenant lector, no está vigente hoy, no
    /// está vinculado o no hay autorización para él. No deja traza: no lee ningún Documento.
    /// </summary>
    Task<AutorizacionInboundDto?> AutorizacionDelContratoAsync(Guid contratoAccesoId, CancellationToken cancellationToken = default);

    /// <summary>Metadatos de los Documentos autorizados, sin referencia de archivo. Vacío si no hay autorización aplicable.</summary>
    Task<IReadOnlyList<DocumentoInboundDto>> DocumentosAutorizadosAsync(Guid contratoAccesoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// La referencia del archivo de un Documento autorizado. Null si el Documento no está en el alcance de una autorización
    /// aplicable; un <see cref="ArchivoInboundDto"/> con <see cref="ArchivoInboundDto.ArchivoUrl"/> null si lo está pero no
    /// tiene archivo.
    /// </summary>
    Task<ArchivoInboundDto?> ArchivoAutorizadoAsync(Guid contratoAccesoId, Guid documentoId, CancellationToken cancellationToken = default);
}

public record AutorizacionInboundDto(
    Guid AutorizacionId,
    DateOnly VigenciaDesde,
    DateOnly? VigenciaHasta,
    bool Revocada,
    bool IncluyeDocumentosEmpresa,
    int Trabajadores);

/// <summary>Un Documento original del Tenant de la Empresa contratista. Sin <c>ArchivoUrl</c> a propósito.</summary>
/// <param name="TrabajadorId">Null en un Documento de la Empresa propia del Tenant de la Empresa contratista.</param>
public record DocumentoInboundDto(
    Guid DocumentoId,
    Guid? TrabajadorId,
    string? TrabajadorNombre,
    string? TrabajadorApellidos,
    string? TrabajadorDni,
    string TipoDocumentoNombre,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    EstadoVigenciaDocumento EstadoVigencia);

public record ArchivoInboundDto(Guid DocumentoId, string? ArchivoUrl);
