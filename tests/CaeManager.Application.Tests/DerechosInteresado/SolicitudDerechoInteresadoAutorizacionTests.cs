using CaeManager.Application.CapacidadesTenant;
using CaeManager.Application.CapacidadesTenant.Commands.ConcederCapacidadTenant;
using CaeManager.Application.CapacidadesTenant.Commands.RevocarCapacidadTenant;
using CaeManager.Application.Common;
using CaeManager.Application.DerechosInteresado;
using CaeManager.Application.DerechosInteresado.Commands.RegistrarSolicitudDerechoInteresado;
using CaeManager.Application.DerechosInteresado.Commands.TramitarSolicitudDerechoInteresado;
using CaeManager.Application.DerechosInteresado.Queries;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.DerechosInteresado;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.DerechosInteresado;

/// <summary>
/// Quién registra y quién resuelve una solicitud de derechos del interesado (#2002, decisión D-3 del 2026-11-15):
/// registra cualquier cuenta con escritura, también un Gestor CAE de un Operador CAE externo; tramitan y resuelven
/// solo el Administrador del Tenant propietario y las cuentas del propio Tenant con la capacidad «Derechos del
/// interesado», que concede y revoca él. La RLS de las dos tablas la fija IntegrationTests.
/// </summary>
public class SolicitudDerechoInteresadoAutorizacionTests
{
    private static readonly Guid TenantPropietario = Guid.NewGuid();
    private static readonly Guid TenantOperadorExterno = Guid.NewGuid();
    private static readonly Guid Administrador = Guid.NewGuid();
    private static readonly Guid Coordinadora = Guid.NewGuid();
    private static readonly Guid GestoraExterna = Guid.NewGuid();

    // ── Quién resuelve ────────────────────────────────────────────────────────

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_tramita_y_resuelve_la_solicitud()
    {
        var mundo = new Mundo();
        var autoridad = mundo.Autoridad(Administrador, "Administrador", TenantPropietario, administradorEnBase: true);

        var resultados = await mundo.RecorrerHastaResolver(autoridad, Administrador);

        resultados.Should().AllSatisfy(r => r.EsExitoso.Should().BeTrue());
        mundo.Solicitud.Estado.Should().Be(EstadoSolicitudDerecho.Resuelta);
        mundo.Solicitud.CerradaPorUsuarioId.Should().Be(Administrador, "el actor de cada paso es el actor real");
    }

    [Fact]
    public async Task Una_cuenta_del_Tenant_con_la_capacidad_vigente_tramita_y_resuelve()
    {
        var mundo = new Mundo();
        mundo.Concesiones.Agregar(ConcesionCapacidadTenant.Conceder(
            Coordinadora, CapacidadTenant.DerechosDelInteresado, Administrador, DateTime.UtcNow));
        var autoridad = mundo.Autoridad(Coordinadora, "CoordinadorCae", TenantPropietario);

        var resultados = await mundo.RecorrerHastaResolver(autoridad, Coordinadora);

        resultados.Should().AllSatisfy(r => r.EsExitoso.Should().BeTrue());
        mundo.Solicitud.Estado.Should().Be(EstadoSolicitudDerecho.Resuelta);
    }

    [Fact]
    public async Task Direccion_CAE_sin_la_capacidad_no_resuelve()
    {
        var mundo = new Mundo();
        var autoridad = mundo.Autoridad(Coordinadora, "DireccionCae", TenantPropietario);

        var resultado = await mundo.Verificar(autoridad, Coordinadora);

        resultado.Error.Should().Be(ErroresDerechosInteresado.SinCapacidad);
        mundo.Solicitud.Estado.Should().Be(EstadoSolicitudDerecho.Recibida);
        mundo.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task La_lista_y_la_ficha_solo_las_lee_quien_tiene_la_capacidad()
    {
        var mundo = new Mundo();
        var conCapacidad = new SolicitudesDerechoInteresadoQueryHandler(
            mundo.Autoridad(Administrador, "Administrador", TenantPropietario, administradorEnBase: true), mundo.Solicitudes);
        var sinCapacidad = new SolicitudesDerechoInteresadoQueryHandler(
            mundo.Autoridad(Coordinadora, "DireccionCae", TenantPropietario), mundo.Solicitudes);

        (await conCapacidad.Handle(new ListarSolicitudesDerechoInteresadoQuery(), default))
            .Should().ContainSingle(s => s.Id == mundo.Solicitud.Id);
        (await conCapacidad.Handle(new ObtenerSolicitudDerechoInteresadoQuery(mundo.Solicitud.Id), default))
            .Should().NotBeNull();
        (await sinCapacidad.Handle(new ListarSolicitudesDerechoInteresadoQuery(), default)).Should().BeEmpty();
        (await sinCapacidad.Handle(new ObtenerSolicitudDerechoInteresadoQuery(mundo.Solicitud.Id), default))
            .Should().BeNull("sin la capacidad la ficha no existe para quien la pide");
    }

    [Fact]
    public async Task Una_cuenta_con_la_capacidad_revocada_no_resuelve()
    {
        var mundo = new Mundo();
        var concesion = ConcesionCapacidadTenant.Conceder(
            Coordinadora, CapacidadTenant.DerechosDelInteresado, Administrador, DateTime.UtcNow.AddDays(-3));
        mundo.Concesiones.Agregar(concesion);
        var autoridadDeLaCoordinadora = mundo.Autoridad(Coordinadora, "CoordinadorCae", TenantPropietario);
        (await mundo.Verificar(autoridadDeLaCoordinadora, Coordinadora)).EsExitoso.Should().BeTrue("antes de revocar la tiene");

        var revocar = await new RevocarCapacidadTenantCommandHandler(
                mundo.Autoridad(Administrador, "Administrador", TenantPropietario, administradorEnBase: true),
                new ActorFijo(Normal(Administrador)), mundo.Concesiones, mundo.UnitOfWork)
            .Handle(new RevocarCapacidadTenantCommand(Coordinadora, CapacidadTenant.DerechosDelInteresado), default);
        revocar.EsExitoso.Should().BeTrue();
        concesion.RevocadaPorUsuarioId.Should().Be(Administrador);

        var resultado = await mundo.Iniciar(autoridadDeLaCoordinadora, Coordinadora);

        resultado.Error.Should().Be(ErroresDerechosInteresado.SinCapacidad);
        mundo.Solicitud.Estado.Should().Be(EstadoSolicitudDerecho.IdentidadVerificada);
    }

    [Fact]
    public async Task Un_Administrador_en_contexto_cruzado_no_resuelve()
    {
        var mundo = new Mundo();
        // Todo lo demás le daría paso (rol Administrador, «Administrador en base», vía normal): solo cambia que su
        // Tenant de origen no es el actual.
        var autoridad = mundo.Autoridad(Administrador, "Administrador", TenantOperadorExterno, administradorEnBase: true);

        (await mundo.Verificar(autoridad, Administrador)).Error.Should().Be(ErroresDerechosInteresado.SinCapacidad);
        (await mundo.Denegar(autoridad, Administrador)).Error.Should().Be(ErroresDerechosInteresado.SinCapacidad);
        mundo.Solicitud.Estado.Should().Be(EstadoSolicitudDerecho.Recibida);
    }

    [Fact]
    public async Task El_Gestor_CAE_del_Operador_CAE_externo_registra_pero_no_resuelve_ni_con_una_concesion_en_la_tabla()
    {
        var mundo = new Mundo();
        var registro = await mundo.Registrar(GestoraExterna, trabajadorId: null);
        registro.EsExitoso.Should().BeTrue("registrar es de cualquier cuenta con escritura");
        mundo.Solicitudes.Agregadas.Should().ContainSingle().Which.RegistradaPorUsuarioId.Should().Be(GestoraExterna);

        // Ni una concesión escrita a mano en la tabla le abre la tramitación: el contexto cruzado cierra antes.
        mundo.Concesiones.Agregar(ConcesionCapacidadTenant.Conceder(
            GestoraExterna, CapacidadTenant.DerechosDelInteresado, Administrador, DateTime.UtcNow));
        var autoridad = mundo.Autoridad(GestoraExterna, "GestorCae", TenantOperadorExterno,
            actor: new ActorAuditoria(GestoraExterna, null, TipoViaAcceso.OperacionDelegada, Guid.NewGuid()));

        (await mundo.Verificar(autoridad, GestoraExterna)).Error.Should().Be(ErroresDerechosInteresado.SinCapacidad);
        (await new SolicitudesDerechoInteresadoQueryHandler(autoridad, mundo.Solicitudes)
            .Handle(new ListarSolicitudesDerechoInteresadoQuery(), default)).Should().BeEmpty("quien solo registra no lee el registro");
    }

    [Fact]
    public async Task La_capacidad_no_se_concede_a_una_cuenta_de_un_Operador_CAE_externo()
    {
        var mundo = new Mundo();

        var resultado = await mundo.Conceder(
            mundo.Autoridad(Administrador, "Administrador", TenantPropietario, administradorEnBase: true),
            GestoraExterna, tenantDelBeneficiario: TenantOperadorExterno);

        resultado.Error.Should().Be(ErroresCapacidadesTenant.BeneficiarioNoValido);
        mundo.Concesiones.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task Sesion_Privilegiada_o_impersonacion_no_resuelven_aunque_el_rol_sea_Administrador()
    {
        var mundo = new Mundo();
        var sesion = new SesionPrivilegiadaActiva(
            Guid.NewGuid(), Guid.NewGuid(), TenantPropietario, CapacidadPrivilegio.AdminPlataforma, null);
        var bajoSesion = mundo.Autoridad(Administrador, "Administrador", TenantPropietario, administradorEnBase: true,
            sesion: sesion);
        var impersonando = mundo.Autoridad(Administrador, "Administrador", TenantPropietario, administradorEnBase: true,
            actor: new ActorAuditoria(Guid.NewGuid(), Administrador, TipoViaAcceso.Normal, null));

        (await mundo.Verificar(bajoSesion, Administrador)).Error.Should().Be(ErroresDerechosInteresado.SinCapacidad);
        (await mundo.Verificar(impersonando, Administrador)).Error.Should().Be(ErroresDerechosInteresado.SinCapacidad);
        (await bajoSesion.EsAdministradorDelTenantPropietarioAsync(default)).Should().BeFalse();
        (await impersonando.EsAdministradorDelTenantPropietarioAsync(default)).Should().BeFalse();
    }

    [Fact]
    public async Task Sin_Tenant_actual_o_sin_rol_efectivo_falla_cerrado()
    {
        var mundo = new Mundo();

        (await mundo.Autoridad(Administrador, "Administrador", TenantPropietario, administradorEnBase: true, conTenantActual: false)
            .TieneCapacidadAsync(CapacidadTenant.DerechosDelInteresado, default)).Should().BeFalse();
        (await mundo.Autoridad(Administrador, null, TenantPropietario, administradorEnBase: true)
            .TieneCapacidadAsync(CapacidadTenant.DerechosDelInteresado, default)).Should().BeFalse();
        (await mundo.Autoridad(Administrador, "Administrador", null, administradorEnBase: true)
            .TieneCapacidadAsync(CapacidadTenant.DerechosDelInteresado, default)).Should().BeFalse("sin Tenant de origen no se sabe si el contexto es cruzado");
    }

    [Fact]
    public async Task Rol_Administrador_en_la_sesion_sin_serlo_en_la_base_no_resuelve()
    {
        var mundo = new Mundo();

        var autoridad = mundo.Autoridad(Administrador, "Administrador", TenantPropietario, administradorEnBase: false);

        (await mundo.Verificar(autoridad, Administrador)).Error.Should().Be(ErroresDerechosInteresado.SinCapacidad);
    }

    [Fact]
    public async Task Con_la_concesion_vigente_pero_la_cuenta_desactivada_o_de_solo_lectura_no_resuelve()
    {
        var mundo = new Mundo();
        mundo.Concesiones.Agregar(ConcesionCapacidadTenant.Conceder(
            Coordinadora, CapacidadTenant.DerechosDelInteresado, Administrador, DateTime.UtcNow));

        var desactivada = mundo.Autoridad(Coordinadora, "CoordinadorCae", TenantPropietario, cuentaActiva: false);
        var consulta = mundo.Autoridad(Coordinadora, "Consulta", TenantPropietario);

        (await mundo.Verificar(desactivada, Coordinadora)).Error.Should().Be(ErroresDerechosInteresado.SinCapacidad);
        (await mundo.Verificar(consulta, Coordinadora)).Error.Should().Be(ErroresDerechosInteresado.SinCapacidad);
    }

    // ── Conceder y revocar ───────────────────────────────────────────────────

    [Fact]
    public async Task El_Administrador_concede_la_capacidad_a_una_cuenta_de_su_Tenant_con_el_actor_real()
    {
        var mundo = new Mundo();

        var resultado = await mundo.Conceder(
            mundo.Autoridad(Administrador, "Administrador", TenantPropietario, administradorEnBase: true),
            Coordinadora, tenantDelBeneficiario: TenantPropietario);

        resultado.EsExitoso.Should().BeTrue();
        var fila = mundo.Concesiones.Filas.Should().ContainSingle().Subject;
        fila.UsuarioId.Should().Be(Coordinadora);
        fila.ConcedidaPorUsuarioId.Should().Be(Administrador);
        fila.EstaVigente.Should().BeTrue();
    }

    [Fact]
    public async Task Quien_no_es_el_Administrador_del_Tenant_propietario_no_concede_ni_revoca()
    {
        var mundo = new Mundo();
        mundo.Concesiones.Agregar(ConcesionCapacidadTenant.Conceder(
            Coordinadora, CapacidadTenant.DerechosDelInteresado, Administrador, DateTime.UtcNow));
        // Dirección CAE con la capacidad: la ejerce, pero no la reparte.
        var direccion = mundo.Autoridad(Coordinadora, "DireccionCae", TenantPropietario);
        var cruzado = mundo.Autoridad(Administrador, "Administrador", TenantOperadorExterno, administradorEnBase: true);

        (await mundo.Conceder(direccion, Guid.NewGuid(), TenantPropietario)).Error
            .Should().Be(ErroresCapacidadesTenant.SoloAdministradorDelTenant);
        (await mundo.Conceder(cruzado, Guid.NewGuid(), TenantPropietario)).Error
            .Should().Be(ErroresCapacidadesTenant.SoloAdministradorDelTenant);
        (await new RevocarCapacidadTenantCommandHandler(direccion, new ActorFijo(Normal(Coordinadora)), mundo.Concesiones, mundo.UnitOfWork)
            .Handle(new RevocarCapacidadTenantCommand(Coordinadora, CapacidadTenant.DerechosDelInteresado), default))
            .Error.Should().Be(ErroresCapacidadesTenant.SoloAdministradorDelTenant);
        mundo.Concesiones.Filas.Should().ContainSingle().Which.EstaVigente.Should().BeTrue();
    }

    [Fact]
    public async Task No_se_concede_dos_veces_ni_a_una_cuenta_de_solo_lectura_y_no_se_revoca_lo_que_no_esta_vigente()
    {
        var mundo = new Mundo();
        var admin = mundo.Autoridad(Administrador, "Administrador", TenantPropietario, administradorEnBase: true);

        (await mundo.Conceder(admin, Coordinadora, TenantPropietario)).EsExitoso.Should().BeTrue();
        (await mundo.Conceder(admin, Coordinadora, TenantPropietario)).Error.Should().Be(ErroresCapacidadesTenant.YaConcedida);
        (await mundo.Conceder(admin, Guid.NewGuid(), TenantPropietario, cuentaConEscritura: false)).Error
            .Should().Be(ErroresCapacidadesTenant.BeneficiarioNoValido);
        (await new RevocarCapacidadTenantCommandHandler(admin, new ActorFijo(Normal(Administrador)), mundo.Concesiones, mundo.UnitOfWork)
            .Handle(new RevocarCapacidadTenantCommand(Guid.NewGuid(), CapacidadTenant.DerechosDelInteresado), default))
            .Error.Should().Be(ErroresCapacidadesTenant.NoVigente);
    }

    // ── Registrar ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registrar_con_un_Trabajador_fuera_de_la_cartera_se_rechaza_como_no_encontrado()
    {
        var mundo = new Mundo();
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Paula", "Campos", "12345678Z");
        mundo.Trabajadores.Agregar(trabajador);

        var fuera = await mundo.Registrar(GestoraExterna, trabajador.Id,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: []));
        var dentro = await mundo.Registrar(GestoraExterna, trabajador.Id,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajador.Id]));
        var inexistente = await mundo.Registrar(GestoraExterna, Guid.NewGuid());

        fuera.Error.Should().Be(ErroresDerechosInteresado.TrabajadorNoEncontrado);
        inexistente.Error.Should().Be(ErroresDerechosInteresado.TrabajadorNoEncontrado);
        dentro.EsExitoso.Should().BeTrue();
        mundo.Solicitudes.Agregadas.Should().ContainSingle().Which.TrabajadorId.Should().Be(trabajador.Id);
    }

    [Fact]
    public void El_validador_del_registro_rechaza_fecha_futura_nombre_vacio_y_valores_desconocidos()
    {
        var validador = new RegistrarSolicitudDerechoInteresadoCommandValidator();
        var valido = new RegistrarSolicitudDerechoInteresadoCommand(
            TipoDerechoInteresado.Acceso, CanalEntradaSolicitudDerecho.CorreoPostal, DiaDeNegocio.Hoy(), "Paula", null, null);

        validador.Validate(valido).IsValid.Should().BeTrue();
        validador.Validate(valido with { FechaRecepcion = DiaDeNegocio.Hoy().AddDays(1) }).IsValid.Should().BeFalse();
        validador.Validate(valido with { NombreInteresado = "  " }).IsValid.Should().BeFalse();
        validador.Validate(valido with { Tipo = (TipoDerechoInteresado)99 }).IsValid.Should().BeFalse();
        validador.Validate(valido with { Canal = (CanalEntradaSolicitudDerecho)99 }).IsValid.Should().BeFalse();
        validador.Validate(valido with { TrabajadorId = Guid.Empty }).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Un_paso_que_el_dominio_no_permite_devuelve_error_sin_guardar()
    {
        var mundo = new Mundo();
        var autoridad = mundo.Autoridad(Administrador, "Administrador", TenantPropietario, administradorEnBase: true);

        var resolverSinVerificar = await new TramitarSolicitudDerechoInteresadoCommandHandler(
                autoridad, new ActorFijo(Normal(Administrador)), mundo.Solicitudes, mundo.UnitOfWork)
            .Handle(new ResolverSolicitudDerechoCommand(mundo.Solicitud.Id, null), default);
        var otraSolicitud = await new TramitarSolicitudDerechoInteresadoCommandHandler(
                autoridad, new ActorFijo(Normal(Administrador)), mundo.Solicitudes, mundo.UnitOfWork)
            .Handle(new VerificarIdentidadSolicitudDerechoCommand(Guid.NewGuid()), default);

        resolverSinVerificar.Error!.Codigo.Should().Be("SolicitudDerecho.PasoNoPermitido");
        otraSolicitud.Error.Should().Be(ErroresDerechosInteresado.NoEncontrada);
        mundo.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    // ── Arnés ────────────────────────────────────────────────────────────────

    private static ActorAuditoria Normal(Guid usuario) => new(usuario, null, TipoViaAcceso.Normal, null);

    private sealed class Mundo
    {
        public ConcesionesFalsas Concesiones { get; } = new();
        public SolicitudesFalsas Solicitudes { get; } = new();
        public TrabajadorRepositorioFalso Trabajadores { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        public SolicitudDerechoInteresado Solicitud { get; }

        public Mundo()
        {
            Solicitud = SolicitudDerechoInteresado.Registrar(
                TipoDerechoInteresado.Supresion, CanalEntradaSolicitudDerecho.CorreoElectronico, DiaDeNegocio.Hoy(),
                "Paula Campos", null, null, GestoraExterna, DiaDeNegocio.Hoy(), DateTime.UtcNow);
            Solicitudes.Existentes.Add(Solicitud);
        }

        public AutoridadCapacidadesTenant Autoridad(
            Guid usuario, string? rolEfectivo, Guid? tenantOrigen, bool administradorEnBase = false,
            SesionPrivilegiadaActiva? sesion = null, ActorAuditoria? actor = null, bool cuentaActiva = true,
            bool conTenantActual = true)
        {
            var tenant = TenantPropietario;
            return new AutoridadCapacidadesTenant(
                new TenantActualFijo(conTenantActual ? tenant : null),
                new CurrentUserServiceFalso(usuario, rolEfectivo, tenantOrigen),
                new SesionPrivilegiadaFalsa(sesion),
                new ActorFijo(actor ?? Normal(usuario)),
                new AdministradorFalso(administradorEnBase ? tenant : null),
                new DirectorioUsuariosServiceFalso(cuentaActivaConRol: cuentaActiva),
                Concesiones);
        }

        public async Task<Result> Conceder(
            IAutoridadCapacidadesTenant autoridad, Guid beneficiario, Guid tenantDelBeneficiario, bool cuentaConEscritura = true)
        {
            var actor = await autoridad.EsAdministradorDelTenantPropietarioAsync(default) ? Administrador : Coordinadora;
            return await new ConcederCapacidadTenantCommandHandler(
                    autoridad, new TenantActualFijo(TenantPropietario), new ActorFijo(Normal(actor)),
                    new DirectorioUsuariosServiceFalso(tenantDelUsuario: tenantDelBeneficiario, cuentaActivaConRol: cuentaConEscritura),
                    Concesiones)
                .Handle(new ConcederCapacidadTenantCommand(beneficiario, CapacidadTenant.DerechosDelInteresado), default);
        }

        public Task<Result<Guid>> Registrar(Guid registrador, Guid? trabajadorId, IAlcanceDatosService? alcance = null) =>
            new RegistrarSolicitudDerechoInteresadoCommandHandler(
                    new ActorFijo(Normal(registrador)), Trabajadores, alcance ?? new AlcanceDatosServiceFalso(),
                    Solicitudes, UnitOfWork)
                .Handle(new RegistrarSolicitudDerechoInteresadoCommand(
                    TipoDerechoInteresado.Acceso, CanalEntradaSolicitudDerecho.Presencial, DiaDeNegocio.Hoy(),
                    "Paula Campos", trabajadorId, null), default);

        private TramitarSolicitudDerechoInteresadoCommandHandler Tramitar(IAutoridadCapacidadesTenant autoridad, Guid actor) =>
            new(autoridad, new ActorFijo(Normal(actor)), Solicitudes, UnitOfWork);

        public Task<Result> Verificar(IAutoridadCapacidadesTenant autoridad, Guid actor) =>
            Tramitar(autoridad, actor).Handle(new VerificarIdentidadSolicitudDerechoCommand(Solicitud.Id), default);

        public Task<Result> Iniciar(IAutoridadCapacidadesTenant autoridad, Guid actor) =>
            Tramitar(autoridad, actor).Handle(new IniciarTramitacionSolicitudDerechoCommand(Solicitud.Id), default);

        public Task<Result> Denegar(IAutoridadCapacidadesTenant autoridad, Guid actor) =>
            Tramitar(autoridad, actor).Handle(new DenegarSolicitudDerechoCommand(Solicitud.Id, "Motivo."), default);

        public async Task<IReadOnlyList<Result>> RecorrerHastaResolver(IAutoridadCapacidadesTenant autoridad, Guid actor)
        {
            var t = Tramitar(autoridad, actor);
            return
            [
                await t.Handle(new VerificarIdentidadSolicitudDerechoCommand(Solicitud.Id), default),
                await t.Handle(new ProrrogarSolicitudDerechoCommand(
                    Solicitud.Id, Solicitud.PlazoOrdinario.AddDays(10), "Complejidad."), default),
                await t.Handle(new IniciarTramitacionSolicitudDerechoCommand(Solicitud.Id), default),
                await t.Handle(new ResolverSolicitudDerechoCommand(Solicitud.Id, "Hecho."), default),
            ];
        }
    }

    private sealed class ConcesionesFalsas : IConcesionCapacidadTenantRepository
    {
        public List<ConcesionCapacidadTenant> Filas { get; } = [];

        public Task<ConcesionCapacidadTenant?> ObtenerVigenteAsync(
            Guid usuarioId, CapacidadTenant capacidad, CancellationToken cancellationToken) =>
            Task.FromResult(Filas.FirstOrDefault(c => c.UsuarioId == usuarioId && c.Capacidad == capacidad && c.EstaVigente));

        public Task<bool> TieneVigenteAsync(Guid usuarioId, CapacidadTenant capacidad, CancellationToken cancellationToken) =>
            Task.FromResult(Filas.Any(c => c.UsuarioId == usuarioId && c.Capacidad == capacidad && c.EstaVigente));

        public void Agregar(ConcesionCapacidadTenant concesion) => Filas.Add(concesion);

        public Task<bool> GuardarConcesionAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class SolicitudesFalsas : ISolicitudDerechoInteresadoRepository
    {
        public List<SolicitudDerechoInteresado> Existentes { get; } = [];
        public List<SolicitudDerechoInteresado> Agregadas { get; } = [];

        public Task<SolicitudDerechoInteresado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Existentes.FirstOrDefault(s => s.Id == id));

        public Task<IReadOnlyList<SolicitudDerechoInteresado>> ListarAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SolicitudDerechoInteresado>>(Existentes);

        public void Agregar(SolicitudDerechoInteresado solicitud) => Agregadas.Add(solicitud);
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class AdministradorFalso(Guid? tenantDelAdministrador) : IAdministradorDelTenantPropietario
    {
        public Task<bool> EsAdministradorEnBaseAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantDelAdministrador == tenantId);
    }

    private sealed class TenantActualFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class SesionPrivilegiadaFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }
}
