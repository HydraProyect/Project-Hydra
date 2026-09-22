using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Operaciones;

namespace CaeManager.Application.Tests.Operaciones.IncorporacionCartera;

/// <summary>
/// <see cref="ICurrentUserService"/> cuyo rol depende del ámbito, como el real:
/// dentro de <see cref="AmbitoTenantExplicito"/> sobre el tenant de origen
/// devuelve el rol de la sesión; en cualquier otro sitio, el de la cartera del
/// workspace activo. Es lo que permite probar que los handlers resuelven el
/// rol en su propia organización y no en el Tenant que el usuario tenga
/// abierto.
/// </summary>
public class CurrentUserServicePorAmbito(Guid? usuarioId, Guid? tenantOrigenId, string? rolEnOrigen, string? rolFueraDelOrigen = null)
    : ICurrentUserService
{
    public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);

    public Task<string?> ObtenerRolActualAsync() =>
        Task.FromResult(AmbitoTenantExplicito.TenantIdActual is { } ambito && ambito == tenantOrigenId
            ? rolEnOrigen
            : rolFueraDelOrigen);

    public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId);

    public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
}

/// <summary>Anota el Tenant activo en cada guardado, para comprobar dónde se sella lo escrito.</summary>
public class UnitOfWorkConAmbito : IUnitOfWork
{
    public List<Guid?> TenantsAlGuardar { get; } = [];
    public Exception? ExcepcionAlGuardar { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ExcepcionAlGuardar is { } excepcion) throw excepcion;
        TenantsAlGuardar.Add(AmbitoTenantExplicito.TenantIdActual);
        return Task.FromResult(1);
    }
}

public class SolicitudIncorporacionCarteraRepositorioFalso : ISolicitudIncorporacionCarteraRepository
{
    public List<SolicitudIncorporacionCartera> Solicitudes { get; } = [];

    public Task<SolicitudIncorporacionCartera?> ObtenerPorIdAsync(
        Guid id, Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Solicitudes.FirstOrDefault(s => s.Id == id && s.OperadorTenantId == operadorTenantId));

    public Task<IReadOnlyList<SolicitudIncorporacionCartera>> ListarPendientesAsync(
        Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SolicitudIncorporacionCartera>>(Solicitudes
            .Where(s => s.OperadorTenantId == operadorTenantId && s.Estado == EstadoSolicitudIncorporacionCartera.Pendiente)
            .ToList());

    public Task<IReadOnlyList<SolicitudIncorporacionCartera>> ListarAceptadasAsync(
        Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SolicitudIncorporacionCartera>>(Solicitudes
            .Where(s => s.OperadorTenantId == operadorTenantId && s.Estado == EstadoSolicitudIncorporacionCartera.Aceptada)
            .ToList());

    public Task<IReadOnlyList<SolicitudIncorporacionCartera>> ListarDelSolicitanteAsync(
        Guid operadorTenantId, Guid solicitanteUsuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SolicitudIncorporacionCartera>>(Solicitudes
            .Where(s => s.OperadorTenantId == operadorTenantId && s.SolicitanteUsuarioId == solicitanteUsuarioId)
            .ToList());

    public Task<bool> ExistePendienteAsync(
        Guid asignacionOperacionId, Guid solicitanteUsuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Solicitudes.Any(s => s.AsignacionOperacionId == asignacionOperacionId
                                             && s.SolicitanteUsuarioId == solicitanteUsuarioId
                                             && s.Estado == EstadoSolicitudIncorporacionCartera.Pendiente));

    public void Agregar(SolicitudIncorporacionCartera solicitud) => Solicitudes.Add(solicitud);
}

/// <summary>
/// Fake del catálogo. Por defecto: los candidatos son los que se registren, y
/// la incorporación crea una cartera universal válida sobre la operación
/// registrada. Anota con qué Tenant activo se llamó a cada escritura.
/// </summary>
public class CatalogoIncorporacionCarteraFalso : ICatalogoIncorporacionCartera
{
    public List<(Guid OperadorTenantId, Guid UsuarioId, TenantCandidatoIncorporacion Candidato)> Candidatos { get; } = [];
    public Dictionary<Guid, AsignacionOperacion> Operaciones { get; } = [];
    public HashSet<Guid> CarterasVigentes { get; } = [];

    public MotivoAnulacionSolicitudCartera? AnularAlIncorporar { get; set; }
    public bool PierdeLaCarrera { get; set; }

    public List<Guid?> TenantsAlIncorporar { get; } = [];
    public List<Guid?> TenantsAlRetirar { get; } = [];
    public List<Guid?> TenantsAlGuardar { get; } = [];
    public List<(Guid OperadorTenantId, Guid UsuarioId)> ConsultasDeCandidatos { get; } = [];
    public int VecesDescartado { get; private set; }

    public void RegistrarCandidato(Guid operadorTenantId, Guid usuarioId, AsignacionOperacion operacion, string nombre)
    {
        Operaciones[operacion.Id] = operacion;
        Candidatos.Add((operadorTenantId, usuarioId,
            new TenantCandidatoIncorporacion(operacion.PropietarioTenantId, nombre, operacion.Id)));
    }

    public Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerCandidatosAsync(
        Guid operadorTenantId, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        ConsultasDeCandidatos.Add((operadorTenantId, usuarioId));
        return Task.FromResult<IReadOnlyList<TenantCandidatoIncorporacion>>(Candidatos
            .Where(c => c.OperadorTenantId == operadorTenantId && c.UsuarioId == usuarioId)
            .Select(c => c.Candidato)
            .ToList());
    }

    public Task<AsignacionOperacion?> ObtenerOperacionVigenteAsync(
        Guid asignacionOperacionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Operaciones.GetValueOrDefault(asignacionOperacionId));

    public Task<ResultadoIncorporacionCartera> IncorporarAsync(
        SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken = default)
    {
        TenantsAlIncorporar.Add(AmbitoTenantExplicito.TenantIdActual);

        if (AnularAlIncorporar is { } motivo)
            return Task.FromResult(ResultadoIncorporacionCartera.Anulada(motivo));

        var operacion = Operaciones[solicitud.AsignacionOperacionId];
        var cartera = AsignacionCartera.Externa(
            operacion, solicitud.SolicitanteUsuarioId, "GestorCae", AmbitoAsignacion.Universal,
            DateTime.UtcNow, null, DateTime.UtcNow);
        CarterasVigentes.Add(cartera.Id);
        return Task.FromResult(new ResultadoIncorporacionCartera(cartera, Guid.NewGuid(), null));
    }

    public Task RetirarAsync(SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken = default)
    {
        TenantsAlRetirar.Add(AmbitoTenantExplicito.TenantIdActual);
        if (solicitud.AsignacionCarteraId is { } id) CarterasVigentes.Remove(id);
        return Task.CompletedTask;
    }

    public Task<bool> GuardarDetectandoCarreraAsync(CancellationToken cancellationToken = default)
    {
        if (PierdeLaCarrera) return Task.FromResult(false);
        TenantsAlGuardar.Add(AmbitoTenantExplicito.TenantIdActual);
        return Task.FromResult(true);
    }

    public void DescartarPendientes() => VecesDescartado++;

    public Task<IReadOnlySet<Guid>> FiltrarCarterasVigentesAsync(
        IReadOnlyCollection<Guid> asignacionCarteraIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<Guid>>(asignacionCarteraIds.Where(CarterasVigentes.Contains).ToHashSet());
}
