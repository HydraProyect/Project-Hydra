using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.AsignarRolACuenta;
using CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario;
using CaeManager.Application.Usuarios.Commands.CrearUsuario;
using CaeManager.Application.Usuarios.Commands.EditarUsuario;
using CaeManager.Application.Usuarios.Commands.EliminarUsuarioPendiente;
using CaeManager.Application.Usuarios.Commands.GenerarActivacionUsuario;
using CaeManager.Application.Usuarios.Queries.ObtenerCuentaUsuario;
using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// P1-I2: la autorización de la gestión de cuentas vive en los handlers de
/// <c>Usuarios/Commands</c>, no en la página. Aquí se prueba contra un doble del
/// puerto de Identity que registra si se llegó a escribir: cada denegación tiene
/// que dejar el registro vacío. La frontera de tenant contra PostgreSQL real está
/// en <c>FronteraDeTenantEnGestionDeUsuariosTests</c> (IntegrationTests).
/// </summary>
public class GestionCuentasCommandsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid Cuenta = Guid.NewGuid();

    private static CurrentUserServiceFalso ActorCon(string? rol, Guid? tenantOrigen = null) =>
        new(Actor, rol, tenantOrigen ?? Tenant);

    private static readonly ITenantActual EnSuTenant = new TenantFijo(Tenant);

    // ---------- Quién administra cuentas ----------

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Solo_Administrador_y_Direccion_CAE_dan_de_alta_una_cuenta(string? rol)
    {
        var puerto = new GestionCuentasFalsa();

        var resultado = await new CrearUsuarioCommandHandler(puerto, ActorCon(rol), EnSuTenant)
            .Handle(Alta("GestorCae"), default);

        resultado.Error.Should().Be(AutoridadSobreCuentas.SinAutoridad);
        puerto.Escrituras.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Sin_autoridad_no_se_edita_activa_elimina_ni_reenvia_nada(string? rol)
    {
        var puerto = new GestionCuentasFalsa { [Cuenta] = CuentaPropia("GestorCae", pendiente: true) };
        var actor = ActorCon(rol);

        (await new EditarUsuarioCommandHandler(puerto, actor, EnSuTenant).Handle(Edicion("GestorCae"), default))
            .Error.Should().Be(AutoridadSobreCuentas.SinAutoridad);
        (await new CambiarActivacionUsuarioCommandHandler(puerto, actor).Handle(new(Cuenta, false), default))
            .Error.Should().Be(AutoridadSobreCuentas.SinAutoridad);
        (await new EliminarUsuarioPendienteCommandHandler(puerto, actor).Handle(new(Cuenta), default))
            .Error.Should().Be(AutoridadSobreCuentas.SinAutoridad);
        (await new GenerarActivacionUsuarioCommandHandler(puerto, actor).Handle(new(Cuenta), default))
            .Error.Should().Be(AutoridadSobreCuentas.SinAutoridad);
        (await new ObtenerCuentaUsuarioQueryHandler(puerto, actor).Handle(new(Cuenta), default))
            .Error.Should().Be(AutoridadSobreCuentas.SinAutoridad);

        puerto.Escrituras.Should().BeEmpty();
    }

    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("GestorCae")]
    public async Task Solo_un_Administrador_asigna_rol_a_una_cuenta_pendiente(string rol)
    {
        var puerto = new GestionCuentasFalsa { [Cuenta] = CuentaPropia(null) };

        var resultado = await new AsignarRolACuentaCommandHandler(puerto, ActorCon(rol), EnSuTenant)
            .Handle(new AsignarRolACuentaCommand(Cuenta, "Consulta"), default);

        resultado.Error.Should().Be(AutoridadSobreCuentas.SinAutoridad);
        puerto.Escrituras.Should().BeEmpty();
    }

    // ---------- Alta ----------

    [Fact]
    public async Task El_alta_nace_en_el_Context_Workspace_activo_con_su_rol_y_un_token_de_activacion()
    {
        var puerto = new GestionCuentasFalsa();

        var resultado = await new CrearUsuarioCommandHandler(puerto, ActorCon("DireccionCae"), EnSuTenant)
            .Handle(Alta("Consulta"), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        puerto.Creadas.Should().ContainSingle().Which.TenantId.Should().Be(Tenant);
        puerto.Escrituras.Should().Equal($"crear:{resultado.Valor.UsuarioId}", $"rol:{resultado.Valor.UsuarioId}:Consulta");
        resultado.Valor.TokenActivacion.Should().Be("token");
        resultado.Valor.FalloAlAsignarRol.Should().BeNull();
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Un_rol_reservado_no_se_da_de_alta_desde_un_Context_Workspace_ajeno(string rol)
    {
        var puerto = new GestionCuentasFalsa();

        var resultado = await new CrearUsuarioCommandHandler(puerto, ActorCon("Administrador", OtroTenant), EnSuTenant)
            .Handle(Alta(rol), default);

        resultado.Error.Codigo.Should().Be("Usuarios.RolReservadoAlTenantDeOrigen");
        puerto.Escrituras.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Administrador", "Administrador", true)]
    [InlineData("DireccionCae", "Administrador", false)]
    [InlineData("Administrador", "GestorCae", false)]
    public async Task El_permiso_sensible_solo_lo_concede_un_Administrador_a_otro_Administrador(
        string rolActor, string rolNuevo, bool esperado)
    {
        var puerto = new GestionCuentasFalsa();

        await new CrearUsuarioCommandHandler(puerto, ActorCon(rolActor), EnSuTenant)
            .Handle(Alta(rolNuevo, permiso: true), default);

        puerto.Creadas.Should().ContainSingle().Which.PermisoConsultarAccesoDocumentosSensibles.Should().Be(esperado);
    }

    [Fact]
    public async Task Una_cuenta_Cliente_sin_empresa_vinculada_no_se_da_de_alta()
    {
        var puerto = new GestionCuentasFalsa();

        var resultado = await new CrearUsuarioCommandHandler(puerto, ActorCon("Administrador"), EnSuTenant)
            .Handle(Alta("Cliente"), default);

        resultado.Error.Should().Be(AutoridadSobreCuentas.ClienteRequerido);
        puerto.Escrituras.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_rol_que_no_existe_no_se_da_de_alta()
    {
        var puerto = new GestionCuentasFalsa();

        var resultado = await new CrearUsuarioCommandHandler(puerto, ActorCon("Administrador"), EnSuTenant)
            .Handle(Alta("SuperAdmin"), default);

        resultado.Error.Should().Be(AutoridadSobreCuentas.RolDesconocido);
        puerto.Escrituras.Should().BeEmpty();
    }

    // ---------- Propiedad, no visibilidad ----------

    [Fact]
    public async Task La_cuenta_de_otra_organizacion_no_se_edita_activa_elimina_ni_reenvia()
    {
        var puerto = new GestionCuentasFalsa { [Cuenta] = CuentaPropia("GestorCae", pendiente: true) with { EsPropiaDelTenantActual = false } };
        var actor = ActorCon("Administrador");

        (await new EditarUsuarioCommandHandler(puerto, actor, EnSuTenant).Handle(Edicion("GestorCae"), default))
            .Error.Should().Be(AutoridadSobreCuentas.NoEncontrado);
        (await new CambiarActivacionUsuarioCommandHandler(puerto, actor).Handle(new(Cuenta, false), default))
            .Error.Should().Be(AutoridadSobreCuentas.NoEncontrado);
        (await new EliminarUsuarioPendienteCommandHandler(puerto, actor).Handle(new(Cuenta), default))
            .Error.Should().Be(AutoridadSobreCuentas.NoEncontrado);
        (await new GenerarActivacionUsuarioCommandHandler(puerto, actor).Handle(new(Cuenta), default))
            .Error.Should().Be(AutoridadSobreCuentas.NoEncontrado);
        (await new ObtenerCuentaUsuarioQueryHandler(puerto, actor).Handle(new(Cuenta), default))
            .Error.Should().Be(AutoridadSobreCuentas.NoEncontrado);

        puerto.Escrituras.Should().BeEmpty();
    }

    [Fact]
    public async Task Asignar_rol_pregunta_por_la_propiedad_antes_de_leer_la_cuenta()
    {
        var puerto = new GestionCuentasFalsa { [Cuenta] = CuentaPropia(null) with { EsPropiaDelTenantActual = false } };

        var resultado = await new AsignarRolACuentaCommandHandler(puerto, ActorCon("Administrador"), EnSuTenant)
            .Handle(new AsignarRolACuentaCommand(Cuenta, "Consulta"), default);

        resultado.Error.Should().Be(AutoridadSobreCuentas.NoEncontrado);
        puerto.Lecturas.Should().BeEmpty("sin ser propia ni siquiera se lee la cuenta");
        puerto.Escrituras.Should().BeEmpty();
    }

    [Fact]
    public async Task No_se_asigna_un_segundo_rol_a_una_cuenta_que_ya_tiene_uno()
    {
        var puerto = new GestionCuentasFalsa { [Cuenta] = CuentaPropia("GestorCae") };

        var resultado = await new AsignarRolACuentaCommandHandler(puerto, ActorCon("Administrador"), EnSuTenant)
            .Handle(new AsignarRolACuentaCommand(Cuenta, "Consulta"), default);

        resultado.Error.Should().Be(AsignarRolACuentaCommandHandler.CuentaConRol,
            "AddToRoleAsync añade sin quitar: la invariante es un rol por cuenta");
        puerto.Escrituras.Should().BeEmpty();
    }

    // ---------- Edición ----------

    [Fact]
    public async Task Un_Administrador_no_se_concede_a_si_mismo_el_permiso_sensible()
    {
        var puerto = new GestionCuentasFalsa { [Actor] = CuentaPropia("Administrador", id: Actor) };

        var resultado = await new EditarUsuarioCommandHandler(puerto, ActorCon("Administrador"), EnSuTenant)
            .Handle(new EditarUsuarioCommand(Actor, "Yo", "Administrador", null, null, true), default);

        resultado.Error.Should().Be(EditarUsuarioCommandHandler.AutogestionPermisoSensible);
        puerto.Escrituras.Should().BeEmpty();
    }

    [Fact]
    public async Task Direccion_CAE_que_promueve_a_Administrador_no_hereda_un_permiso_sensible_antiguo()
    {
        var puerto = new GestionCuentasFalsa
        {
            [Cuenta] = CuentaPropia("GestorCae") with { PermisoConsultarAccesoDocumentosSensibles = true },
        };

        var resultado = await new EditarUsuarioCommandHandler(puerto, ActorCon("DireccionCae"), EnSuTenant)
            .Handle(Edicion("Administrador", permiso: true), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        puerto.Datos.Should().ContainSingle().Which.PermisoConsultarAccesoDocumentosSensibles.Should().BeFalse();
    }

    [Fact]
    public async Task Direccion_CAE_editando_a_un_Administrador_conserva_su_permiso_sensible()
    {
        var puerto = new GestionCuentasFalsa
        {
            [Cuenta] = CuentaPropia("Administrador") with { PermisoConsultarAccesoDocumentosSensibles = true },
        };

        await new EditarUsuarioCommandHandler(puerto, ActorCon("DireccionCae"), EnSuTenant)
            .Handle(Edicion("Administrador", permiso: false), default);

        puerto.Datos.Should().ContainSingle().Which.PermisoConsultarAccesoDocumentosSensibles.Should().BeTrue();
        puerto.CambiosDeRol.Should().BeEmpty("conservar el rol no es concederlo");
    }

    [Fact]
    public async Task Conservar_un_rol_reservado_desde_un_Context_Workspace_ajeno_no_se_bloquea()
    {
        var puerto = new GestionCuentasFalsa { [Cuenta] = CuentaPropia("Administrador") };

        var resultado = await new EditarUsuarioCommandHandler(puerto, ActorCon("Administrador", OtroTenant), EnSuTenant)
            .Handle(Edicion("Administrador"), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
    }

    [Fact]
    public async Task Conceder_un_rol_reservado_desde_un_Context_Workspace_ajeno_no_escribe_nada()
    {
        var puerto = new GestionCuentasFalsa { [Cuenta] = CuentaPropia("GestorCae") };

        var resultado = await new EditarUsuarioCommandHandler(puerto, ActorCon("Administrador", OtroTenant), EnSuTenant)
            .Handle(Edicion("DireccionCae"), default);

        resultado.Error.Codigo.Should().Be("Usuarios.RolReservadoAlTenantDeOrigen");
        puerto.Escrituras.Should().BeEmpty();
    }

    // ---------- Activación, baja y reenvío ----------

    [Fact]
    public async Task Nadie_desactiva_ni_elimina_su_propia_cuenta()
    {
        var puerto = new GestionCuentasFalsa { [Actor] = CuentaPropia("Administrador", pendiente: true, id: Actor) };
        var actor = ActorCon("Administrador");

        (await new CambiarActivacionUsuarioCommandHandler(puerto, actor).Handle(new(Actor, false), default))
            .Error.Codigo.Should().Be("Usuarios.PropiaCuenta");
        (await new EliminarUsuarioPendienteCommandHandler(puerto, actor).Handle(new(Actor), default))
            .Error.Codigo.Should().Be("Usuarios.PropiaCuenta");

        puerto.Escrituras.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_cuenta_que_ya_no_existe_pide_recargar_la_lista()
    {
        var puerto = new GestionCuentasFalsa();

        (await new CambiarActivacionUsuarioCommandHandler(puerto, ActorCon("Administrador")).Handle(new(Cuenta, false), default))
            .Error.Should().Be(AutoridadSobreCuentas.CuentaInexistente);
    }

    [Fact]
    public async Task Solo_se_elimina_una_cuenta_pendiente_de_activacion_y_sin_cartera_vigente()
    {
        var activada = new GestionCuentasFalsa { [Cuenta] = CuentaPropia("GestorCae", pendiente: false) };
        (await new EliminarUsuarioPendienteCommandHandler(activada, ActorCon("Administrador")).Handle(new(Cuenta), default))
            .Error.Should().Be(EliminarUsuarioPendienteCommandHandler.NoPendiente);

        var conCartera = new GestionCuentasFalsa { [Cuenta] = CuentaPropia("GestorCae", pendiente: true), ConVinculoOperativo = true };
        (await new EliminarUsuarioPendienteCommandHandler(conCartera, ActorCon("Administrador")).Handle(new(Cuenta), default))
            .Error.Should().Be(EliminarUsuarioPendienteCommandHandler.CarteraVigente);

        activada.Escrituras.Should().BeEmpty();
        conCartera.Escrituras.Should().BeEmpty();

        var pendiente = new GestionCuentasFalsa { [Cuenta] = CuentaPropia("GestorCae", pendiente: true) };
        (await new EliminarUsuarioPendienteCommandHandler(pendiente, ActorCon("DireccionCae")).Handle(new(Cuenta), default))
            .EsExitoso.Should().BeTrue("control positivo");
        pendiente.Escrituras.Should().Equal($"eliminar:{Cuenta}");
    }

    [Fact]
    public async Task No_se_emite_un_enlace_de_activacion_para_una_cuenta_ya_activada()
    {
        var puerto = new GestionCuentasFalsa { [Cuenta] = CuentaPropia("GestorCae", pendiente: false) };

        var resultado = await new GenerarActivacionUsuarioCommandHandler(puerto, ActorCon("Administrador"))
            .Handle(new GenerarActivacionUsuarioCommand(Cuenta), default);

        resultado.Error.Should().Be(GenerarActivacionUsuarioCommandHandler.YaActivada);
        puerto.Escrituras.Should().BeEmpty();
    }

    // ---------- Dobles ----------

    private static CrearUsuarioCommand Alta(string rol, bool permiso = false) =>
        new("nueva@x.test", "Nueva", rol, null, null, permiso);

    private static EditarUsuarioCommand Edicion(string rol, bool permiso = false) =>
        new(Cuenta, "Nombre", rol, null, null, permiso);

    private static CuentaUsuario CuentaPropia(string? rol, bool pendiente = false, Guid? id = null) =>
        new(id ?? Cuenta, "c@x.test", "C", true, rol is null ? [] : [rol], pendiente, true, false);

    private sealed class TenantFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class GestionCuentasFalsa : IGestionCuentasUsuario
    {
        private readonly Dictionary<Guid, CuentaUsuario> _cuentas = [];

        public CuentaUsuario this[Guid id] { set => _cuentas[id] = value; }

        public bool ConVinculoOperativo { get; init; }
        public List<string> Escrituras { get; } = [];
        public List<Guid> Lecturas { get; } = [];
        public List<NuevaCuentaUsuario> Creadas { get; } = [];
        public List<DatosCuentaUsuario> Datos { get; } = [];
        public List<string> CambiosDeRol { get; } = [];

        public Task<CuentaUsuario?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        {
            Lecturas.Add(usuarioId);
            return Task.FromResult(_cuentas.GetValueOrDefault(usuarioId));
        }

        public Task<bool> EsPropiaDelTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_cuentas.TryGetValue(usuarioId, out var c) && c.EsPropiaDelTenantActual);

        public Task<bool> TieneVinculoOperativoAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConVinculoOperativo);

        public Task<Result<Guid>> CrearAsync(NuevaCuentaUsuario cuenta, CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            Creadas.Add(cuenta);
            Escrituras.Add($"crear:{id}");
            return Task.FromResult(Result.Exito(id));
        }

        public Task<Result> AsignarRolAsync(Guid usuarioId, string rol, CancellationToken cancellationToken = default)
        {
            Escrituras.Add($"rol:{usuarioId}:{rol}");
            return Task.FromResult(Result.Exito());
        }

        public Task<Result> ActualizarDatosAsync(Guid usuarioId, DatosCuentaUsuario datos, CancellationToken cancellationToken = default)
        {
            Datos.Add(datos);
            Escrituras.Add($"datos:{usuarioId}");
            return Task.FromResult(Result.Exito());
        }

        public Task<ResultadoCambioRol> CambiarRolAsync(Guid usuarioId, string rolNuevo, CancellationToken cancellationToken = default)
        {
            CambiosDeRol.Add(rolNuevo);
            Escrituras.Add($"cambiarRol:{usuarioId}:{rolNuevo}");
            return Task.FromResult(new ResultadoCambioRol(DesenlaceCambioRol.Cambiado));
        }

        public Task<Result> CambiarActivacionAsync(Guid usuarioId, bool activar, CancellationToken cancellationToken = default)
        {
            Escrituras.Add($"activacion:{usuarioId}:{activar}");
            return Task.FromResult(Result.Exito());
        }

        public Task<Result> EliminarAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        {
            Escrituras.Add($"eliminar:{usuarioId}");
            return Task.FromResult(Result.Exito());
        }

        public Task<Result<string>> GenerarTokenActivacionAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Exito("token"));
    }
}
