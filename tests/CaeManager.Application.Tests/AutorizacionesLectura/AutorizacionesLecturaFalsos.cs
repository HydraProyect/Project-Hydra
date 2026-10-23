using CaeManager.Application.AutorizacionesLectura;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.AutorizacionesLectura;

namespace CaeManager.Application.Tests.AutorizacionesLectura;

/// <summary>
/// Repositorio en memoria. Como el real, solo ve el Tenant actual: una autorización de otro Tenant
/// sencillamente no está en la lista (en la base la oculta RLS).
/// </summary>
public class AutorizacionLecturaRepositorioFalso : IAutorizacionLecturaRepository
{
    public List<AutorizacionLecturaDocumental> Autorizaciones { get; } = [];
    public List<AutorizacionLecturaTrabajador> Trabajadores { get; } = [];

    public Task<AutorizacionLecturaDocumental?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Autorizaciones.FirstOrDefault(a => a.Id == id));

    public Task<IReadOnlyList<AutorizacionLecturaTrabajador>> ObtenerTrabajadoresAsync(
        Guid autorizacionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AutorizacionLecturaTrabajador>>(
            Trabajadores.Where(t => t.AutorizacionId == autorizacionId).ToList());

    public Task<bool> ExisteNoRevocadaAsync(Guid tenantDestinatarioId, Guid contratoAccesoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Autorizaciones.Any(a =>
            a.TenantDestinatarioId == tenantDestinatarioId && a.ContratoAccesoId == contratoAccesoId && !a.EstaRevocada));

    public void Agregar(AutorizacionLecturaDocumental autorizacion) => Autorizaciones.Add(autorizacion);

    public void AgregarTrabajador(AutorizacionLecturaTrabajador trabajador) => Trabajadores.Add(trabajador);

    public void RetirarTrabajador(AutorizacionLecturaTrabajador trabajador) => Trabajadores.Remove(trabajador);
}

public class AutorizacionesLecturaQueryContextFalso(AutorizacionLecturaRepositorioFalso repositorio) : IAutorizacionesLecturaQueryContext
{
    public IQueryable<AutorizacionLecturaDocumental> AutorizacionesLecturaDocumental =>
        new TestAsyncQueryable<AutorizacionLecturaDocumental>(repositorio.Autorizaciones.AsQueryable());

    public IQueryable<AutorizacionLecturaTrabajador> AutorizacionesLecturaTrabajadores =>
        new TestAsyncQueryable<AutorizacionLecturaTrabajador>(repositorio.Trabajadores.AsQueryable());
}

public sealed class TenantActualFijo(Guid? tenantId) : ITenantActual
{
    public Guid? TenantId => tenantId;
}
