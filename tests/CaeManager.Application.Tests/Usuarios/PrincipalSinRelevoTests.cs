using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
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
        public OutboundCerradoTenantTecFijo Outbound { get; init; } = OutboundCerradoTenantTecFijo.Abierto();

        // Sin fijarlo, la sesión de quien actúa tiene el rol que su cuenta tiene en Identity
        // (lo normal: el rol con el que abrió sesión); fijarlo simula una sesión que diverge.
        private string? _rolDeSesion = "Consulta";
        private bool _rolDeSesionFijado;

        public string? RolDeSesion
        {
            get => _rolDeSesion;
            init { _rolDeSesion = value; _rolDeSesionFijado = true; }
        }

        public Escenario()
        {
            Catalogo.Asignables.Add((Operador, new TenantCandidatoIncorporacion(Propietario, "Talleres Norte", Operacion)));
        }

        public Guid Cuenta(string rol, bool activa = true, Guid? tenant = null, Guid? id = null)
        {
            var usuarioId = id ?? Guid.NewGuid();
            Roles.Asignar(usuarioId, tenant ?? Operador, rol);
            if (!activa) Roles.Desactivar(usuarioId);
            if (usuarioId == Actor && !_rolDeSesionFijado) _rolDeSesion = rol;
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
            new AsumirPrincipalDeOperacionCommandHandler(Sesion, Roles, Catalogo, Transaccion, Bloqueo, Outbound)
                .Handle(new AsumirPrincipalDeOperacionCommand(operacion ?? Operacion), default);

        public Task<AlertaDePrincipal> AlertaAsync() =>
            new ObtenerOperacionesSinPrincipalQueryHandler(Sesion, Roles, Catalogo, Outbound)
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
            e.Catalogo, e.Roles, e.Bloqueo, e.Outbound, nueva, Operador, default)).Should().BeTrue();

        e.Principal().Should().Be(nueva);
        e.Principal(conPrincipal).Should().Be(gestor, "una operación con principal no se toca");
        e.Catalogo.CarterasVivas.Single(c => c.Cartera.UsuarioId == nueva).Cartera.Rol.Should().Be("CoordinadorCae");
    }

    [Fact]
    public async Task Un_Gestor_CAE_no_es_el_primer_usuario_elegible()
    {
        var e = new Escenario();
        var gestor = e.Cuenta("GestorCae");

        await EscaladoDePrincipalDeCartera.AsignarAlPrimerElegibleAsync(e.Catalogo, e.Roles, e.Bloqueo, e.Outbound, gestor, Operador, default);

        e.Catalogo.CarterasVivas.Should().BeEmpty();
    }

    [Fact]
    public async Task Quien_llega_cuando_ya_habia_otro_elegible_no_recibe_nada_solo()
    {
        var e = new Escenario();
        e.Cuenta("Administrador");
        var segunda = e.Cuenta("CoordinadorCae");

        await EscaladoDePrincipalDeCartera.AsignarAlPrimerElegibleAsync(e.Catalogo, e.Roles, e.Bloqueo, e.Outbound, segunda, Operador, default);

        e.Catalogo.CarterasVivas.Should().BeEmpty("con más de un elegible decide una persona con «Asumir»");
    }

    /// <summary>
    /// D3, R1 (DECISION_LOG 2026-11-20, punto 5): el primer usuario elegible asume sin una baja previa, así
    /// que no recibe nada con el Tenant de origen del Operador CAE cerrado, y se salta la operación cuyo Tenant
    /// beneficiario está cerrado sin dejar de recibir la abierta. Cada Tenant se pregunta con él como Tenant activo. Por
    /// el puerto (<see cref="AsignacionAutomaticaDePrincipal"/>), como lo llaman el alta, la edición y el cambio de rol.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task D3_el_primer_usuario_elegible_no_asume_operaciones_con_Outbound_cerrado(
        bool beneficiarioCerrado, bool operadorCerrado)
    {
        var otroPropietario = Guid.NewGuid();
        var otraOperacion = Guid.NewGuid();
        var e = new Escenario
        {
            Outbound = new OutboundCerradoTenantTecFijo(
                t => (beneficiarioCerrado && t == Propietario) || (operadorCerrado && t == Operador)),
        };
        e.Catalogo.Asignables.Add((Operador, new TenantCandidatoIncorporacion(otroPropietario, "Obras Sur", otraOperacion)));
        var nueva = e.Cuenta("CoordinadorCae");

        (await new AsignacionAutomaticaDePrincipal(e.Catalogo, e.Roles, e.Bloqueo, e.Outbound)
            .AlPrimerElegibleAsync(nueva, Operador, default)).Should().BeTrue("la cuenta se confirma igual");

        e.Principal().Should().Be(beneficiarioCerrado || operadorCerrado ? null : nueva);
        e.Catalogo.CarterasVivas.Any(c => c.Cartera.AsignacionOperacionId == otraOperacion && c.Cartera.EsPrincipal)
            .Should().Be(!operadorCerrado, "la operación de Tenant beneficiario abierto solo se cierra por el Operador CAE");
        e.Outbound.Ambitos.Should().OnlyContain(p => p.Preguntado == p.Ambito, "cada Tenant se lee con él como Tenant activo");
    }

    // ---------- «Asumir» con Outbound cerrado (D3, R1) ----------

    /// <summary>
    /// D3, R1 (DECISION_LOG 2026-11-20, punto 5): «Asumir» es asumir una operación sin una baja previa
    /// que lo justifique (una que nunca tuvo principal, o con la cartera del principal Expirada sin relevo: para el comando
    /// las dos son una operación sin principal vivo), y se deniega con el Tenant beneficiario cerrado, con el Tenant de
    /// origen del Operador CAE externo cerrado o con los dos, emita o no una Asignación de Cartera nueva. No se guarda nada.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task D3_asumir_sin_baja_previa_se_deniega_con_Outbound_cerrado(
        bool beneficiarioCerrado, bool operadorCerrado, bool yaTieneCartera)
    {
        var e = new Escenario
        {
            Outbound = new OutboundCerradoTenantTecFijo(
                t => (beneficiarioCerrado && t == Propietario) || (operadorCerrado && t == Operador)),
        };
        e.Cuenta("CoordinadorCae", id: Actor);
        e.Cuenta("CoordinadorCae");
        if (yaTieneCartera) e.Cartera(Actor, rol: "CoordinadorCae");
        e.Cartera(Apoyo);

        (await e.AsumirAsync()).Error.Should().Be(CaeManager.Application.Comercial.LineaOutbound.ErroresLimiteTenantTec.OutboundCerrado);

        e.Principal().Should().BeNull("la operación cerrada sin candidato se queda sin principal (coste aceptado)");
        e.Catalogo.CambiosDeMarca.Should().BeEmpty();
        e.Catalogo.CarterasVivas.Count.Should().Be(yaTieneCartera ? 2 : 1, "no se emite ninguna Asignación de Cartera");
        e.Transaccion.Confirmadas.Should().Be(0);
    }

    [Fact]
    public async Task D3_asumir_con_los_dos_abiertos_no_cambia_y_pregunta_por_los_dos()
    {
        var e = new Escenario();
        e.Cuenta("CoordinadorCae", id: Actor);
        e.Cuenta("CoordinadorCae");

        (await e.AsumirAsync()).EsExitoso.Should().BeTrue();

        e.Principal().Should().Be(Actor);
        e.Outbound.Ambitos.Should().BeEquivalentTo(
            [(Operador, (Guid?)Operador), (Propietario, (Guid?)Propietario)],
            "el Tenant de origen del Operador CAE y el Tenant beneficiario, cada uno como Tenant activo");
    }

    // ---------- La alerta con Outbound cerrado (D3, R1) ----------

    /// <summary>
    /// D3, R1 (DECISION_LOG 2026-11-20, punto 5): la alerta lista igual la operación sin principal con Outbound cerrado
    /// para su Tenant beneficiario o para el Tenant de origen del Operador CAE externo, pero no ofrece «Asumir» (ni la
    /// cuenta como asumible), con la misma regla que el comando: lo que ofrece, el comando lo permite, y lo que no,
    /// el comando lo deniega. Cada Tenant se pregunta con él como Tenant activo.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task D3_la_alerta_lista_sin_Asumir_las_operaciones_con_Outbound_cerrado(
        bool beneficiarioCerrado, bool operadorCerrado)
    {
        var otroPropietario = Guid.NewGuid();
        var otraOperacion = Guid.NewGuid();
        var e = new Escenario
        {
            Outbound = new OutboundCerradoTenantTecFijo(
                t => (beneficiarioCerrado && t == Propietario) || (operadorCerrado && t == Operador)),
        };
        e.Catalogo.Asignables.Add((Operador, new TenantCandidatoIncorporacion(otroPropietario, "Obras Sur", otraOperacion)));
        e.Cuenta("CoordinadorCae", id: Actor);
        e.Cuenta("CoordinadorCae");

        var alerta = await e.AlertaAsync();

        alerta.SinPrincipal.Select(o => o.AsignacionOperacionId).Should().BeEquivalentTo(
            [Operacion, otraOperacion], "la fila se lista igual, se pueda asumir o no");
        var cerrada = alerta.Operaciones.Single(o => o.AsignacionOperacionId == Operacion);
        cerrada.SePuedeAsumir.Should().Be(!beneficiarioCerrado && !operadorCerrado);
        cerrada.OutboundCerrado.Should().Be(beneficiarioCerrado || operadorCerrado);
        alerta.Operaciones.Single(o => o.AsignacionOperacionId == otraOperacion).SePuedeAsumir
            .Should().Be(!operadorCerrado, "la de Tenant beneficiario abierto solo se cierra por el Operador CAE");
        alerta.Asumibles.Select(o => o.AsignacionOperacionId).Should().BeEquivalentTo(
            alerta.Operaciones.Where(o => o.SePuedeAsumir).Select(o => o.AsignacionOperacionId));
        alerta.ConCoordinadorCaePrincipal.Should().BeEmpty("cerrada no es lo mismo que con principal");
        e.Outbound.Ambitos.Should().OnlyContain(p => p.Preguntado == p.Ambito, "cada Tenant se lee con él como Tenant activo");
        e.Outbound.Preguntados.Should().BeEquivalentTo(
            operadorCerrado ? [Operador] : new[] { Operador, Propietario, otroPropietario },
            "el Tenant de origen una vez y, si está abierto, cada Tenant beneficiario una vez");

        var asumir = await e.AsumirAsync();
        asumir.EsExitoso.Should().Be(cerrada.SePuedeAsumir, "la alerta ofrece «Asumir» solo donde el comando lo permite");
        if (!cerrada.SePuedeAsumir)
            asumir.Error.Should().Be(CaeManager.Application.Comercial.LineaOutbound.ErroresLimiteTenantTec.OutboundCerrado);
    }

    [Fact]
    public async Task D3_la_alerta_pregunta_una_vez_por_Tenant_beneficiario_y_no_por_las_que_tienen_principal()
    {
        var e = new Escenario();
        e.Cuenta("CoordinadorCae", id: Actor);
        var mismoPropietario = Guid.NewGuid();
        var conCoordinadorPropietario = Guid.NewGuid();
        var conCoordinador = Guid.NewGuid();
        e.Catalogo.Asignables.Add((Operador, new TenantCandidatoIncorporacion(Propietario, "Talleres Norte bis", mismoPropietario)));
        e.Catalogo.Asignables.Add((Operador, new TenantCandidatoIncorporacion(conCoordinadorPropietario, "Con coordinador", conCoordinador)));
        e.Catalogo.CarterasVivas.Add((Operador, new CarteraVivaDeOperacion(
            conCoordinador, conCoordinadorPropietario, "Con coordinador", e.Cuenta("CoordinadorCae"), "CoordinadorCae", true, null)));

        var alerta = await e.AlertaAsync();

        alerta.Asumibles.Select(o => o.AsignacionOperacionId).Should().BeEquivalentTo([Operacion, mismoPropietario]);
        alerta.ConCoordinadorCaePrincipal.Single().OutboundCerrado.Should().BeFalse("con principal no se evalúa D3");
        e.Outbound.Preguntados.Should().Equal([Operador, Propietario],
            "un Tenant beneficiario con dos operaciones se lee una vez, y el de la operación con principal no se lee");
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
    public async Task Decide_el_rol_de_la_sesion_en_origen_e_Identity_solo_lo_confirma()
    {
        // La sesión es de Coordinador CAE; en Identity la cuenta ya es Gestor CAE.
        var degradado = new Escenario { RolDeSesion = "CoordinadorCae" };
        degradado.Cuenta("GestorCae", id: Actor);
        (await degradado.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.SinAutoridad);

        // Y al revés (decisión P7): la sesión se abrió como Gestor CAE y la cuenta recibió después
        // Coordinador CAE en Identity. El rol nuevo no autoriza hasta la sesión siguiente.
        var ascendido = new Escenario { RolDeSesion = "GestorCae" };
        ascendido.Cuenta("CoordinadorCae", id: Actor);
        ascendido.Cartera(Apoyo);
        (await ascendido.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.SinAutoridad);
        ascendido.Principal().Should().BeNull();
        ascendido.Catalogo.CambiosDeMarca.Should().BeEmpty();
        ascendido.Transaccion.Ejecutadas.Should().Be(0);

        // Una sesión limitada a Consulta de una cuenta de Dirección CAE, tampoco.
        var limitada = new Escenario { RolDeSesion = "Consulta" };
        limitada.Cuenta("DireccionCae", id: Actor);
        (await limitada.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.SinAutoridad);
    }

    [Fact]
    public async Task Con_el_rol_solo_en_otro_Tenant_no_se_asume()
    {
        // La sesión dice Administrador, pero Identity solo se lo da en otro Tenant.
        var e = new Escenario { RolDeSesion = "Administrador" };
        e.Cuenta("Administrador", tenant: OtroOperador, id: Actor);

        (await e.AsumirAsync()).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.SinAutoridad);
        e.Transaccion.Ejecutadas.Should().Be(0);
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
        var e = new Escenario();
        e.Cuenta(rol, id: Actor);

        var alerta = await e.AlertaAsync();

        alerta.LaVe.Should().Be(laVe);
        alerta.Operaciones.Should().HaveCount(laVe ? 1 : 0);
    }

    [Theory]
    [InlineData("GestorCae", "CoordinadorCae")] // la cuenta recibió Coordinador CAE tras abrir sesión
    [InlineData("Consulta", "DireccionCae")]    // sesión limitada a Consulta por login local
    [InlineData("CoordinadorCae", "GestorCae")] // la cuenta perdió Coordinador CAE con la sesión viva
    public async Task La_alerta_la_decide_el_rol_de_la_sesion_en_origen_confirmado_por_Identity(
        string rolDeSesion, string rolEnIdentity)
    {
        var e = new Escenario { RolDeSesion = rolDeSesion };
        e.Cuenta(rolEnIdentity, id: Actor);

        var alerta = await e.AlertaAsync();

        alerta.Should().Be(AlertaDePrincipal.Ninguna, "ni las operaciones sin principal ni la acción de asumir se ofrecen");
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
