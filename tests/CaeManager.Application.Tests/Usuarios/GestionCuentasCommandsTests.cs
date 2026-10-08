using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.AsignarRolACuenta;
using CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario;
using CaeManager.Application.Usuarios.Commands.CrearUsuario;
using CaeManager.Application.Usuarios.Commands.EditarUsuario;
using CaeManager.Application.Usuarios.Commands.EliminarUsuarioPendiente;
using CaeManager.Application.Usuarios.Commands.GenerarActivacionUsuario;
using CaeManager.Application.Usuarios.Queries.ObtenerCuentaUsuario;
using CaeManager.Application.Usuarios.Queries.ObtenerEmpresasAsignablesEnAlta;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
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

        var resultado = await NuevoAlta(puerto, ActorCon(rol), EnSuTenant)
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
        (await new CambiarActivacionUsuarioCommandHandler(puerto, actor, new CaeManager.Application.Tests.Clientes.TransaccionDeComandoFalsa(), new CaeManager.Application.Tests.Clientes.BloqueoCarteraUsuarioFalso(), new CatalogoIncorporacionCarteraFalso(), new DirectorioDestinosCarteraFalso(null), new DirectorioRolesEnOrigen()).Handle(new(Cuenta, false), default))
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

        var resultado = await NuevoAlta(puerto, ActorCon("DireccionCae"), EnSuTenant)
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

        var resultado = await NuevoAlta(puerto, ActorCon("Administrador", OtroTenant), EnSuTenant)
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

        await NuevoAlta(puerto, ActorCon(rolActor), EnSuTenant)
            .Handle(Alta(rolNuevo, permiso: true), default);

        puerto.Creadas.Should().ContainSingle().Which.PermisoConsultarAccesoDocumentosSensibles.Should().Be(esperado);
    }

    [Fact]
    public async Task Una_cuenta_Cliente_sin_empresa_vinculada_no_se_da_de_alta()
    {
        var puerto = new GestionCuentasFalsa();

        var resultado = await NuevoAlta(puerto, ActorCon("Administrador"), EnSuTenant)
            .Handle(Alta("Cliente"), default);

        resultado.Error.Should().Be(AutoridadSobreCuentas.ClienteRequerido);
        puerto.Escrituras.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_rol_que_no_existe_no_se_da_de_alta()
    {
        var puerto = new GestionCuentasFalsa();

        var resultado = await NuevoAlta(puerto, ActorCon("Administrador"), EnSuTenant)
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
        (await new CambiarActivacionUsuarioCommandHandler(puerto, actor, new CaeManager.Application.Tests.Clientes.TransaccionDeComandoFalsa(), new CaeManager.Application.Tests.Clientes.BloqueoCarteraUsuarioFalso(), new CatalogoIncorporacionCarteraFalso(), new DirectorioDestinosCarteraFalso(null), new DirectorioRolesEnOrigen()).Handle(new(Cuenta, false), default))
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

        (await new CambiarActivacionUsuarioCommandHandler(puerto, actor, new CaeManager.Application.Tests.Clientes.TransaccionDeComandoFalsa(), new CaeManager.Application.Tests.Clientes.BloqueoCarteraUsuarioFalso(), new CatalogoIncorporacionCarteraFalso(), new DirectorioDestinosCarteraFalso(null), new DirectorioRolesEnOrigen()).Handle(new(Actor, false), default))
            .Error.Codigo.Should().Be("Usuarios.PropiaCuenta");
        (await new EliminarUsuarioPendienteCommandHandler(puerto, actor).Handle(new(Actor), default))
            .Error.Codigo.Should().Be("Usuarios.PropiaCuenta");

        puerto.Escrituras.Should().BeEmpty();
    }

    /// <summary>
    /// Revisión Codex de FS-25 (ronda 2): desactivar toma el candado exclusivo de cartera de
    /// la cuenta dentro de una transacción; reactivar no lo necesita.
    /// </summary>
    [Fact]
    public async Task Desactivar_toma_el_candado_exclusivo_de_cartera_dentro_de_una_transaccion()
    {
        var puerto = new GestionCuentasFalsa { [Cuenta] = CuentaPropia("GestorCae") };
        var transaccion = new CaeManager.Application.Tests.Clientes.TransaccionDeComandoFalsa();
        var bloqueo = new CaeManager.Application.Tests.Clientes.BloqueoCarteraUsuarioFalso();
        var handler = new CambiarActivacionUsuarioCommandHandler(puerto, ActorCon("Administrador"), transaccion, bloqueo, new CatalogoIncorporacionCarteraFalso(), new DirectorioDestinosCarteraFalso(null), new DirectorioRolesEnOrigen());

        (await handler.Handle(new(Cuenta, false), default)).EsExitoso.Should().BeTrue();
        (await handler.Handle(new(Cuenta, true), default)).EsExitoso.Should().BeTrue();

        bloqueo.Exclusivos.Should().Equal(Cuenta);
        transaccion.Confirmadas.Should().Be(2);
        puerto.Escrituras.Should().Equal($"activacion:{Cuenta}:False", $"activacion:{Cuenta}:True");
    }

    // ---------- Relevo del principal al desactivar (ADR-011 § 2.7, enmienda 2026-10-08) ----------

    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly Guid OperacionExterna = Guid.NewGuid();
    private static readonly Guid Coordinador = Guid.NewGuid();
    private static readonly Guid Apoyo = Guid.NewGuid();

    private readonly CaeManager.Application.Tests.Clientes.BloqueoCarteraUsuarioFalso _bloqueoDelRelevo = new();

    private (CambiarActivacionUsuarioCommandHandler Handler, CatalogoIncorporacionCarteraFalso Catalogo, CaeManager.Application.Tests.Clientes.TransaccionDeComandoFalsa Transaccion)
        DesactivacionDelPrincipal(Guid? coordinador, bool coordinadorActivo = true)
    {
        var catalogo = new CatalogoIncorporacionCarteraFalso();
        catalogo.CarterasVivas.Add((Tenant, new CarteraVivaDeOperacion(OperacionExterna, Propietario, "Talleres Norte", Cuenta, "GestorCae", true, null)));
        catalogo.CarterasVivas.Add((Tenant, new CarteraVivaDeOperacion(OperacionExterna, Propietario, "Talleres Norte", Apoyo, "GestorCae", false, null)));
        var roles = new DirectorioRolesEnOrigen();
        roles.Asignar(Coordinador, Tenant, "CoordinadorCae");
        if (!coordinadorActivo) roles.Desactivar(Coordinador);
        var transaccion = new CaeManager.Application.Tests.Clientes.TransaccionDeComandoFalsa();
        var handler = new CambiarActivacionUsuarioCommandHandler(
            new GestionCuentasFalsa { [Cuenta] = CuentaPropia("GestorCae") }, ActorCon("Administrador"), transaccion,
            _bloqueoDelRelevo, catalogo,
            new DirectorioDestinosCarteraFalso(new DestinoCartera(false, "GestorCae", coordinador, EsOperadorDelegado: false)), roles);
        return (handler, catalogo, transaccion);
    }

    private static Guid? PrincipalDe(CatalogoIncorporacionCarteraFalso catalogo) =>
        catalogo.CarterasVivas.Where(c => c.Cartera.EsPrincipal).Select(c => (Guid?)c.Cartera.UsuarioId).SingleOrDefault();

    [Fact]
    public async Task Desactivar_al_principal_pasa_la_marca_a_su_Coordinador_CAE_y_su_cartera_sigue_viva()
    {
        var (handler, catalogo, transaccion) = DesactivacionDelPrincipal(Coordinador);

        (await handler.Handle(new(Cuenta, false), default)).EsExitoso.Should().BeTrue();

        PrincipalDe(catalogo).Should().Be(Coordinador);
        catalogo.CarterasVivas.Single(c => c.Cartera.UsuarioId == Coordinador).Cartera.Rol.Should().Be("CoordinadorCae");
        catalogo.CarterasVivas.Select(c => c.Cartera.UsuarioId).Should().BeEquivalentTo(
            [Cuenta, Apoyo, Coordinador], "el desactivado pierde la marca, no la cartera (opción C), y el apoyo no se toca");
        // Apagar y relevar van con el Tenant propietario como ámbito, cada uno con su guardado.
        catalogo.CambiosDeMarca.Select(c => (c.Paso, c.TenantActivo)).Should().Equal(
            ("apagar", Propietario), ("guardar", Propietario), ("relevar", Propietario), ("guardar", Propietario));
        transaccion.Confirmadas.Should().Be(1);
        _bloqueoDelRelevo.Exclusivos.Should().Equal(Cuenta);
        _bloqueoDelRelevo.Compartidos.Should().Equal([Coordinador], "quien recibe la marca no puede estar desactivándose a la vez");
    }

    [Theory]
    [InlineData(false, true)]  // no tiene Coordinador CAE
    [InlineData(true, false)]  // su Coordinador CAE está desactivado
    public async Task Desactivar_al_principal_sin_Coordinador_CAE_util_deja_la_operacion_sin_principal(bool tieneCoordinador, bool activo)
    {
        var (handler, catalogo, _) = DesactivacionDelPrincipal(tieneCoordinador ? Coordinador : null, activo);

        (await handler.Handle(new(Cuenta, false), default)).EsExitoso.Should().BeTrue();

        PrincipalDe(catalogo).Should().BeNull();
        catalogo.CarterasVivas.Select(c => c.Cartera.UsuarioId).Should().BeEquivalentTo([Cuenta, Apoyo]);
        catalogo.CambiosDeMarca.Select(c => c.Paso).Should().Equal("apagar", "guardar");
    }

    [Fact]
    public async Task Reactivar_no_devuelve_ni_mueve_la_marca()
    {
        var (handler, catalogo, _) = DesactivacionDelPrincipal(Coordinador);

        (await handler.Handle(new(Cuenta, true), default)).EsExitoso.Should().BeTrue();

        PrincipalDe(catalogo).Should().Be(Cuenta);
        catalogo.CambiosDeMarca.Should().BeEmpty();
    }

    [Fact]
    public async Task Desactivar_a_quien_solo_tiene_cartera_de_apoyo_no_releva_a_nadie()
    {
        var (handler, catalogo, _) = DesactivacionDelPrincipal(Coordinador);
        var apoyo = new CambiarActivacionUsuarioCommandHandler(
            new GestionCuentasFalsa { [Apoyo] = CuentaPropia("GestorCae", id: Apoyo) }, ActorCon("Administrador"),
            new CaeManager.Application.Tests.Clientes.TransaccionDeComandoFalsa(), new CaeManager.Application.Tests.Clientes.BloqueoCarteraUsuarioFalso(),
            catalogo, new DirectorioDestinosCarteraFalso(new DestinoCartera(false, "GestorCae", Coordinador, EsOperadorDelegado: false)), new DirectorioRolesEnOrigen());
        _ = handler;

        (await apoyo.Handle(new(Apoyo, false), default)).EsExitoso.Should().BeTrue();

        PrincipalDe(catalogo).Should().Be(Cuenta);
        catalogo.CambiosDeMarca.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_relevo_pierde_la_carrera_la_desactivacion_no_se_confirma()
    {
        var (handler, catalogo, transaccion) = DesactivacionDelPrincipal(Coordinador);
        catalogo.PierdeLaCarrera = true;

        (await handler.Handle(new(Cuenta, false), default)).EsFallido.Should().BeTrue();

        transaccion.Confirmadas.Should().Be(0);
        transaccion.Deshechas.Should().Be(1);
    }

    [Fact]
    public async Task Una_cuenta_que_ya_no_existe_pide_recargar_la_lista()
    {
        var puerto = new GestionCuentasFalsa();

        (await new CambiarActivacionUsuarioCommandHandler(puerto, ActorCon("Administrador"), new CaeManager.Application.Tests.Clientes.TransaccionDeComandoFalsa(), new CaeManager.Application.Tests.Clientes.BloqueoCarteraUsuarioFalso(), new CatalogoIncorporacionCarteraFalso(), new DirectorioDestinosCarteraFalso(null), new DirectorioRolesEnOrigen()).Handle(new(Cuenta, false), default))
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

    // ---------- Cartera en el alta (2026-09-28) ----------

    private static readonly Guid Beneficiario1 = Guid.NewGuid();
    private static readonly Guid Beneficiario2 = Guid.NewGuid();
    private static readonly Guid BeneficiarioDeOtroOperador = Guid.NewGuid();

    /// <summary>
    /// Catálogo con los dos Tenants beneficiarios que opera el Operador CAE
    /// (<see cref="Tenant"/>) y uno que opera otro Operador CAE.
    /// </summary>
    private static CatalogoIncorporacionCarteraFalso CatalogoConOperaciones()
    {
        var catalogo = new CatalogoIncorporacionCarteraFalso();
        catalogo.RegistrarAsignable(Tenant, Operacion(Beneficiario1, Tenant), "Beneficiario Uno");
        catalogo.RegistrarAsignable(Tenant, Operacion(Beneficiario2, Tenant), "Beneficiario Dos");
        catalogo.RegistrarAsignable(OtroTenant, Operacion(BeneficiarioDeOtroOperador, OtroTenant), "Ajeno");
        return catalogo;
    }

    private static AsignacionOperacion Operacion(Guid propietario, Guid operador) =>
        AsignacionOperacion.Externa(
            propietario, operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            DateTime.UtcNow.AddDays(-30), null, DateTime.UtcNow);

    [Fact]
    public async Task El_alta_de_un_Gestor_CAE_crea_la_cuenta_y_una_cartera_por_Tenant_en_una_sola_transaccion()
    {
        var puerto = new GestionCuentasFalsa();
        var catalogo = CatalogoConOperaciones();
        var transaccion = new TransaccionDeComandoFalsa();

        var resultado = await NuevoAlta(puerto, ActorCon("Administrador"), EnSuTenant, catalogo, transaccion)
            .Handle(Alta("GestorCae", tenantsCartera: [Beneficiario1, Beneficiario2]), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        var usuarioId = resultado.Valor.UsuarioId;
        puerto.Escrituras.Should().Equal($"crear:{usuarioId}", $"rol:{usuarioId}:GestorCae");
        transaccion.Ejecutadas.Should().Be(1);
        transaccion.Confirmadas.Should().Be(1);

        // Cada cartera, con su Tenant beneficiario como Tenant activo (RLS de las
        // carteras) y sobre la operación que eligió el catálogo, no quien llama.
        catalogo.IncorporacionesDirectas.Should().BeEquivalentTo(new[]
        {
            (Beneficiario1, Tenant, catalogo.Asignables[0].Asignable.AsignacionOperacionId, usuarioId, (Guid?)Beneficiario1),
            (Beneficiario2, Tenant, catalogo.Asignables[1].Asignable.AsignacionOperacionId, usuarioId, (Guid?)Beneficiario2),
        });
        catalogo.TenantsAlGuardar.Should().Equal(Beneficiario1, Beneficiario2);
    }

    [Fact]
    public async Task Un_Tenant_que_no_opera_el_Operador_CAE_no_entra_en_la_cartera_ni_deja_cuenta()
    {
        var puerto = new GestionCuentasFalsa();
        var catalogo = CatalogoConOperaciones();
        var transaccion = new TransaccionDeComandoFalsa();

        var resultado = await NuevoAlta(puerto, ActorCon("Administrador"), EnSuTenant, catalogo, transaccion)
            .Handle(Alta("GestorCae", tenantsCartera: [Beneficiario1, BeneficiarioDeOtroOperador]), default);

        resultado.Error.Should().Be(CrearUsuarioCommandHandler.EmpresaNoAsignable);
        puerto.Escrituras.Should().BeEmpty();
        catalogo.IncorporacionesDirectas.Should().BeEmpty();
        transaccion.Ejecutadas.Should().Be(0);
    }

    [Fact]
    public async Task Si_la_operacion_deja_de_estar_vigente_a_mitad_del_alta_no_queda_ni_la_cuenta()
    {
        var puerto = new GestionCuentasFalsa();
        var catalogo = CatalogoConOperaciones();
        catalogo.AnularAlIncorporar = MotivoAnulacionSolicitudCartera.OperacionNoVigente;
        var transaccion = new TransaccionDeComandoFalsa();

        var resultado = await NuevoAlta(puerto, ActorCon("Administrador"), EnSuTenant, catalogo, transaccion)
            .Handle(Alta("GestorCae", tenantsCartera: [Beneficiario1]), default);

        resultado.Error.Should().Be(CrearUsuarioCommandHandler.EmpresaNoAsignable);
        transaccion.Deshechas.Should().Be(1, "la cuenta ya creada se deshace con la transacción");
        transaccion.Confirmadas.Should().Be(0);
        catalogo.TenantsAlGuardar.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("Consulta")]
    public async Task Un_rol_que_no_lleva_cartera_no_se_da_de_alta_con_Tenants(string rol)
    {
        var puerto = new GestionCuentasFalsa();
        var catalogo = CatalogoConOperaciones();

        var resultado = await NuevoAlta(puerto, ActorCon("Administrador"), EnSuTenant, catalogo)
            .Handle(Alta(rol, tenantsCartera: [Beneficiario1]), default);

        resultado.Error.Should().Be(CrearUsuarioCommandHandler.CarteraSoloParaGestorCae);
        puerto.Escrituras.Should().BeEmpty();
        catalogo.IncorporacionesDirectas.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Operacion_no_concede_roles_de_Propiedad_una_cuenta_de_Propiedad_no_recibe_cartera(string rol)
    {
        var puerto = new GestionCuentasFalsa();
        var catalogo = CatalogoConOperaciones();

        var resultado = await NuevoAlta(puerto, ActorCon("Administrador"), EnSuTenant, catalogo)
            .Handle(Alta(rol, tenantsCartera: [Beneficiario1]), default);

        resultado.Error.Should().Be(CrearUsuarioCommandHandler.CarteraSoloParaGestorCae);
        puerto.Escrituras.Should().BeEmpty();
        catalogo.IncorporacionesDirectas.Should().BeEmpty();
    }

    [Fact]
    public async Task Desde_el_Context_Workspace_de_otro_Tenant_el_alta_no_lleva_cartera()
    {
        var puerto = new GestionCuentasFalsa();
        var catalogo = CatalogoConOperaciones();

        // Su Tenant de origen (el Operador CAE) es OtroTenant, y la cuenta nacería en Tenant.
        var resultado = await NuevoAlta(puerto, ActorCon("Administrador", OtroTenant), EnSuTenant, catalogo)
            .Handle(Alta("GestorCae", tenantsCartera: [BeneficiarioDeOtroOperador]), default);

        resultado.Error.Should().Be(CrearUsuarioCommandHandler.CarteraSoloDesdeTuOrganizacion);
        puerto.Escrituras.Should().BeEmpty();
        catalogo.IncorporacionesDirectas.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_autoridad_sobre_cuentas_no_se_consulta_ni_se_asigna_cartera()
    {
        var puerto = new GestionCuentasFalsa();
        var catalogo = CatalogoConOperaciones();

        var resultado = await NuevoAlta(puerto, ActorCon("CoordinadorCae"), EnSuTenant, catalogo)
            .Handle(Alta("GestorCae", tenantsCartera: [Beneficiario1]), default);

        resultado.Error.Should().Be(AutoridadSobreCuentas.SinAutoridad);
        puerto.Escrituras.Should().BeEmpty();
        catalogo.IncorporacionesDirectas.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_cartera_un_fallo_al_asignar_el_rol_deshace_el_alta_entera()
    {
        var puerto = new GestionCuentasFalsa { FallarAlAsignarRol = true };
        var catalogo = CatalogoConOperaciones();
        var transaccion = new TransaccionDeComandoFalsa();

        var resultado = await NuevoAlta(puerto, ActorCon("Administrador"), EnSuTenant, catalogo, transaccion)
            .Handle(Alta("GestorCae", tenantsCartera: [Beneficiario1]), default);

        resultado.EsFallido.Should().BeTrue();
        transaccion.Deshechas.Should().Be(1);
        catalogo.IncorporacionesDirectas.Should().BeEmpty();

        // Control: sin cartera, el fallo del rol no deshace el alta (contrato anterior).
        var sinCartera = new TransaccionDeComandoFalsa();
        var alta = await NuevoAlta(new GestionCuentasFalsa { FallarAlAsignarRol = true }, ActorCon("Administrador"), EnSuTenant, catalogo, sinCartera)
            .Handle(Alta("GestorCae"), default);
        alta.EsExitoso.Should().BeTrue();
        alta.Valor.FalloAlAsignarRol.Should().NotBeNull();
        sinCartera.Confirmadas.Should().Be(1);
    }

    [Fact]
    public async Task La_lista_de_empresas_del_alta_solo_se_ofrece_a_quien_gestiona_cuentas_en_su_propio_Tenant()
    {
        var catalogo = CatalogoConOperaciones();

        (await new ObtenerEmpresasAsignablesEnAltaQueryHandler(ActorCon("Administrador"), EnSuTenant, catalogo)
                .Handle(new(), default))
            .Select(e => e.TenantId).Should().Equal(Beneficiario1, Beneficiario2);

        (await new ObtenerEmpresasAsignablesEnAltaQueryHandler(ActorCon("GestorCae"), EnSuTenant, catalogo)
                .Handle(new(), default))
            .Should().BeEmpty("sin autoridad sobre cuentas");

        (await new ObtenerEmpresasAsignablesEnAltaQueryHandler(ActorCon("Administrador", OtroTenant), EnSuTenant, catalogo)
                .Handle(new(), default))
            .Should().BeEmpty("en el Context Workspace de otro Tenant");
    }

    // ---------- Dobles ----------

    private static CrearUsuarioCommandHandler NuevoAlta(
        IGestionCuentasUsuario puerto, ICurrentUserService actor, ITenantActual tenant,
        CatalogoIncorporacionCarteraFalso? catalogo = null, TransaccionDeComandoFalsa? transaccion = null) =>
        new(puerto, actor, tenant, catalogo ?? new CatalogoIncorporacionCarteraFalso(), transaccion ?? new TransaccionDeComandoFalsa());

    private static CrearUsuarioCommand Alta(string rol, bool permiso = false, IReadOnlyCollection<Guid>? tenantsCartera = null) =>
        new("nueva@x.test", "Nueva", rol, null, null, permiso, tenantsCartera);

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

        public bool FallarAlAsignarRol { get; init; }

        public Task<Result> AsignarRolAsync(Guid usuarioId, string rol, CancellationToken cancellationToken = default)
        {
            Escrituras.Add($"rol:{usuarioId}:{rol}");
            return Task.FromResult(FallarAlAsignarRol
                ? Result.Fallo(Error.Crear("Usuarios.FalloAlAsignarRol", "rol"))
                : Result.Exito());
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
