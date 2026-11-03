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
/// «sin autorización» (vacío o null): un lector vacío en silencio podría alimentar una baja por ausencia. Por lo mismo,
/// si el propietario de la función no evita la RLS, la base lanza una excepción en vez de devolver vacío.
/// </para>
///
/// <para>
/// Cada listado y cada apertura que dan lectura dejan una fila en <c>AccesosLecturaInbound</c> del Tenant de la Empresa
/// contratista, escrita por la propia función. <see cref="DocumentosAutorizadosAsync"/> y
/// <see cref="ArchivoAutorizadoAsync"/> lanzan <see cref="InvalidOperationException"/> si se llaman dentro de una
/// transacción (de EF o ambiental): un rollback del llamante borraría la traza de una lectura que sí se dio.
/// </para>
///
/// <para>
/// Solo la consumen los handlers de <c>ObtenerLecturaInboundQueries</c> y <c>RegistrarEvidenciaInboundCommandHandler</c>, que deciden antes la cartera
/// (<c>ConsumidoresDelLectorInboundAcotadosTests</c>).
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

/// <param name="VigenteHoy">
/// La autorización da lectura hoy (no revocada y dentro de su vigencia, con el día de negocio de Europe/Madrid), calculado
/// en la base con la misma condición que las lecturas.
/// </param>
/// <param name="Trabajadores">Trabajadores asignados que no están eliminados ni anonimizados: los que se pueden leer.</param>
public record AutorizacionInboundDto(
    Guid AutorizacionId,
    DateOnly VigenciaDesde,
    DateOnly? VigenciaHasta,
    bool Revocada,
    bool VigenteHoy,
    bool IncluyeDocumentosEmpresa,
    int Trabajadores);

/// <summary>Un Documento original del Tenant de la Empresa contratista. Sin <c>ArchivoUrl</c> a propósito.</summary>
/// <param name="SoloConclusionAptitud">
/// El tipo del original, en el catálogo del Tenant de la Empresa contratista, es de «solo conclusión de aptitud» (el único
/// tipo de salud que la lectura deja pasar): su evidencia no lleva archivo, solo la conclusión transcrita.
/// </param>
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
    EstadoVigenciaDocumento EstadoVigencia,
    bool SoloConclusionAptitud);

/// <summary>
/// La apertura de un Documento original autorizado, tal como la devuelve <c>app_inbound_archivo_autorizado</c>. Es la única
/// entrada de <c>IAlmacenOriginalesInbound.AbrirOriginalAsync</c>: el Tenant y la referencia viajan juntos desde la base,
/// nunca como un par libre que quien llama pudiera componer.
/// </summary>
/// <param name="TenantPropietarioId">
/// El Tenant propietario del original, el de la Empresa contratista del contrato (<c>v_aut.tenant_contratista</c> de la
/// función): la carpeta y la clave con las que se abre el binario.
/// </param>
/// <param name="SoloConclusionAptitud">
/// El tipo del original es de «solo conclusión de aptitud» en el catálogo de su Tenant propietario, leído en la misma
/// sentencia que da la apertura. El registro de evidencia lo compara con la marca del listado y nunca copia el binario de
/// uno marcado.
/// </param>
public record ArchivoInboundDto(Guid DocumentoId, Guid TenantPropietarioId, string? ArchivoUrl, bool SoloConclusionAptitud);
