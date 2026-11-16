namespace CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;

/// <summary>
/// Lo que ocurre dentro de un contrato de acceso y que el listado y el detalle enseñan al lado de sus condiciones:
/// Trabajadores asignados, solicitudes y requerimientos pendientes y subcontratas.
/// </summary>
public sealed record ActividadDeContrato(
    int TrabajadoresAsignados, int SolicitudesPendientes, int RequerimientosPendientes, int Subcontratas);

/// <summary>
/// Lectura de la actividad de los contratos de acceso. Hoy no tiene backend: las solicitudes y los Trabajadores con cupo
/// son B-17, las subcontratas B-19 y los requerimientos B-11. Un contrato ausente del resultado significa «sin dato»,
/// no cero: la pantalla pinta «—» y no inventa cifras.
/// </summary>
public interface IActividadDeContratoAcceso
{
    Task<IReadOnlyDictionary<Guid, ActividadDeContrato>> ObtenerAsync(
        IReadOnlyCollection<Guid> contratoIds, CancellationToken cancellationToken = default);
}

/// <summary>Implementación vacía mientras no existan los backends que la alimentan: nunca devuelve un contrato.</summary>
public sealed class ActividadDeContratoAccesoNoDisponible : IActividadDeContratoAcceso
{
    // shortcut: sin backend, cablear cuando entre B-17 (solicitudes y Trabajadores con cupo), B-19 (subcontratas) y B-11
    // (requerimientos y lectura inversa para el Tenant TEC).
    public Task<IReadOnlyDictionary<Guid, ActividadDeContrato>> ObtenerAsync(
        IReadOnlyCollection<Guid> contratoIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ActividadDeContrato>>(new Dictionary<Guid, ActividadDeContrato>());
}
