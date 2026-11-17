namespace CaeManager.Application.Inbound.AutorizacionesLectura.Queries.ObtenerRequerimientosTec;

/// <summary>
/// Lectura inversa X-2 de la demo Inbound: los requisitos que los Tenants TCE piden al Tenant TEC activo por sus contratos de
/// acceso. La implementación llama a <c>app_inbound_tec_requerimientos()</c> (migración <c>AnadeLecturaRequerimientosTec</c>),
/// que toma el Tenant TEC del contexto firmado, nunca de un parámetro, y solo devuelve contratos Activos, vinculados, dentro
/// de sus fechas y con la línea Inbound vigente del Tenant TCE (D10), con las capas del Centro y de las exigencias del
/// Cliente empresarial (D9). Sin contexto firmado la base lanza 42501: «sin contexto» no es «sin requisitos».
///
/// <para>
/// <b>No dice a quién se aplica cada requisito.</b> Eso lo decide quien llama con los datos propios del Tenant TEC (los
/// Trabajadores asignados a la autorización de lectura del contrato). Solo lo consume
/// <see cref="ObtenerRequerimientosTecQueryHandler"/> (<c>ConsumidoresDelLectorInboundAcotadosTests</c>).
/// </para>
/// </summary>
public interface ILectorRequerimientosTec
{
    Task<IReadOnlyList<RequisitoTecDto>> RequisitosAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Una fila de <c>app_inbound_tec_requerimientos()</c>. Ningún campo identifica a una persona (P-4): ni Gestor CAE, ni
/// usuario, ni correo. Los textos de los enumerados viajan como en la base (<c>Entidad</c>: Empresa, Trabajador o
/// VehiculoOMaquina; <c>AmbitoDocumento</c>: GeneralTec, EspecificoTce o EspecificoCentro; <c>CapaPlantilla</c>: DeCentro o
/// ExigenciasClienteEmpresarial).
/// </summary>
/// <param name="TipoDocumentoNombre">El nombre exacto del tipo en el catálogo del Tenant TCE (D1).</param>
/// <param name="SePideA">
/// Todos, Nacionales o Extranjeros. Este incremento no lo evalúa (Trabajador extranjero, #2338): el requisito se aplica a
/// todos los Trabajadores asignados.
/// </param>
/// <param name="TiposContratacion">Máscara de bits de los tipos de contratación. Tampoco se evalúa todavía.</param>
public sealed record RequisitoTecDto(
    Guid ContratoAccesoId,
    string TenantTceNombre,
    Guid CentroId,
    string CentroNombre,
    string ClienteEmpresarialNombre,
    Guid RequisitoId,
    Guid TipoDocumentoId,
    string TipoDocumentoNombre,
    string Entidad,
    string? Criterio,
    string ModoVigencia,
    int? VigenciaMeses,
    int? ToleranciaDias,
    bool BloqueaAcceso,
    string AmbitoDocumento,
    string CapaPlantilla,
    string SePideA,
    int TiposContratacion);
