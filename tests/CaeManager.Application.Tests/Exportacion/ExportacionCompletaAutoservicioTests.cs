using CaeManager.Application.Common;
using CaeManager.Application.Exportacion;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Exportacion;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Application.Tests.Exportacion;

/// <summary>
/// Solicitud y descarga de la exportación completa en autoservicio (#2008, D-8): solo el Administrador del Tenant
/// propietario, actuando él mismo y con TOTP reciente; una cada 24 horas por Tenant; la contraseña sale una vez; la
/// descarga solo antes de caducar. El candado y el índice único los prueba la integración en CI.
/// </summary>
public class ExportacionCompletaAutoservicioTests
{
    private static readonly DateTime Ahora = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

    private readonly Tenant _tenant = new("Refrielectric");
    private readonly Guid _usuarioId = Guid.NewGuid();
    private readonly RepositorioEnMemoria _repositorio = new();
    private readonly UnitOfWorkFalso _unidad = new();
    private readonly RelojFijo _reloj = new(Ahora);

    private sealed record Escenario(
        string? Rol = "Administrador",
        bool SuperoTotp = true,
        bool OrigenAjeno = false,
        bool AdministradorEnBase = true,
        SesionPrivilegiadaActiva? Sesion = null,
        Guid? UsuarioSimulado = null,
        TipoViaAcceso Via = TipoViaAcceso.Normal,
        Guid? EncargoId = null,
        Guid? ActorReal = null,
        bool TenantDePlataforma = false);

    private SolicitarExportacionCompletaCommandHandler Solicitud(Escenario e) => new(
        Usuario(e), new TenantFijo(_tenant.Id), Actor(e), new SesionFija(e.Sesion), new AdministradorFijo(e.AdministradorEnBase),
        Tenants(e), new CandadoDirecto(), _repositorio, new ProtectorFalso(), _unidad, _reloj);

    private DescargarExportacionCompletaCommandHandler Descarga(Escenario e, AlmacenEnMemoria almacen) => new(
        Usuario(e), new TenantFijo(_tenant.Id), Actor(e), new SesionFija(e.Sesion), new AdministradorFijo(e.AdministradorEnBase),
        Tenants(e), _repositorio, almacen, _unidad, _reloj);

    private CurrentUserServiceFalso Usuario(Escenario e) =>
        new(_usuarioId, e.Rol, e.OrigenAjeno ? Guid.NewGuid() : _tenant.Id, superoTotp: e.SuperoTotp);

    private ActorFijo Actor(Escenario e) =>
        new(new ActorAuditoria(e.ActorReal ?? _usuarioId, e.UsuarioSimulado, e.Via, e.Via == TipoViaAcceso.Normal ? null : Guid.NewGuid(), e.EncargoId));

    private TenantsQueryContextFalso Tenants(Escenario e)
    {
        if (e.TenantDePlataforma)
            _tenant.MarcarComoPlataforma();
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaTenants.Add(_tenant);
        return tenants;
    }

    // ---- Solicitud ----

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_con_TOTP_reciente_la_pide_y_recibe_la_contrasena_una_vez()
    {
        var resultado = await Solicitud(new Escenario()).Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Contrasena.Should().NotBeNullOrWhiteSpace();
        var solicitud = _repositorio.Solicitudes.Should().ContainSingle().Subject;
        solicitud.Id.Should().Be(resultado.Valor.SolicitudId);
        solicitud.SolicitadaPorUsuarioId.Should().Be(_usuarioId);
        solicitud.Estado.Should().Be(EstadoSolicitudExportacionCompleta.Pendiente);
        // En la fila solo protegida, y la protección se puede deshacer solo con el protector propio.
        solicitud.ContrasenaProtegida.Should().NotBe(resultado.Valor.Contrasena);
        new ProtectorFalso().Desproteger(solicitud.ContrasenaProtegida!).Should().Be(resultado.Valor.Contrasena);
        _unidad.VecesGuardado.Should().Be(1);

        var auditoria = _repositorio.Auditoria.Should().ContainSingle().Subject;
        auditoria.Accion.Should().Be(RegistroAuditoria.AccionExportacion);
        auditoria.EntidadId.Should().Be(solicitud.Id);
        auditoria.DatosDespues.Should().Contain(AuditoriaExportacionCompleta.FaseSolicitada)
            .And.NotContain(resultado.Valor.Contrasena);
        resultado.Valor.ToString().Should().NotContain(resultado.Valor.Contrasena);
    }

    [Fact]
    public async Task Sin_TOTP_reciente_se_rechaza()
    {
        var resultado = await Solicitud(new Escenario(SuperoTotp: false)).Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error.Should().Be(AutoridadExportacionCompleta.TotpReciente);
        NadaRegistrado();
    }

    [Fact]
    public async Task Un_usuario_en_contexto_cruzado_se_rechaza_aunque_su_rol_efectivo_sea_Administrador()
    {
        // Un Gestor CAE del Operador CAE externo cuyo perfil de origen es Administrador: su Tenant de origen no es el
        // Tenant beneficiario en curso.
        var resultado = await Solicitud(new Escenario(OrigenAjeno: true)).Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.SoloAdministradorDelTenant);
        NadaRegistrado();
    }

    [Fact]
    public async Task Por_Asignacion_de_Operacion_del_Operador_CAE_externo_se_rechaza()
    {
        var resultado = await Solicitud(new Escenario(OrigenAjeno: true, Via: TipoViaAcceso.OperacionDelegada))
            .Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.EsExitoso.Should().BeFalse();
        NadaRegistrado();
    }

    [Fact]
    public async Task Por_Asignacion_de_Operacion_se_rechaza_aunque_el_origen_sea_el_propio_Tenant()
    {
        // Aísla la regla de la vía: sin contexto cruzado, solo Via != Normal la rechaza.
        var resultado = await Solicitud(new Escenario(Via: TipoViaAcceso.OperacionDelegada))
            .Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.SinSesionPrivilegiadaNiSuplantacion);
        NadaRegistrado();
    }

    [Fact]
    public async Task Un_Actor_real_distinto_del_usuario_autenticado_se_rechaza()
    {
        var resultado = await Solicitud(new Escenario(ActorReal: Guid.NewGuid()))
            .Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.SoloAdministradorDelTenant);
        NadaRegistrado();
    }

    [Fact]
    public async Task En_el_Tenant_de_plataforma_TALVEG_se_rechaza()
    {
        var resultado = await Solicitud(new Escenario(TenantDePlataforma: true))
            .Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.SoloAdministradorDelTenant);
        NadaRegistrado();
    }

    [Fact]
    public async Task Bajo_un_Encargo_de_administracion_se_rechaza()
    {
        var resultado = await Solicitud(new Escenario(EncargoId: Guid.NewGuid())).Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.SinSesionPrivilegiadaNiSuplantacion);
        NadaRegistrado();
    }

    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Un_rol_distinto_de_Administrador_se_rechaza(string? rol)
    {
        var resultado = await Solicitud(new Escenario(Rol: rol)).Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.SoloAdministradorDelTenant);
        NadaRegistrado();
    }

    [Fact]
    public async Task Administrador_en_la_claim_pero_no_en_la_base_se_rechaza()
    {
        var resultado = await Solicitud(new Escenario(AdministradorEnBase: false)).Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.SoloAdministradorDelTenant);
        NadaRegistrado();
    }

    [Fact]
    public async Task Bajo_una_Sesion_Privilegiada_se_rechaza()
    {
        var sesion = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), _tenant.Id, default, null);

        var resultado = await Solicitud(new Escenario(Sesion: sesion)).Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.SinSesionPrivilegiadaNiSuplantacion);
        NadaRegistrado();
    }

    [Fact]
    public async Task Bajo_una_impersonacion_se_rechaza()
    {
        var resultado = await Solicitud(new Escenario(UsuarioSimulado: Guid.NewGuid())).Handle(new SolicitarExportacionCompletaCommand(), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.SinSesionPrivilegiadaNiSuplantacion);
        NadaRegistrado();
    }

    [Fact]
    public async Task Una_segunda_solicitud_en_24_horas_se_rechaza()
    {
        (await Solicitud(new Escenario()).Handle(new SolicitarExportacionCompletaCommand(), default)).EsExitoso.Should().BeTrue();
        _reloj.Avanzar(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));

        var segunda = await Solicitud(new Escenario()).Handle(new SolicitarExportacionCompletaCommand(), default);

        segunda.Error.Should().Be(ErroresSolicitudExportacionCompleta.UnaCadaVeinticuatroHoras);
        _repositorio.Solicitudes.Should().ContainSingle();
        _repositorio.Auditoria.Should().ContainSingle();
    }

    [Fact]
    public async Task Pasadas_24_horas_se_puede_pedir_otra()
    {
        (await Solicitud(new Escenario()).Handle(new SolicitarExportacionCompletaCommand(), default)).EsExitoso.Should().BeTrue();
        _reloj.Avanzar(SolicitudExportacionCompleta.IntervaloMinimo);

        (await Solicitud(new Escenario()).Handle(new SolicitarExportacionCompletaCommand(), default)).EsExitoso.Should().BeTrue();

        _repositorio.Solicitudes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Una_solicitud_fallida_no_cuenta_para_el_limite()
    {
        (await Solicitud(new Escenario()).Handle(new SolicitarExportacionCompletaCommand(), default)).EsExitoso.Should().BeTrue();
        _repositorio.Solicitudes[0].MarcarFallida("GeneracionFallida", Ahora);

        (await Solicitud(new Escenario()).Handle(new SolicitarExportacionCompletaCommand(), default)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task La_contrasena_no_queda_en_la_fila_al_estar_lista_ni_al_fallar()
    {
        await Solicitud(new Escenario()).Handle(new SolicitarExportacionCompletaCommand(), default);
        var lista = _repositorio.Solicitudes[0];
        lista.MarcarEnCurso(Ahora);
        lista.MarcarLista("x/y.zip", 10, Ahora);
        lista.ContrasenaProtegida.Should().BeNull();

        var fallida = new SolicitudExportacionCompleta(_usuarioId, "protegida", Ahora);
        fallida.MarcarEnCurso(Ahora);
        fallida.MarcarFallida("GeneracionFallida", Ahora);
        fallida.ContrasenaProtegida.Should().BeNull();
    }

    // ---- Descarga ----

    [Fact]
    public async Task La_descarga_vigente_entrega_el_fichero_y_queda_auditada()
    {
        var (solicitud, almacen) = ExportacionLista();

        var resultado = await Descarga(new Escenario(), almacen).Handle(new DescargarExportacionCompletaCommand(solicitud.Id), default);

        resultado.EsExitoso.Should().BeTrue();
        await using (resultado.Valor.Contenido)
            resultado.Valor.Contenido.Length.Should().Be(3);
        resultado.Valor.TamanoBytes.Should().Be(3);
        var auditoria = _repositorio.Auditoria.Should().ContainSingle().Subject;
        auditoria.EntidadId.Should().Be(solicitud.Id);
        auditoria.DatosDespues.Should().Contain(AuditoriaExportacionCompleta.FaseDescargada);
        auditoria.ActorRealUsuarioId.Should().Be(_usuarioId);
        _unidad.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task La_descarga_caducada_se_rechaza()
    {
        var (solicitud, almacen) = ExportacionLista();
        _reloj.Avanzar(SolicitudExportacionCompleta.Vigencia);

        var resultado = await Descarga(new Escenario(), almacen).Handle(new DescargarExportacionCompletaCommand(solicitud.Id), default);

        resultado.Error.Should().Be(ErroresDescargaExportacionCompleta.NoDisponible);
        almacen.Aperturas.Should().Be(0);
        _repositorio.Auditoria.Should().BeEmpty();
    }

    [Fact]
    public async Task La_descarga_sin_TOTP_reciente_se_rechaza()
    {
        var (solicitud, almacen) = ExportacionLista();

        var resultado = await Descarga(new Escenario(SuperoTotp: false), almacen).Handle(new DescargarExportacionCompletaCommand(solicitud.Id), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.TotpReciente);
        almacen.Aperturas.Should().Be(0);
    }

    [Theory]
    [InlineData("GestorCae", false)]
    [InlineData("Administrador", true)]
    public async Task La_descarga_de_quien_no_es_el_Administrador_del_Tenant_propietario_se_rechaza(string rol, bool origenAjeno)
    {
        var (solicitud, almacen) = ExportacionLista();

        var resultado = await Descarga(new Escenario(Rol: rol, OrigenAjeno: origenAjeno), almacen)
            .Handle(new DescargarExportacionCompletaCommand(solicitud.Id), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.SoloAdministradorDelTenant);
        almacen.Aperturas.Should().Be(0);
    }

    [Fact]
    public async Task La_descarga_bajo_Sesion_Privilegiada_se_rechaza()
    {
        var (solicitud, almacen) = ExportacionLista();
        var sesion = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), _tenant.Id, default, null);

        var resultado = await Descarga(new Escenario(Sesion: sesion), almacen).Handle(new DescargarExportacionCompletaCommand(solicitud.Id), default);

        resultado.Error.Should().Be(AutoridadExportacionCompleta.SinSesionPrivilegiadaNiSuplantacion);
        almacen.Aperturas.Should().Be(0);
    }

    [Fact]
    public async Task La_descarga_de_una_solicitud_de_otro_Tenant_se_rechaza()
    {
        var (solicitud, almacen) = ExportacionLista(tenantId: Guid.NewGuid());

        var resultado = await Descarga(new Escenario(), almacen).Handle(new DescargarExportacionCompletaCommand(solicitud.Id), default);

        resultado.Error.Should().Be(ErroresDescargaExportacionCompleta.NoDisponible);
        almacen.Aperturas.Should().Be(0);
    }

    [Fact]
    public async Task Si_la_auditoria_de_la_descarga_no_se_guarda_no_se_entrega_el_fichero()
    {
        var (solicitud, almacen) = ExportacionLista();
        _unidad.ExcepcionAlGuardar = new InvalidOperationException("sin auditoría");

        var accion = () => Descarga(new Escenario(), almacen).Handle(new DescargarExportacionCompletaCommand(solicitud.Id), default);

        await accion.Should().ThrowAsync<InvalidOperationException>();
        almacen.UltimoFlujo!.CanRead.Should().BeFalse("el flujo abierto se cierra si la auditoría falla");
    }

    private (SolicitudExportacionCompleta, AlmacenEnMemoria) ExportacionLista(Guid? tenantId = null)
    {
        var solicitud = new SolicitudExportacionCompleta(_usuarioId, "protegida", Ahora);
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(solicitud, tenantId ?? _tenant.Id);
        solicitud.MarcarEnCurso(Ahora);
        solicitud.MarcarLista("t/s.zip", 3, Ahora);
        _repositorio.Solicitudes.Add(solicitud);
        return (solicitud, new AlmacenEnMemoria("t/s.zip"));
    }

    private void NadaRegistrado()
    {
        _repositorio.Solicitudes.Should().BeEmpty();
        _repositorio.Auditoria.Should().BeEmpty();
        _unidad.VecesGuardado.Should().Be(0);
    }

    private sealed class RepositorioEnMemoria : ISolicitudExportacionCompletaRepository
    {
        public List<SolicitudExportacionCompleta> Solicitudes { get; } = [];
        public List<RegistroAuditoria> Auditoria { get; } = [];

        public Task<bool> ExisteQueCuentaParaElLimiteDesdeAsync(DateTime desdeUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(Solicitudes.Any(s => s.SolicitadaEnUtc > desdeUtc && s.CuentaParaElLimite));

        public Task<SolicitudExportacionCompleta?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Solicitudes.SingleOrDefault(s => s.Id == id));

        public void Agregar(SolicitudExportacionCompleta solicitud) => Solicitudes.Add(solicitud);

        public void AgregarAuditoria(RegistroAuditoria registro) => Auditoria.Add(registro);
    }

    private sealed class AlmacenEnMemoria(string ubicacion) : IAlmacenExportacionesCompletas
    {
        public int Aperturas { get; private set; }
        public MemoryStream? UltimoFlujo { get; private set; }

        public Task<Stream> AbrirAsync(Guid tenantId, string ubicacionPedida, CancellationToken cancellationToken = default)
        {
            if (ubicacionPedida != ubicacion)
                throw new FileNotFoundException();
            Aperturas++;
            UltimoFlujo = new MemoryStream([1, 2, 3]);
            return Task.FromResult<Stream>(UltimoFlujo);
        }
    }

    private sealed class ProtectorFalso : IProtectorContrasenaExportacion
    {
        public string Proteger(string contrasena) => "protegida:" + new string(contrasena.Reverse().ToArray());

        public string Desproteger(string contrasenaProtegida) =>
            new(contrasenaProtegida["protegida:".Length..].Reverse().ToArray());
    }

    private sealed class CandadoDirecto : ICandadoExportacionCompleta
    {
        public Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default) =>
            operacion(cancellationToken);
    }

    private sealed class TenantFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class SesionFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }

    private sealed class AdministradorFijo(bool es) : IAdministradorDelTenantPropietario
    {
        public Task<bool> EsAdministradorEnBaseAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(es);
    }

    private sealed class RelojFijo(DateTime ahora) : TimeProvider
    {
        private DateTimeOffset _ahora = new(ahora);
        public void Avanzar(TimeSpan intervalo) => _ahora += intervalo;
        public override DateTimeOffset GetUtcNow() => _ahora;
    }
}
