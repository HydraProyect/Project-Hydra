using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Operaciones;

namespace CaeManager.Application.Tests.Operaciones.IncorporacionCartera;

/// <summary>
/// <see cref="ICurrentUserService"/> cuyo rol depende del ámbito, como el real:
/// dentro de <see cref="AmbitoTenantExplicito"/> sobre el tenant de origen
/// devuelve el claim de rol de la sesión; en cualquier otro sitio, el de la
/// cartera del workspace activo. Dentro de un Workspace operativo derivado ese
/// claim ya no es el de origen (RolEfectivoDelWorkspaceMiddleware lo sustituye
/// por el de la cartera), así que quien lo construye pasa aquí el claim tal
/// como quedó, y el rol real de origen al <see cref="DirectorioRolesEnOrigen"/>.
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

/// <summary>
/// Directorio con el rol de cada cuenta en su tenant, como Identity: la fuente
/// del rol de origen de ContextoOperadorCae, que la selección de workspace no
/// cambia. Resuelve nombres para cualquiera (no es de lo que van estos tests).
/// </summary>
public class DirectorioRolesEnOrigen : IDirectorioUsuariosService
{
    private readonly Dictionary<(Guid Usuario, Guid Tenant), string> _roles = [];
    private readonly HashSet<Guid> _desactivadas = [];

    public void Asignar(Guid usuarioId, Guid tenantId, string? rol)
    {
        if (rol is null)
            _roles.Remove((usuarioId, tenantId));
        else
            _roles[(usuarioId, tenantId)] = rol;
    }

    public void Desactivar(Guid usuarioId) => _desactivadas.Add(usuarioId);

    public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(null);

    public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
        IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            usuarioIds.ToDictionary(id => id, id => $"Usuario {id:N}"[..12]));

    public Task<bool> EsCuentaActivaConRolAsync(
        Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
        Task.FromResult(_roles.TryGetValue((usuarioId, tenantId), out var suyo)
                        && suyo == rol
                        && !_desactivadas.Contains(usuarioId));
}
