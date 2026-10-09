using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Application.Usuarios.Commands.AsumirPrincipalDeOperacion;
using CaeManager.Application.Usuarios.Queries.ObtenerOperacionesSinPrincipal;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// Alerta «sin principal», escalado y «Asumir» (ADR-011 § 2.7, enmienda 2026-10-08, punto 4).
/// Aquí viven la matriz del escalado y las negativas de autorización; que la cartera emitida
/// lleve rol Coordinador CAE y el Tenant propietario como ámbito, y que dos «Asumir» a la vez
/// dejen un solo principal, lo prueba <c>PrincipalSinRelevoBajoRuntimeTests</c> en integración.
/// </summary>
public class PrincipalSinRelevoTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid OtroOperador = Guid.NewGuid();
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly Guid Operacion = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid Apoyo = Guid.NewGuid();

    private sealed class Escenario
    {
        public CatalogoIncorporacionCarteraFalso Catalogo { get; } = new();
        public TransaccionDeComandoFalsa Transaccion { get; } = new();
        public BloqueoCarteraUsuarioFalso Bloqueo { get; } = new();
        public DirectorioRolesEnOrigen Roles { get; } = new();
        public Guid? TenantOrigen { get; init; } = Operador;
        public string? RolDeSesion { get; init; } = "Consulta";

        public Escenario()
        {
            Catalogo.Asignables.Add((Operador, new TenantCandidatoIncorporacion(Propietario, "Talleres Norte", Operacion)));
        }

        public Guid Cuenta(string rol, bool activa = true, Guid? tenant = null, Guid? id = null)
        {
            var usuarioId = id ?? Guid.NewGuid();
            Roles.Asignar(usuarioId, tenant ?? Operador, rol);
            if (!activa) Roles.Desactivar(usuarioId);
            return usuarioId;
        }

        public void Cartera(Guid usuarioId, bool principal = false, string rol = "GestorCae", Guid? operacion = null) =>
            Catalogo.CarterasVivas.Add((Operador, new CarteraVivaDeOperacion(
                operacion ?? Operacion, Propietario, "Talleres Norte", usuarioId, rol, principal, null)));

        public Guid? Principal(Guid? operacion = null) => Catalogo.CarterasVivas
            .Where(c => c.Cartera.AsignacionOperacionId == (operacion ?? Operacion) && c.Cartera.EsPrincipal)
            .Select(c => (Guid?)c.Cartera.UsuarioId)
            .SingleOrDefault();

        public CurrentUserServicePorAmbito Sesion => new(Actor, TenantOrigen, RolDeSesion, RolDeSesion);

        public Task<Domain.Common.Result> AsumirAsync(Guid? operacion = null) =>
            new AsumirPrincipalDeOperacionCommandHandler(Sesion, Roles, Catalogo, Transaccion, Bloqueo)
                .Handle(new AsumirPrincipalDeOperacionCommand(operacion ?? Operacion), default);

        public Task<AlertaDePrincipal> AlertaAsync() =>
            new ObtenerOperacionesSinPrincipalQueryHandler(Sesion, Roles, Catalogo)
                .Handle(new ObtenerOperacionesSinPrincipalQuery(), default);

        public Task<bool> EscalarAsync(Guid? excluido = null) =>
            EscaladoDePrincipalDeCartera.AsignarAlUnicoAsync(
                Catalogo, Roles, Bloqueo, [new OperacionConPrincipal(Propietario, Operacion)], Operador, excluido, default);
    }

    // ---------- Escalado (D-7) ----------

    [Theory]
    [InlineData(1, 0, 0, "CoordinadorCae")] // un Coordinador CAE
    [InlineData(1, 2, 2, "CoordinadorCae")] // un Coordinador CAE, aunque haya varios por encima
    [InlineData(0, 1, 0, "DireccionCae")]   // ninguno y una Dirección CAE
    [InlineData(0, 1, 3, "DireccionCae")]
    [InlineData(0, 0, 1, "Administrador")]  // ninguno y un Administrador
    public async Task Con_una_sola_persona_en_el_primer_perfil_con_alguien_la_recibe_ella(
        int coordinadores, int direcciones, int administradores, string perfilQueRecibe)
    {
        var e = new Escenario();
        var porPerfil = new Dictionary<string, List<Guid>>
        {
            ["CoordinadorCae"] = Enumerable.Range(0, coordinadores).Select(_ => e.Cuenta("CoordinadorCae")).ToList(),
            ["DireccionCae"] = Enumerable.Range(0, direcciones).Select(_ => e.Cuenta("DireccionCae")).ToList(),
            ["Administrador"] = Enumerable.Range(0, administradores).Select(_ => e.Cuenta("Administrador")).ToList(),
        };
        e.Cuenta("GestorCae");

        (await e.EscalarAsync()).Should().BeTrue();

        var elegida = porPerfil[perfilQueRecibe].Single();
        e.Principal().Should().Be(elegida);
        var cartera = e.Catalogo.CarterasVivas.Single().Cartera;
        cartera.Rol.Should().Be("CoordinadorCae", "la Operación nunca concede roles de Propiedad, sea cual sea el perfil de quien recibe");
        e.Catalogo.CambiosDeMarca.Single(c => c.Paso == "relevar").TenantActivo.Should().Be(Propietario,
            "la cartera se escribe con el Tenant propietario como ámbito");
        e.Bloqueo.Compartidos.Should().Equal([elegida], "quien recibe no puede estar desactivándose a la vez");
    }

    [Theory]
    [InlineData(2, 0, 0)] // varios Coordinadores CAE
    [InlineData(2, 1, 1)] // varios en el primer perfil: no se baja al siguiente aunque ahí haya uno solo
    [InlineData(0, 3, 1)]
    [InlineData(0, 0, 2)]
    [InlineData(0, 0, 0)] // nadie
    public async Task Con_varias_personas_en_ese_perfil_o_sin_nadie_no_se_asigna_a_nadie(
        int coordinadores, int direcciones, int administradores)
    {
        var e = new Escenario();
        for (var i = 0; i < coordinadores; i++) e.Cuenta("CoordinadorCae");
        for (var i = 0; i < direcciones; i++) e.Cuenta("DireccionCae");
        for (var i = 0; i < administradores; i++) e.Cuenta("Administrador");
        e.Cuenta("GestorCae");

        (await e.EscalarAsync()).Should().BeTrue();

        e.Principal().Should().BeNull();
        e.Catalogo.CarterasVivas.Should().BeEmpty("la operación queda en la alerta; nadie recibe cartera");
        e.Catalogo.CambiosDeMarca.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Gestor_CAE_nunca_recibe_el_principal_por_escalado()
    {
        var e = new Escenario();
        e.Cuenta("GestorCae");

        await e.EscalarAsync();

        e.Catalogo.CarterasVivas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_cuenta_desactivada_o_de_otro_Operador_CAE_no_cuenta_en_su_perfil()
    {
        var e = new Escenario();
        e.Cuenta("CoordinadorCae", activa: false);
        e.Cuenta("CoordinadorCae", tenant: OtroOperador);
        var direccion = e.Cuenta("DireccionCae");

        await e.EscalarAsync();

        e.Principal().Should().Be(direccion, "sin Coordinador CAE activo propio se sube a Dirección CAE");
    }

    [Fact]
    public async Task Quien_suelta_la_marca_no_la_recibe_de_vuelta()
    {
        var e = new Escenario();
        var saliente = e.Cuenta("CoordinadorCae");
        var administrador = e.Cuenta("Administrador");

        await e.EscalarAsync(excluido: saliente);

        e.Principal().Should().Be(administrador);
    }

    [Fact]
    public async Task Si_el_guardado_pierde_la_carrera_el_escalado_lo_dice_para_que_el_comando_falle_entero()
    {
        var e = new Escenario();
        e.Cuenta("CoordinadorCae");
        e.Catalogo.PierdeLaCarrera = true;

        (await e.EscalarAsync()).Should().BeFalse();
    }

    // ---------- Primer usuario elegible ----------

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("DireccionCae")]
    [InlineData("Administrador")]
    public async Task El_primer_usuario_elegible_recibe_las_operaciones_que_seguian_sin_principal(string rol)
    {
        var e = new Escenario();
        var conPrincipal = Guid.NewGuid();
        e.Catalogo.Asignables.Add((Operador, new TenantCandidatoIncorporacion(Guid.NewGuid(), "Obras Sur", conPrincipal)));
        var gestor = e.Cuenta("GestorCae");
        e.Cartera(gestor, principal: true, operacion: conPrincipal);
        var nueva = e.Cuenta(rol);

        (await EscaladoDePrincipalDeCartera.AsignarAlPrimerElegibleAsync(
            e.Catalogo, e.Roles, e.Bloqueo, nueva, Operador, default)).Should().BeTrue();

        e.Principal().Should().Be(nueva);
        e.Principal(conPrincipal).Should().Be(gestor, "una operación con principal no se toca");
        e.Catalogo.CarterasVivas.Single(c => c.Cartera.UsuarioId == nueva).Cartera.Rol.Should().Be("CoordinadorCae");
    }

    [Fact]
    public async Task Un_Gestor_CAE_no_es_el_primer_usuario_elegible()
    {
        var e = new Escenario();
        var gestor = e.Cuenta("GestorCae");

        await EscaladoDePrincipalDeCartera.AsignarAlPrimerElegibleAsync(e.Catalogo, e.Roles, e.Bloqueo, gestor, Operador, default);

        e.Catalogo.CarterasVivas.Should().BeEmpty();
    }

    [Fact]
    public async Task Quien_llega_cuando_ya_habia_otro_elegible_no_recibe_nada_solo()
    {
        var e = new Escenario();
        e.Cuenta("Administrador");
        var segunda = e.Cuenta("CoordinadorCae");

        await EscaladoDePrincipalDeCartera.AsignarAlPrimerElegibleAsync(e.Catalogo, e.Roles, e.Bloqueo, segunda, Operador, default);

        e.Catalogo.CarterasVivas.Should().BeEmpty("con más de un elegible decide una persona con «Asumir»");
    }

    // ---------- «Asumir» ----------

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("DireccionCae")]
    [InlineData("Administrador")]
    public async Task Coordinador_CAE_Direccion_CAE_y_Administrador_asumen_en_el_acto(string rol)
    {
        var e = new Escenario();
        e.Cuenta(rol, id: Actor);
        e.Cuenta(rol); // hay más gente en su perfil: por eso no se asignó sola
        e.Cartera(Apoyo);

        var resultado = await e.AsumirAsync();

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        e.Principal().Should().Be(Actor);
        e.Catalogo.CarterasVivas.Single(c => c.Cartera.UsuarioId == Actor).Cartera.Rol.Should().Be("CoordinadorCae");
        e.Catalogo.CarterasVivas.Single(c => c.Cartera.UsuarioId == Apoyo).Cartera.EsPrincipal.Should().BeFalse("las carteras de apoyo no se tocan");
        e.Catalogo.CambiosDeMarca.Single(c => c.Paso == "relevar").TenantActivo.Should().Be(Propietario);
        e.Transaccion.Confirmadas.Should().Be(1);
        e.Bloqueo.Compartidos.Should().Contain(Actor);
    }

    [Fact]
    public async Task Quien_ya_tiene_cartera_viva_en_la_operacion_no_recibe_otra_se_le_marca()
    {
        var e = new Escenario();
        e.Cuenta("CoordinadorCae", id: Actor);
        e.Cartera(Actor, rol: "CoordinadorCae");

        (await e.AsumirAsync()).EsExitoso.Should().BeTrue();

        e.Principal().Should().Be(Actor);
        e.Catalogo.CarterasVivas.Count(c => c.Cartera.UsuarioId == Actor).Should().Be(1);
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task Un_Gestor_CAE_no_puede_asumir(string rol)
    {
        var e = new Escenario();
        e.Cuenta(rol, id: Actor);

        (await e.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.SinAutoridad);

        e.Catalogo.CarterasVivas.Should().BeEmpty();
        e.Transaccion.Ejecutadas.Should().Be(0);
    }

    [Fact]
    public async Task El_perfil_se_lee_en_Identity_sobre_el_Tenant_de_origen_no_en_el_claim()
    {
        // El claim de la sesión dice Coordinador CAE; en Identity ya es Gestor CAE.
        var degradado = new Escenario { RolDeSesion = "CoordinadorCae" };
        degradado.Cuenta("GestorCae", id: Actor);
        (await degradado.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.SinAutoridad);

        // Y al revés: dentro de un Workspace operativo derivado el claim es el de la cartera.
        var enOtroTenant = new Escenario { RolDeSesion = "GestorCae" };
        enOtroTenant.Cuenta("Administrador", id: Actor);
        (await enOtroTenant.AsumirAsync()).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Una_cuenta_desactivada_no_asume()
    {
        var e = new Escenario();
        e.Cuenta("CoordinadorCae", activa: false, id: Actor);

        (await e.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.SinAutoridad);
    }

    [Fact]
    public async Task Sin_rol_de_negocio_en_la_sesion_no_se_asume()
    {
        // Sesión Privilegiada de Soporte TALVEG: nunca es Operador CAE ni Gestor CAE.
        var e = new Escenario { RolDeSesion = null };
        e.Cuenta("Administrador", id: Actor);

        (await e.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.SinAutoridad);
    }

    [Fact]
    public async Task La_operacion_de_otro_Operador_CAE_no_existe_para_quien_asume()
    {
        var e = new Escenario();
        e.Cuenta("Administrador", id: Actor);
        var ajena = Guid.NewGuid();
        e.Catalogo.Asignables.Add((OtroOperador, new TenantCandidatoIncorporacion(Guid.NewGuid(), "Ajena", ajena)));

        (await e.AsumirAsync(ajena)).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.OperacionNoEncontrada);

        e.Catalogo.CambiosDeMarca.Should().BeEmpty();
    }

    [Fact]
    public async Task El_Administrador_de_otro_Operador_CAE_no_asume_esta_operacion()
    {
        var e = new Escenario { TenantOrigen = OtroOperador };
        e.Cuenta("Administrador", tenant: OtroOperador, id: Actor);

        (await e.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.OperacionNoEncontrada);

        e.Catalogo.CarterasVivas.Should().BeEmpty();
    }

    [Fact]
    public async Task Asumir_no_sirve_para_quitarle_el_principal_a_otro()
    {
        var e = new Escenario();
        e.Cuenta("Administrador", id: Actor);
        e.Cartera(Apoyo, principal: true);

        (await e.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.YaTienePrincipal);

        e.Principal().Should().Be(Apoyo);
        e.Catalogo.CambiosDeMarca.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_la_escritura_no_puede_darle_la_cartera_Asumir_falla_y_no_guarda_nada()
    {
        var e = new Escenario();
        e.Cuenta("DireccionCae", id: Actor);
        e.Catalogo.RelevoImposible = true;

        (await e.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.NoSePudoAsignar);

        e.Principal().Should().BeNull();
        e.Transaccion.Deshechas.Should().Be(1);
        e.Transaccion.Confirmadas.Should().Be(0);
    }

    [Fact]
    public async Task Si_otro_gana_la_carrera_quien_pierde_falla_sin_dejar_nada()
    {
        var e = new Escenario();
        e.Cuenta("CoordinadorCae", id: Actor);
        e.Catalogo.PierdeLaCarrera = true;

        (await e.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.CambioMientrasDecidias);

        e.Transaccion.Deshechas.Should().Be(1);
        e.Transaccion.Confirmadas.Should().Be(0);
    }

    // ---------- Alerta ----------

    [Theory]
    [InlineData("CoordinadorCae", true)]
    [InlineData("DireccionCae", true)]
    [InlineData("Administrador", true)]
    [InlineData("GestorCae", false)]
    [InlineData("Consulta", false)]
    public async Task La_alerta_la_ven_Coordinador_CAE_Direccion_CAE_y_Administrador(string rol, bool laVe)
    {
        // El claim dice lo contrario que Identity: manda Identity.
        var e = new Escenario { RolDeSesion = laVe ? "GestorCae" : "Administrador" };
        e.Cuenta(rol, id: Actor);

        var alerta = await e.AlertaAsync();

        alerta.LaVe.Should().Be(laVe);
        alerta.Operaciones.Should().HaveCount(laVe ? 1 : 0);
    }

    [Fact]
    public async Task La_alerta_distingue_sin_nadie_con_personas_sin_principal_y_con_Coordinador_CAE_principal()
    {
        var e = new Escenario();
        e.Cuenta("DireccionCae", id: Actor);
        var conApoyo = Guid.NewGuid();
        var conCoordinador = Guid.NewGuid();
        var conGestor = Guid.NewGuid();
        var ajena = Guid.NewGuid();
        e.Catalogo.Asignables.Add((Operador, new TenantCandidatoIncorporacion(Guid.NewGuid(), "B con apoyo", conApoyo)));
        e.Catalogo.Asignables.Add((Operador, new TenantCandidatoIncorporacion(Guid.NewGuid(), "C con coordinador", conCoordinador)));
        e.Catalogo.Asignables.Add((Operador, new TenantCandidatoIncorporacion(Guid.NewGuid(), "D con gestor", conGestor)));
        e.Catalogo.Asignables.Add((OtroOperador, new TenantCandidatoIncorporacion(Guid.NewGuid(), "Ajena", ajena)));
        var coordinador = e.Cuenta("CoordinadorCae");
        e.Cartera(Apoyo, operacion: conApoyo);
        e.Cartera(Guid.NewGuid(), operacion: conApoyo);
        e.Cartera(coordinador, principal: true, rol: "CoordinadorCae", operacion: conCoordinador);
        e.Cartera(Apoyo, principal: true, operacion: conGestor);

        var alerta = await e.AlertaAsync();

        alerta.Operaciones.Select(o => (o.AsignacionOperacionId, o.Situacion, o.PersonasAsignadas, o.SePuedeAsumir))
            .Should().BeEquivalentTo(new[]
            {
                (Operacion, SituacionDePrincipal.SinNadieAsignado, 0, true),
                (conApoyo, SituacionDePrincipal.ConPersonasSinPrincipal, 2, true),
                (conCoordinador, SituacionDePrincipal.CoordinadorCaePrincipal, 1, false),
            }, "ni la operación con un Gestor CAE principal ni la de otro Operador CAE están en la alerta");
        alerta.Operaciones.Single(o => o.AsignacionOperacionId == conCoordinador).NombrePrincipal.Should().NotBeNullOrEmpty();
        alerta.Operaciones.Should().OnlyContain(o => !o.EsDeQuienConsulta);
    }

    [Fact]
    public async Task Sin_rol_de_negocio_en_la_sesion_no_hay_alerta()
    {
        var e = new Escenario { RolDeSesion = null };
        e.Cuenta("Administrador", id: Actor);

        (await e.AlertaAsync()).Should().Be(AlertaDePrincipal.Ninguna);
    }
}
