using CaeManager.Application.Clientes;
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

    public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(rolEnOrigen);

    public Task<string?> ObtenerRolEfectivoAsync() =>
        Task.FromResult(AmbitoTenantExplicito.TenantIdActual is { } ambito && ambito == tenantOrigenId
            ? rolEnOrigen
            : rolFueraDelOrigen);

    public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId);

    public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
}

/// <summary>Las cuentas del Operador CAE tal y como las da Identity: rol, a quién reportan y si están activas.</summary>
public class DirectorioDestinosPorUsuario : IDirectorioDestinosCartera
{
    public Dictionary<Guid, DestinoCartera> Cuentas { get; } = [];

    public Task<DestinoCartera?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Cuentas.GetValueOrDefault(usuarioId));
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

public class PropuestaApoyoCarteraRepositorioFalso : IPropuestaApoyoCarteraRepository
{
    public List<PropuestaApoyoCartera> Propuestas { get; } = [];

    public Task<PropuestaApoyoCartera?> ObtenerPorIdAsync(
        Guid id, Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Propuestas.FirstOrDefault(p => p.Id == id && p.OperadorTenantId == operadorTenantId));

    public Task<IReadOnlyList<PropuestaApoyoCartera>> ListarPendientesDelDestinatarioAsync(
        Guid operadorTenantId, Guid destinatarioUsuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PropuestaApoyoCartera>>(Propuestas
            .Where(p => p.OperadorTenantId == operadorTenantId && p.DestinatarioUsuarioId == destinatarioUsuarioId
                        && p.Estado == EstadoPropuestaApoyoCartera.Pendiente)
            .ToList());

    public Task<IReadOnlyList<PropuestaApoyoCartera>> ListarPendientesDelProponenteAsync(
        Guid operadorTenantId, Guid proponenteUsuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PropuestaApoyoCartera>>(Propuestas
            .Where(p => p.OperadorTenantId == operadorTenantId && p.ProponenteUsuarioId == proponenteUsuarioId
                        && p.Estado == EstadoPropuestaApoyoCartera.Pendiente)
            .ToList());

    public Task<bool> ExistePendienteAsync(
        Guid asignacionOperacionId, Guid destinatarioUsuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Propuestas.Any(p => p.AsignacionOperacionId == asignacionOperacionId
                                            && p.DestinatarioUsuarioId == destinatarioUsuarioId
                                            && p.Estado == EstadoPropuestaApoyoCartera.Pendiente));

    public void Agregar(PropuestaApoyoCartera propuesta) => Propuestas.Add(propuesta);
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

    /// <summary>La escritura real no encuentra cómo emitir ni qué marcar (otra cartera suya, delegación caída): <c>SinRelevo</c>.</summary>
    public bool RelevoImposible { get; set; }

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

    /// <summary>Asignables por Operador CAE, sin descontar carteras: los del alta de un Gestor CAE.</summary>
    public List<(Guid OperadorTenantId, TenantCandidatoIncorporacion Asignable)> Asignables { get; } = [];

    /// <summary>Cada incorporación sin solicitud, con el Tenant activo con que se pidió.</summary>
    public List<(Guid PropietarioTenantId, Guid OperadorTenantId, Guid AsignacionOperacionId, Guid UsuarioId, Guid? TenantActivo)> IncorporacionesDirectas { get; } = [];

    public void RegistrarAsignable(Guid operadorTenantId, AsignacionOperacion operacion, string nombre)
    {
        Operaciones[operacion.Id] = operacion;
        Asignables.Add((operadorTenantId,
            new TenantCandidatoIncorporacion(operacion.PropietarioTenantId, nombre, operacion.Id)));
    }

    public Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerAsignablesAsync(
        Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TenantCandidatoIncorporacion>>(Asignables
            .Where(a => a.OperadorTenantId == operadorTenantId)
            .Select(a => a.Asignable)
            .ToList());

    public Task<ResultadoIncorporacionCartera> IncorporarAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid asignacionOperacionId, Guid usuarioId,
        CancellationToken cancellationToken = default)
    {
        IncorporacionesDirectas.Add((propietarioTenantId, operadorTenantId, asignacionOperacionId, usuarioId,
            AmbitoTenantExplicito.TenantIdActual));

        if (AnularAlIncorporar is { } motivo)
            return Task.FromResult(ResultadoIncorporacionCartera.Anulada(motivo));

        var cartera = AsignacionCartera.Externa(
            Operaciones[asignacionOperacionId], usuarioId, "GestorCae", AmbitoAsignacion.Universal,
            DateTime.UtcNow, null, DateTime.UtcNow);
        CarterasVigentes.Add(cartera.Id);
        return Task.FromResult(new ResultadoIncorporacionCartera(cartera, Guid.NewGuid(), null));
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

    /// <summary>Motivo con el que la vía de apoyo anula, en vez de emitir, si se fija.</summary>
    public MotivoAnulacionPropuestaApoyo? AnularAlIncorporarApoyo { get; set; }

    /// <summary>Cada emisión de apoyo pedida, con el Tenant activo con que se pidió.</summary>
    public List<(PropuestaApoyoCartera Propuesta, Guid? TenantActivo)> ApoyosPedidos { get; } = [];

    /// <summary>
    /// Como el real: solo emite si quien propuso lleva hoy la marca de principal en
    /// <see cref="CarterasVivas"/>, y lo que emite nunca la lleva.
    /// </summary>
    public Task<ResultadoApoyoCartera> IncorporarApoyoAsync(
        PropuestaApoyoCartera propuesta, CancellationToken cancellationToken = default)
    {
        ApoyosPedidos.Add((propuesta, AmbitoTenantExplicito.TenantIdActual));

        if (AnularAlIncorporarApoyo is { } motivo)
            return Task.FromResult(ResultadoApoyoCartera.Anulada(motivo));

        var principal = CarterasVivas.FirstOrDefault(c => c.OperadorTenantId == propuesta.OperadorTenantId
                                                          && c.Cartera.AsignacionOperacionId == propuesta.AsignacionOperacionId
                                                          && c.Cartera.EsPrincipal).Cartera;
        if (principal?.UsuarioId != propuesta.ProponenteUsuarioId)
            return Task.FromResult(ResultadoApoyoCartera.Anulada(MotivoAnulacionPropuestaApoyo.ProponenteYaNoEsPrincipal));

        var cartera = AsignacionCartera.Externa(
            Operaciones[propuesta.AsignacionOperacionId], propuesta.DestinatarioUsuarioId, "GestorCae",
            AmbitoAsignacion.Universal, DateTime.UtcNow, propuesta.VigenciaHastaPropuesta, DateTime.UtcNow);
        CarterasVigentes.Add(cartera.Id);
        CarterasVivas.Add((propuesta.OperadorTenantId, new CarteraVivaDeOperacion(
            propuesta.AsignacionOperacionId, propuesta.PropietarioTenantId, "Empresa",
            propuesta.DestinatarioUsuarioId, "GestorCae", false, propuesta.VigenciaHastaPropuesta)));
        ApoyosEmitidos.Add((propuesta, cartera.Id));
        return Task.FromResult(new ResultadoApoyoCartera(cartera, Guid.NewGuid(), null));
    }

    /// <summary>Cada propuesta que emitió una cartera de apoyo, con la cartera emitida.</summary>
    public List<(PropuestaApoyoCartera Propuesta, Guid CarteraId)> ApoyosEmitidos { get; } = [];

    /// <summary>Cada fin de apoyo pedido, con el Tenant activo con que se pidió.</summary>
    public List<(Guid PropuestaId, Guid ActorUsuarioId, bool ExigirProponentePrincipal, Guid? TenantActivo)> ApoyosRetirados { get; } = [];

    private int IndiceDeCartera(Guid operadorTenantId, Guid asignacionOperacionId, Guid usuarioId) =>
        CarterasVivas.FindIndex(c => c.OperadorTenantId == operadorTenantId
                                     && c.Cartera.AsignacionOperacionId == asignacionOperacionId
                                     && c.Cartera.UsuarioId == usuarioId);

    /// <summary>Como el real: propuestas aceptadas cuya cartera sigue viva, sin marca y de Gestor CAE.</summary>
    public Task<IReadOnlyList<ApoyoVivoDeCartera>> ObtenerApoyosVivosAsync(
        Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApoyoVivoDeCartera>>(ApoyosEmitidos
            .Select(a => a.Propuesta)
            .Where(p => p.OperadorTenantId == operadorTenantId && p.Estado == EstadoPropuestaApoyoCartera.Aceptada)
            .Select(p => (Propuesta: p, Indice: IndiceDeCartera(operadorTenantId, p.AsignacionOperacionId, p.DestinatarioUsuarioId)))
            .Where(x => x.Indice >= 0 && CarterasVivas[x.Indice].Cartera is { EsPrincipal: false, Rol: "GestorCae" })
            .Select(x => new ApoyoVivoDeCartera(
                x.Propuesta.Id, x.Propuesta.AsignacionOperacionId, x.Propuesta.PropietarioTenantId,
                CarterasVivas[x.Indice].Cartera.NombreTenant, x.Propuesta.DestinatarioUsuarioId,
                x.Propuesta.ProponenteUsuarioId, CarterasVivas[x.Indice].Cartera.VigenciaHasta))
            .ToList());

    /// <summary>
    /// Como el real: no toca una cartera con marca, exige (si se pide) que quien propuso siga
    /// siendo el principal, y al cerrar da la propuesta por terminada. Nunca toca otra cartera.
    /// </summary>
    public Task<ResultadoRetiradaApoyo> RetirarCarteraDeApoyoAsync(
        PropuestaApoyoCartera propuesta, Guid actorUsuarioId, bool exigirProponentePrincipal,
        CancellationToken cancellationToken = default)
    {
        ApoyosRetirados.Add((propuesta.Id, actorUsuarioId, exigirProponentePrincipal, AmbitoTenantExplicito.TenantIdActual));

        var i = IndiceDeCartera(propuesta.OperadorTenantId, propuesta.AsignacionOperacionId, propuesta.DestinatarioUsuarioId);
        if (i < 0)
        {
            propuesta.Terminar();
            return Task.FromResult(ResultadoRetiradaApoyo.YaEstabaCerrada);
        }

        if (CarterasVivas[i].Cartera.EsPrincipal)
            return Task.FromResult(ResultadoRetiradaApoyo.YaNoEsDeApoyo);

        if (exigirProponentePrincipal
            && !CarterasVivas.Any(c => c.OperadorTenantId == propuesta.OperadorTenantId
                                       && c.Cartera.AsignacionOperacionId == propuesta.AsignacionOperacionId
                                       && c.Cartera.EsPrincipal
                                       && c.Cartera.UsuarioId == propuesta.ProponenteUsuarioId))
            return Task.FromResult(ResultadoRetiradaApoyo.ProponenteYaNoEsPrincipal);

        CarterasVivas.RemoveAt(i);
        CarterasVigentes.Remove(ApoyosEmitidos.FirstOrDefault(a => a.Propuesta.Id == propuesta.Id).CarteraId);
        propuesta.Terminar();
        return Task.FromResult(ResultadoRetiradaApoyo.Retirada);
    }

    /// <summary>Los Tenants que cada Gestor CAE tiene enteros, por Operador CAE.</summary>
    public List<(Guid OperadorTenantId, Guid UsuarioId, TenantEnCarteraDeGestor Tenant)> CarterasUniversales { get; } = [];

    /// <summary>Cada retirada sin solicitud, con el Tenant activo con que se pidió.</summary>
    public List<(Guid PropietarioTenantId, Guid OperadorTenantId, Guid UsuarioId, Guid ActorUsuarioId, Guid? TenantActivo)> Retiradas { get; } = [];

    public Task<IReadOnlyList<TenantEnCarteraDeGestor>> ObtenerCarteraUniversalAsync(
        Guid operadorTenantId, Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TenantEnCarteraDeGestor>>(CarterasUniversales
            .Where(c => c.OperadorTenantId == operadorTenantId && c.UsuarioId == usuarioId)
            .Select(c => c.Tenant)
            .ToList());

    public Task<bool> RetirarCarteraUniversalAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, Guid actorUsuarioId,
        CancellationToken cancellationToken = default)
    {
        Retiradas.Add((propietarioTenantId, operadorTenantId, usuarioId, actorUsuarioId, AmbitoTenantExplicito.TenantIdActual));
        CarterasVivas.RemoveAll(c => c.OperadorTenantId == operadorTenantId && c.Cartera.UsuarioId == usuarioId
                                     && c.Cartera.PropietarioTenantId == propietarioTenantId && c.Cartera.Rol == "GestorCae");
        return Task.FromResult(CarterasUniversales.RemoveAll(c =>
            c.OperadorTenantId == operadorTenantId && c.UsuarioId == usuarioId
            && c.Tenant.PropietarioTenantId == propietarioTenantId) > 0);
    }

    /// <summary>Carteras vivas por operación (marca de principal), con su Operador CAE.</summary>
    public List<(Guid OperadorTenantId, CarteraVivaDeOperacion Cartera)> CarterasVivas { get; } = [];

    /// <summary>Operaciones externas vivas sobre cada Tenant propietario, vistas desde él.</summary>
    public List<(Guid PropietarioTenantId, OperacionExternaSobreTenant Operacion)> OperacionesExternas { get; } = [];

    /// <summary>Cada Tenant propietario por el que se preguntó: vacío si la Query no llegó a leer.</summary>
    public List<Guid> ConsultasDeOperacionesExternas { get; } = [];

    public Task<IReadOnlyList<OperacionExternaSobreTenant>> ObtenerOperacionesExternasSobreTenantAsync(
        Guid propietarioTenantId, CancellationToken cancellationToken = default)
    {
        ConsultasDeOperacionesExternas.Add(propietarioTenantId);
        return Task.FromResult<IReadOnlyList<OperacionExternaSobreTenant>>(OperacionesExternas
            .Where(o => o.PropietarioTenantId == propietarioTenantId)
            .Select(o => o.Operacion)
            .ToList());
    }

    /// <summary>Cada paso que toca la marca de principal, en orden, con el Tenant activo con que se pidió.</summary>
    public List<(string Paso, Guid AsignacionOperacionId, Guid UsuarioId, Guid? TenantActivo)> CambiosDeMarca { get; } = [];

    public Task<IReadOnlyList<CarteraVivaDeOperacion>> ObtenerCarterasVivasAsync(
        Guid operadorTenantId, Guid? propietarioTenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CarteraVivaDeOperacion>>(CarterasVivas
            .Where(c => c.OperadorTenantId == operadorTenantId
                        && (propietarioTenantId is null || c.Cartera.PropietarioTenantId == propietarioTenantId))
            .Select(c => c.Cartera)
            .ToList());

    public Task<IReadOnlyList<OperacionConPrincipal>> ObtenerOperacionesDondeEsPrincipalAsync(
        Guid operadorTenantId, Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OperacionConPrincipal>>(CarterasVivas
            .Where(c => c.OperadorTenantId == operadorTenantId && c.Cartera.UsuarioId == usuarioId && c.Cartera.EsPrincipal)
            .Select(c => new OperacionConPrincipal(c.Cartera.PropietarioTenantId, c.Cartera.AsignacionOperacionId))
            .ToList());

    private void Marcar(int indice, bool esPrincipal) =>
        CarterasVivas[indice] = (CarterasVivas[indice].OperadorTenantId, CarterasVivas[indice].Cartera with { EsPrincipal = esPrincipal });

    public Task<bool> ApagarPrincipalAsync(
        Guid operadorTenantId, Guid asignacionOperacionId, Guid usuarioEsperadoId, CancellationToken cancellationToken = default)
    {
        CambiosDeMarca.Add(("apagar", asignacionOperacionId, usuarioEsperadoId, AmbitoTenantExplicito.TenantIdActual));
        var i = CarterasVivas.FindIndex(c => c.OperadorTenantId == operadorTenantId
                                             && c.Cartera.AsignacionOperacionId == asignacionOperacionId && c.Cartera.EsPrincipal);
        if (i < 0 || CarterasVivas[i].Cartera.UsuarioId != usuarioEsperadoId) return Task.FromResult(false);
        Marcar(i, false);
        return Task.FromResult(true);
    }

    public Task<bool> EncenderPrincipalAsync(
        Guid operadorTenantId, Guid asignacionOperacionId, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        CambiosDeMarca.Add(("encender", asignacionOperacionId, usuarioId, AmbitoTenantExplicito.TenantIdActual));
        if (CarterasVivas.Any(c => c.Cartera.AsignacionOperacionId == asignacionOperacionId && c.Cartera.EsPrincipal))
            return Task.FromResult(false);
        var i = CarterasVivas.FindIndex(c => c.OperadorTenantId == operadorTenantId
                                             && c.Cartera.AsignacionOperacionId == asignacionOperacionId && c.Cartera.UsuarioId == usuarioId);
        if (i < 0) return Task.FromResult(false);
        Marcar(i, true);
        return Task.FromResult(true);
    }

    public Task<ResultadoRelevoPrincipal> RelevarPrincipalAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid asignacionOperacionId, Guid coordinadorUsuarioId,
        CancellationToken cancellationToken = default)
    {
        CambiosDeMarca.Add(("relevar", asignacionOperacionId, coordinadorUsuarioId, AmbitoTenantExplicito.TenantIdActual));
        if (RelevoImposible)
            return Task.FromResult(ResultadoRelevoPrincipal.SinRelevo);
        if (CarterasVivas.Any(c => c.Cartera.AsignacionOperacionId == asignacionOperacionId && c.Cartera.EsPrincipal))
            return Task.FromResult(ResultadoRelevoPrincipal.SinRelevo);
        var i = CarterasVivas.FindIndex(c => c.Cartera.AsignacionOperacionId == asignacionOperacionId && c.Cartera.UsuarioId == coordinadorUsuarioId);
        if (i >= 0)
        {
            Marcar(i, true);
            return Task.FromResult(ResultadoRelevoPrincipal.CarteraExistenteMarcada);
        }

        CarterasVivas.Add((operadorTenantId, new CarteraVivaDeOperacion(
            asignacionOperacionId, propietarioTenantId, "Empresa", coordinadorUsuarioId, "CoordinadorCae", true, null)));
        return Task.FromResult(ResultadoRelevoPrincipal.CarteraEmitida);
    }

    public Task RetirarAsync(SolicitudIncorporacionCartera solicitud, CancellationToken cancellationToken = default)
    {
        CarterasVivas.RemoveAll(c => c.Cartera.AsignacionOperacionId == solicitud.AsignacionOperacionId
                                     && c.Cartera.UsuarioId == solicitud.SolicitanteUsuarioId);
        TenantsAlRetirar.Add(AmbitoTenantExplicito.TenantIdActual);
        if (solicitud.AsignacionCarteraId is { } id) CarterasVigentes.Remove(id);
        return Task.CompletedTask;
    }

    public Task<bool> GuardarDetectandoCarreraAsync(CancellationToken cancellationToken = default)
    {
        if (PierdeLaCarrera) return Task.FromResult(false);
        TenantsAlGuardar.Add(AmbitoTenantExplicito.TenantIdActual);
        CambiosDeMarca.Add(("guardar", Guid.Empty, Guid.Empty, AmbitoTenantExplicito.TenantIdActual));
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

    /// <summary>Avatar elegido por cada usuario; quien no está aquí no eligió ninguno.</summary>
    public Dictionary<Guid, string> Avatares { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> ObtenerAvataresVisiblesAsync(
        IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            Avatares.Where(a => usuarioIds.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value));

    public Task<bool> EsCuentaActivaConRolAsync(
        Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
        Task.FromResult(_roles.TryGetValue((usuarioId, tenantId), out var suyo)
                        && suyo == rol
                        && !_desactivadas.Contains(usuarioId));

    public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(
        Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(_roles
            .Where(r => r.Key.Tenant == tenantId && r.Value == rol && !_desactivadas.Contains(r.Key.Usuario))
            .Select(r => r.Key.Usuario)
            .OrderBy(id => id)
            .ToList());
}

/// <summary>
/// Doble de <see cref="IAsignacionAutomaticaDePrincipal"/> que no asigna nada y anota lo que se le pidió:
/// para los tests de comandos de delegaciones y de cuentas que no tratan de la cartera.
/// </summary>
public class AsignacionAutomaticaInerte : IAsignacionAutomaticaDePrincipal
{
    public List<AsignacionOperacion> OperacionesAbiertas { get; } = [];
    public List<(Guid UsuarioId, Guid OperadorTenantId)> PrimerosElegibles { get; } = [];
    public bool Resultado { get; set; } = true;

    public Task<bool> AlAbrirOperacionAsync(AsignacionOperacion operacion, CancellationToken cancellationToken = default)
    {
        OperacionesAbiertas.Add(operacion);
        return Task.FromResult(Resultado);
    }

    public Task<bool> AlPrimerElegibleAsync(Guid usuarioId, Guid operadorTenantId, CancellationToken cancellationToken = default)
    {
        PrimerosElegibles.Add((usuarioId, operadorTenantId));
        return Task.FromResult(Resultado);
    }
}
