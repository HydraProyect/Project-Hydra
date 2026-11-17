using CaeManager.Domain.Inbound.EvidenciasInbound;

namespace CaeManager.Application.Inbound.AutorizacionesLectura;

/// <summary>
/// Lectura inversa X-2 de la demo Inbound (P0b): el veredicto que los Tenants TCE dieron sobre los Documentos del Tenant TEC
/// del contexto firmado, por la función <c>app_inbound_tec_veredictos()</c> (migración <c>AnadeLecturaVeredictosTec</c>).
/// Solo evidencias cuyo origen es el Tenant TEC y solo por contratos de acceso que pasan el gate D10 (Activo, vinculado,
/// dentro de sus fechas y con la línea Inbound vigente del Tenant TCE). No recibe ningún Tenant: lo pone el contexto firmado.
/// Todavía sin consumidor: la Query del Tenant TEC (P1) lo cableará en su PR.
/// </summary>
public interface ILectorVeredictosTec
{
    Task<IReadOnlyList<VeredictoTecDto>> VeredictosAsync(CancellationToken cancellationToken = default);
}

/// <summary>Los dos veredictos que una evidencia Inbound puede tener.</summary>
public enum EstadoVeredictoTec
{
    Aceptada = 0,
    Rechazada = 1
}

/// <summary>
/// Un veredicto sobre un Documento del Tenant TEC (P-4: sin ninguna identidad de Gestor CAE ni de otra persona).
/// <see cref="CausaRechazo"/> y <see cref="MotivoRechazo"/> solo llegan con <see cref="EstadoVeredictoTec.Rechazada"/>, y
/// nunca en una evidencia de salud (art. 9 RGPD): de esa el Tenant TEC solo sabe el estado y la fecha, lo necesario para
/// volver a subir el Documento. <see cref="FechaVeredictoUtc"/> es la del rechazo o la de la última validación.
/// </summary>
public sealed record VeredictoTecDto(
    Guid ContratoAccesoId,
    Guid DocumentoOrigenId,
    EstadoVeredictoTec Estado,
    CausaRechazoEvidenciaInbound? CausaRechazo,
    string? MotivoRechazo,
    DateTime FechaVeredictoUtc);
