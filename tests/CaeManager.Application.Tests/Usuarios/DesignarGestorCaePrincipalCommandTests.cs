using CaeManager.Application.Clientes;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using CaeManager.Application.Usuarios.Commands.DesignarGestorCaePrincipal;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// Designar al principal de una Asignación de Operación y el relevo automático al Coordinador
/// CAE cuando la cartera principal se retira (ADR-011 § 2.7, enmienda 2026-10-08). Las negativas
/// de autorización viven aquí; que el cambio de marca no viole el índice único bajo runtime y
/// RLS lo prueba <c>PrincipalDeCarteraBajoRuntimeTests</c> en integración.
/// </summary>
public class DesignarGestorCaePrincipalCommandTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid OtroOperador = Guid.NewGuid();
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly Guid Operacion = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid Coordinador1 = Guid.NewGuid();
    private static readonly Guid Coordinador2 = Guid.NewGuid();
    private static readonly Guid GestorA = Guid.NewGuid(); // equipo de Coordinador1, principal de partida
    private static readonly Guid GestorB = Guid.NewGuid(); // equipo de Coordinador1, apoyo
    private static readonly Guid GestorC = Guid.NewGuid(); // equipo de Coordinador2, apoyo

    private sealed class DirectorioPorUsuario : IDirectorioDestinosCartera
    {
        public Dictionary<Guid, DestinoCartera> Cuentas { get; } = [];

        public Task<DestinoCartera?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Cuentas.GetValueOrDefault(usuarioId));
    }

    private sealed class Escenario
    {
        public CatalogoIncorporacionCarteraFalso Catalogo { get; } = new();
        public TransaccionDeComandoFalsa Transaccion { get; } = new();
        public BloqueoCarteraUsuarioFalso Bloqueo { get; } = new();
        public DirectorioRolesEnOrigen Roles { get; } = new();
        public DirectorioPorUsuario Cuentas { get; } = new();
        public Guid ActorId { get; init; } = Actor;
        public Guid TenantOrigen { get; init; } = Operador;
        public string? RolDeSesion { get; init; } = "Administrador";

        public Escenario()
        {
            Cuenta(GestorA, "GestorCae", Coordinador1);
            Cuenta(GestorB, "GestorCae", Coordinador1);
            Cuenta(GestorC, "GestorCae", Coordinador2);
            Cuenta(Coordinador1, "CoordinadorCae", null);
            Cuenta(Coordinador2, "CoordinadorCae", null);
            Cartera(GestorA, principal: true);
            Cartera(GestorB);
            Cartera(GestorC);
        }

        public void Cuenta(Guid usuarioId, string rol, Guid? coordinador, bool activa = true)
        {
            Cuentas.Cuentas[usuarioId] = new DestinoCartera(activa, rol, coordinador, EsOperadorDelegado: false);
            Roles.Asignar(usuarioId, Operador, rol);
            if (!activa) Roles.Desactivar(usuarioId);
        }

        public void Cartera(Guid usuarioId, bool principal = false, string rol = "GestorCae") =>
            Catalogo.CarterasVivas.Add((Operador, new CarteraVivaDeOperacion(
                Operacion, Propietario, "Talleres Norte", usuarioId, rol, principal, null)));

        public Escenario ActorConRol(string rol)
        {
            Roles.Asignar(ActorId, TenantOrigen, rol);
            return this;
        }

        public Guid? Principal => Catalogo.CarterasVivas
            .Where(c => c.Cartera.AsignacionOperacionId == Operacion && c.Cartera.EsPrincipal)
            .Select(c => (Guid?)c.Cartera.UsuarioId)
            .SingleOrDefault();

        public IEnumerable<Guid> ConCartera => Catalogo.CarterasVivas.Select(c => c.Cartera.UsuarioId);

        public DesignarGestorCaePrincipalCommandHandler Handler() => new(
            new CurrentUserServicePorAmbito(ActorId, TenantOrigen, RolDeSesion), Roles, Cuentas, Catalogo, Transaccion, Bloqueo);

        public Task<Domain.Common.Result> Designar(Guid usuarioId, Guid? operacion = null) =>
            Handler().Handle(new DesignarGestorCaePrincipalCommand(operacion ?? Operacion, usuarioId), default);

        public void NadaCambio()
        {
            Principal.Should().Be(GestorA);
            Catalogo.CambiosDeMarca.Should().BeEmpty();
        }
    }

    // ---------- Quién puede ----------

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Administrador_y_Direccion_CAE_pasan_la_marca_apagando_guardando_y_encendiendo_en_el_Tenant_propietario(string rol)
    {
        var e = new Escenario().ActorConRol(rol);

        var resultado = await e.Designar(GestorC);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        e.Principal.Should().Be(GestorC);
        e.ConCartera.Should().BeEquivalentTo([GestorA, GestorB, GestorC], "quien pierde la marca conserva su cartera, ahora de apoyo");
        // El índice único de principal no es diferible: apagar, guardar, encender, guardar.
        e.Catalogo.CambiosDeMarca.Select(c => (c.Paso, c.UsuarioId, c.TenantActivo)).Should().Equal(
            ("apagar", GestorA, Propietario),
            ("guardar", Guid.Empty, Propietario),
            ("encender", GestorC, Propietario),
            ("guardar", Guid.Empty, Propietario));
        e.Transaccion.Confirmadas.Should().Be(1);
        e.Bloqueo.Compartidos.Should().Equal(GestorC);
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public async Task Un_Gestor_CAE_o_un_rol_sin_gestion_no_designa_principal(string rol)
    {
        var e = new Escenario { RolDeSesion = rol }.ActorConRol(rol);

        var resultado = await e.Designar(GestorB);

        resultado.Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.SinAutoridad);
        e.NadaCambio();
        e.Transaccion.Ejecutadas.Should().Be(0);
    }

    [Fact]
    public async Task Un_Gestor_CAE_no_se_designa_a_si_mismo_aunque_tenga_cartera_de_apoyo()
    {
        var e = new Escenario { ActorId = GestorB, RolDeSesion = "GestorCae" };

        (await e.Designar(GestorB)).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.SinAutoridad);
        e.NadaCambio();
    }

    [Fact]
    public async Task El_rol_se_lee_de_Identity_en_el_Tenant_de_origen_y_no_del_claim_de_la_sesion()
    {
        // El claim dice Administrador (por ejemplo, el de un Workspace operativo derivado o una
        // sesión anterior a un cambio de rol); en Identity, sobre su Tenant de origen, es Gestor CAE.
        var degradado = new Escenario { RolDeSesion = "Administrador" }.ActorConRol("GestorCae");
        (await degradado.Designar(GestorB)).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.SinAutoridad);
        degradado.NadaCambio();

        // Y al revés: el claim dice Gestor CAE y en Identity es Dirección CAE.
        var real = new Escenario { RolDeSesion = "GestorCae" }.ActorConRol("DireccionCae");
        (await real.Designar(GestorB)).EsExitoso.Should().BeTrue();
        real.Principal.Should().Be(GestorB);
    }

    [Fact]
    public async Task Una_cuenta_desactivada_no_designa_aunque_su_sesion_siga_viva()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Roles.Desactivar(Actor);

        (await e.Designar(GestorB)).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.SinAutoridad);
        e.NadaCambio();
    }

    [Fact]
    public async Task Sin_rol_de_negocio_en_la_sesion_no_se_designa()
    {
        var e = new Escenario { RolDeSesion = null }.ActorConRol("Administrador");

        (await e.Designar(GestorB)).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.SinAutoridad);
        e.NadaCambio();
    }

    [Fact]
    public async Task El_Administrador_de_otro_Operador_CAE_no_ve_esa_operacion_ni_cambia_su_principal()
    {
        var e = new Escenario { TenantOrigen = OtroOperador }.ActorConRol("Administrador");

        (await e.Designar(GestorB)).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.DestinoSinCartera);
        e.NadaCambio();
    }

    // ---------- Coordinador CAE ----------

    [Fact]
    public async Task Un_Coordinador_CAE_designa_dentro_de_su_equipo()
    {
        var e = new Escenario { ActorId = Coordinador1, RolDeSesion = "CoordinadorCae" };

        (await e.Designar(GestorB)).EsExitoso.Should().BeTrue();
        e.Principal.Should().Be(GestorB);
    }

    [Fact]
    public async Task Un_Coordinador_CAE_ajeno_no_designa_a_quien_no_le_reporta()
    {
        var e = new Escenario { ActorId = Coordinador2, RolDeSesion = "CoordinadorCae" };

        (await e.Designar(GestorB)).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.FueraDeTuEquipo);
        e.NadaCambio();
    }

    [Fact]
    public async Task Un_Coordinador_CAE_no_le_quita_la_marca_al_equipo_de_otro_aunque_el_destino_sea_suyo()
    {
        // GestorC le reporta a Coordinador2, pero el principal actual (GestorA) es del equipo de Coordinador1.
        var e = new Escenario { ActorId = Coordinador2, RolDeSesion = "CoordinadorCae" };

        (await e.Designar(GestorC)).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.FueraDeTuEquipo);
        e.NadaCambio();
    }

    [Fact]
    public async Task El_Coordinador_CAE_que_es_el_principal_actual_designa_a_cualquiera_con_cartera()
    {
        var e = new Escenario { ActorId = Coordinador2, RolDeSesion = "CoordinadorCae" };
        e.Catalogo.CarterasVivas.Clear();
        e.Cartera(Coordinador2, principal: true, rol: "CoordinadorCae");
        e.Cartera(GestorB); // no le reporta

        (await e.Designar(GestorB)).EsExitoso.Should().BeTrue();
        e.Principal.Should().Be(GestorB);
        e.ConCartera.Should().Contain(Coordinador2, "su cartera sigue viva, sin la marca");
    }

    [Fact]
    public async Task Un_Coordinador_CAE_puede_ser_el_destino_con_su_cartera_propia()
    {
        var e = new Escenario().ActorConRol("DireccionCae");
        e.Cartera(Coordinador1, rol: "CoordinadorCae");

        (await e.Designar(Coordinador1)).EsExitoso.Should().BeTrue();
        e.Principal.Should().Be(Coordinador1);
    }

    // ---------- Destino ----------

    [Fact]
    public async Task El_destino_necesita_cartera_viva_bajo_esa_operacion()
    {
        var e = new Escenario().ActorConRol("Administrador");
        var sinCartera = Guid.NewGuid();
        e.Cuenta(sinCartera, "GestorCae", Coordinador1);

        (await e.Designar(sinCartera)).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.DestinoSinCartera);
        (await e.Designar(GestorB, operacion: Guid.NewGuid())).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.DestinoSinCartera);
        e.NadaCambio();
    }

    [Fact]
    public async Task Una_cuenta_desactivada_no_puede_ser_el_principal()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Cuenta(GestorB, "GestorCae", Coordinador1, activa: false);

        (await e.Designar(GestorB)).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.DestinoDesactivado);
        e.NadaCambio();
    }

    [Fact]
    public async Task Designar_a_quien_ya_es_el_principal_no_escribe_nada()
    {
        var e = new Escenario().ActorConRol("Administrador");

        (await e.Designar(GestorA)).EsExitoso.Should().BeTrue();
        e.NadaCambio();
        e.Transaccion.Ejecutadas.Should().Be(0);
    }

    [Fact]
    public async Task Sin_principal_previo_solo_se_enciende_la_marca()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Catalogo.CarterasVivas.Clear();
        e.Cartera(GestorA);
        e.Cartera(GestorB);

        (await e.Designar(GestorB)).EsExitoso.Should().BeTrue();
        e.Principal.Should().Be(GestorB);
        e.Catalogo.CambiosDeMarca.Select(c => c.Paso).Should().Equal("encender", "guardar");
    }

    [Fact]
    public async Task Si_el_guardado_pierde_la_carrera_el_comando_falla_y_la_transaccion_se_deshace()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Catalogo.PierdeLaCarrera = true;

        (await e.Designar(GestorB)).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.CambioMientrasDecidias);
        e.Transaccion.Deshechas.Should().Be(1);
        e.Transaccion.Confirmadas.Should().Be(0);
    }

    // ---------- Relevo automático al retirar la cartera principal ----------

    private static AsignarCarteraGestorCaeCommandHandler Retirada(Escenario e) => new(
        new CurrentUserServicePorAmbito(e.ActorId, e.TenantOrigen, e.RolDeSesion), e.Roles, e.Cuentas, e.Catalogo, e.Transaccion, e.Bloqueo);

    private static async Task RetirarAsync(Escenario e, Guid gestor)
    {
        e.Catalogo.CarterasUniversales.Add((Operador, gestor, new TenantEnCarteraDeGestor(Propietario, "Talleres Norte")));
        var resultado = await Retirada(e).Handle(new AsignarCarteraGestorCaeCommand(gestor, null, [Propietario]), default);
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
    }

    [Fact]
    public async Task Retirar_la_cartera_principal_pasa_la_marca_al_Coordinador_CAE_de_esa_persona_emitiendole_cartera()
    {
        var e = new Escenario().ActorConRol("Administrador");

        await RetirarAsync(e, GestorA);

        e.Principal.Should().Be(Coordinador1);
        var emitida = e.Catalogo.CarterasVivas.Single(c => c.Cartera.UsuarioId == Coordinador1).Cartera;
        emitida.Rol.Should().Be("CoordinadorCae");
        emitida.PropietarioTenantId.Should().Be(Propietario);
        e.ConCartera.Should().BeEquivalentTo([GestorB, GestorC, Coordinador1], "las carteras de apoyo no se tocan");
        // El relevo va después del guardado del cierre y con el Tenant propietario como ámbito.
        e.Catalogo.CambiosDeMarca.Select(c => (c.Paso, c.TenantActivo)).Should().Equal(
            ("guardar", Propietario), ("relevar", Propietario), ("guardar", Propietario));
        e.Transaccion.Confirmadas.Should().Be(1);
    }

    [Fact]
    public async Task Si_el_Coordinador_CAE_ya_tiene_cartera_bajo_la_operacion_se_marca_la_suya_y_no_se_emite_otra()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Cartera(Coordinador1, rol: "CoordinadorCae");

        await RetirarAsync(e, GestorA);

        e.Principal.Should().Be(Coordinador1);
        e.Catalogo.CarterasVivas.Count(c => c.Cartera.UsuarioId == Coordinador1).Should().Be(1);
    }

    [Fact]
    public async Task Sin_Coordinador_CAE_la_operacion_queda_sin_principal()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Cuenta(GestorA, "GestorCae", coordinador: null);

        await RetirarAsync(e, GestorA);

        e.Principal.Should().BeNull();
        e.Catalogo.CambiosDeMarca.Select(c => c.Paso).Should().NotContain("relevar");
        e.ConCartera.Should().BeEquivalentTo([GestorB, GestorC], "nadie hereda la marca ni recibe cartera");
    }

    [Theory]
    [InlineData(false, "CoordinadorCae")] // desactivado
    [InlineData(true, "GestorCae")]       // ya no es Coordinador CAE en Identity
    public async Task Un_Coordinador_CAE_desactivado_o_que_ya_no_lo_es_no_recibe_el_relevo(bool activa, string rol)
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Cuenta(Coordinador1, rol, null, activa);

        await RetirarAsync(e, GestorA);

        e.Principal.Should().BeNull();
        e.ConCartera.Should().NotContain(Coordinador1);
    }

    [Fact]
    public async Task Retirar_una_cartera_de_apoyo_no_releva_a_nadie()
    {
        var e = new Escenario().ActorConRol("Administrador");

        await RetirarAsync(e, GestorB);

        e.Principal.Should().Be(GestorA);
        e.Catalogo.CambiosDeMarca.Select(c => c.Paso).Should().NotContain("relevar");
        e.ConCartera.Should().NotContain(Coordinador1);
    }

    // ---------- Lectura ----------

    [Fact]
    public async Task La_lectura_da_por_operacion_quien_es_el_principal_y_quienes_de_apoyo()
    {
        var e = new Escenario { ActorId = GestorB, RolDeSesion = "GestorCae" };
        e.Catalogo.CarterasVivas.Add((Operador, new CarteraVivaDeOperacion(
            Operacion, Propietario, "Talleres Norte", Coordinador2, "GestorCae", false, new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc))));

        var operaciones = await new ObtenerPersonasConCarteraQueryHandler(
                new CurrentUserServicePorAmbito(e.ActorId, e.TenantOrigen, e.RolDeSesion), e.Roles, e.Catalogo)
            .Handle(new ObtenerPersonasConCarteraQuery(Propietario), default);

        var operacion = operaciones.Should().ContainSingle().Subject;
        operacion.AsignacionOperacionId.Should().Be(Operacion);
        operacion.TenantId.Should().Be(Propietario);
        operacion.Principal!.UsuarioId.Should().Be(GestorA);
        operacion.Principal.Rol.Should().Be("GestorCae");
        operacion.Apoyos.Select(a => a.UsuarioId).Should().BeEquivalentTo([GestorB, GestorC, Coordinador2]);
        operacion.Apoyos.Single(a => a.UsuarioId == Coordinador2).VigenciaHasta.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Consulta", true)]
    [InlineData("Cliente", true)]
    [InlineData("Administrador", false)] // de otro Operador CAE
    public async Task La_lectura_sale_vacia_para_quien_no_gestiona_o_no_es_de_ese_Operador_CAE(string rol, bool mismoOperador)
    {
        var e = new Escenario { TenantOrigen = mismoOperador ? Operador : OtroOperador, RolDeSesion = rol }.ActorConRol(rol);

        var operaciones = await new ObtenerPersonasConCarteraQueryHandler(
                new CurrentUserServicePorAmbito(e.ActorId, e.TenantOrigen, e.RolDeSesion), e.Roles, e.Catalogo)
            .Handle(new ObtenerPersonasConCarteraQuery(), default);

        operaciones.Should().BeEmpty();
    }
}
