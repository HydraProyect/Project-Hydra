using CaeManager.Application.Common;
using CaeManager.Domain.Cumplimiento.Subencargados;

namespace CaeManager.Application.Tests.Cumplimiento;

/// <summary>
/// Doble de <see cref="ISubencargadoRepository"/> que también hace de unidad de trabajo: lo agregado queda pendiente
/// hasta <see cref="SaveChangesAsync"/>, para poder fijar qué estaba guardado cuando salió cada correo. Como el
/// repositorio real, lo que es de un Tenant se lee y se sella en el Tenant actual: el de
/// <see cref="AmbitoTenantExplicito"/> si hay uno abierto, y si no <see cref="TenantDeLaSesion"/>.
/// </summary>
public sealed class SubencargadoRepositoryFalso : ISubencargadoRepository, IUnitOfWork
{
    private readonly List<(Guid TenantId, AvisoCambioSubencargado Aviso)> _avisosPendientes = [];
    private readonly List<(Guid TenantId, ObjecionSubencargado Objecion)> _objecionesPendientes = [];
    private readonly List<Subencargado> _subencargadosPendientes = [];

    public Guid? TenantDeLaSesion { get; set; }
    public Dictionary<Guid, Subencargado> Subencargados { get; } = [];
    public List<Guid> TenantsActivos { get; } = [];
    public Dictionary<Guid, List<DestinatarioAvisoSubencargado>> Administradores { get; } = [];
    public List<(Guid TenantId, AvisoCambioSubencargado Aviso)> Avisos { get; } = [];
    public List<(Guid TenantId, ObjecionSubencargado Objecion)> Objeciones { get; } = [];
    public bool HayTransaccionAbierta { get; set; }
    public bool CambioAjenoPendiente { get; set; }
    public int VecesGuardado { get; private set; }

    public bool HayCambiosPendientes =>
        CambioAjenoPendiente || _avisosPendientes.Count > 0 || _objecionesPendientes.Count > 0 || _subencargadosPendientes.Count > 0;

    private Guid TenantActual =>
        AmbitoTenantExplicito.TenantIdActual ?? TenantDeLaSesion
        ?? throw new InvalidOperationException("Sin Tenant actual: el repositorio real no vería ninguna fila del Tenant.");

    public void AvisoGuardado(Guid tenantId, Guid subencargadoId, FaseAvisoCambioSubencargado fase, DateTime registradoEnUtc) =>
        Avisos.Add((tenantId, new AvisoCambioSubencargado(subencargadoId, Guid.NewGuid(), Guid.NewGuid(), fase, registradoEnUtc, Guid.NewGuid())));

    public void Agregar(Subencargado subencargado) => _subencargadosPendientes.Add(subencargado);

    public Task<Subencargado?> ObtenerAsync(Guid subencargadoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Subencargados.GetValueOrDefault(subencargadoId));

    public Task<IReadOnlyList<Guid>> TenantsPropietariosActivosAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. TenantsActivos]);

    public Task<bool> TieneAvisoProbadoAsync(Guid subencargadoId, DateTime? enviadoComoTardeEnUtc, CancellationToken cancellationToken = default)
    {
        var tenant = TenantActual;
        return Task.FromResult(Avisos.Any(a =>
            a.TenantId == tenant && a.Aviso.SubencargadoId == subencargadoId && a.Aviso.Fase == FaseAvisoCambioSubencargado.Enviado
            && (enviadoComoTardeEnUtc is null || a.Aviso.RegistradoEnUtc <= enviadoComoTardeEnUtc)));
    }

    public Task<IReadOnlyList<DestinatarioAvisoSubencargado>> AdministradoresDelTenantActualAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId != TenantActual)
            throw new InvalidOperationException("Los Administradores se leen en el ámbito de su propio Tenant.");
        return Task.FromResult<IReadOnlyList<DestinatarioAvisoSubencargado>>(
            Administradores.TryGetValue(tenantId, out var lista) ? [.. lista] : []);
    }

    public void AgregarAviso(AvisoCambioSubencargado aviso) => _avisosPendientes.Add((TenantActual, aviso));

    public Task<bool> ExisteObjecionAsync(Guid subencargadoId, CancellationToken cancellationToken = default)
    {
        var tenant = TenantActual;
        return Task.FromResult(Objeciones.Any(o => o.TenantId == tenant && o.Objecion.SubencargadoId == subencargadoId));
    }

    public void AgregarObjecion(ObjecionSubencargado objecion) => _objecionesPendientes.Add((TenantActual, objecion));

    public Task<bool> ExcluyeConmutadorAsync(Guid tenantId, ConmutadorExclusionSubencargado conmutador, CancellationToken cancellationToken = default) =>
        Task.FromResult(Objeciones.Any(o =>
            o.TenantId == tenantId
            && o.Objecion.Efecto == EfectoObjecionSubencargado.ExcluidoParaElTenant
            && o.Objecion.Conmutador == conmutador
            && Subencargados.TryGetValue(o.Objecion.SubencargadoId, out var s) && s.Estado == EstadoSubencargado.Activo));

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Avisos.AddRange(_avisosPendientes);
        Objeciones.AddRange(_objecionesPendientes);
        foreach (var subencargado in _subencargadosPendientes)
            Subencargados[subencargado.Id] = subencargado;
        _avisosPendientes.Clear();
        _objecionesPendientes.Clear();
        _subencargadosPendientes.Clear();
        VecesGuardado++;
        return Task.FromResult(1);
    }
}
