using CaeManager.Application.Clientes;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Operaciones.ApoyoCartera;
using CaeManager.Application.Operaciones.ApoyoCartera.Commands;
using CaeManager.Application.Operaciones.ApoyoCartera.Queries;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Notificaciones;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Operaciones.ApoyoCartera;

/// <summary>
/// El fin de un apoyo de cartera (ADR-011 § 2.7, enmienda 2026-10-08, puntos 5 y 6): quién puede
/// desasignarse, quién retira lo que concedió (D-6), quién revoca (D-4), a quién se avisa y la
/// fecha de fin opcional (D-5). Aquí viven las negativas de autorización y la orquestación.
///
/// <para>
/// <b>Lo que NO prueba</b>: que la cartera se cierre de verdad bajo RLS con el Tenant
/// propietario como ámbito, que la fila heredada de Operador Delegado se borre, que la marca de
/// principal de la base de datos impida «Desasignarme» y que el circuito vivo pierda el Tenant.
/// El catálogo es un doble que imita esas reglas; las prueba
/// <c>FinDeApoyoCarteraBajoRuntimeTests</c> en integración contra el catálogo real.
/// </para>
/// </summary>
public class FinDeApoyoCarteraTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid OtroOperador = Guid.NewGuid();
    private static readonly Guid GestorA = Guid.NewGuid(); // principal de partida; propone el apoyo; equipo de CoordinadorDeA
    private static readonly Guid GestorB = Guid.NewGuid(); // Gestor CAE de apoyo; equipo de CoordinadorDeB
    private static readonly Guid GestorC = Guid.NewGuid(); // otro Gestor CAE con cartera; equipo de CoordinadorAjeno
    private static readonly Guid CoordinadorDeA = Guid.NewGuid();
    private static readonly Guid CoordinadorDeB = Guid.NewGuid();
    private static readonly Guid CoordinadorAjeno = Guid.NewGuid();
    private static readonly Guid Direccion = Guid.NewGuid();
    private static readonly Guid Administrador = Guid.NewGuid();
    private static readonly Guid CoordinadorDeOtroOperador = Guid.NewGuid();

    private sealed class Escenario
    {
        public Tenant Empresa { get; } = new("Talleres Norte Demo");
        public AsignacionOperacion Operacion { get; }
        public CatalogoIncorporacionCarteraFalso Catalogo { get; } = new();
        public PropuestaApoyoCarteraRepositorioFalso Repositorio { get; } = new();
        public TransaccionDeComandoFalsa Transaccion { get; } = new();
        public BloqueoCarteraUsuarioFalso Bloqueo { get; } = new();
        public DirectorioRolesEnOrigen Roles { get; } = new();
        public DirectorioDestinosPorUsuario Cuentas { get; } = new();
        public TenantsQueryContextFalso Tenants { get; } = new();
        public NotificacionUsuarioRepositorioFalso Notificaciones { get; } = new();
        public UnitOfWorkConAmbito UnitOfWork { get; } = new();

        public Escenario()
        {
            Operacion = AsignacionOperacion.Externa(
                Empresa.Id, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                DateTime.UtcNow.AddDays(-30), null, DateTime.UtcNow);
            Tenants.ListaTenants.Add(Empresa);
            Catalogo.RegistrarAsignable(Operador, Operacion, Empresa.Nombre);

            Cuenta(GestorA, "GestorCae", CoordinadorDeA);
            Cuenta(GestorB, "GestorCae", CoordinadorDeB);
            Cuenta(GestorC, "GestorCae", CoordinadorAjeno);
            foreach (var coordinador in new[] { CoordinadorDeA, CoordinadorDeB, CoordinadorAjeno })
                Cuenta(coordinador, "CoordinadorCae", null);
            Cuenta(Direccion, "DireccionCae", null);
            Cuenta(Administrador, "Administrador", null);
            Roles.Asignar(CoordinadorDeOtroOperador, OtroOperador, "CoordinadorCae");

            Cartera(GestorA, principal: true);
            Cartera(GestorC);
            Catalogo.RegistrarCandidato(Operador, GestorB, Operacion, Empresa.Nombre);
        }

        /// <summary>La cuenta en Identity: su rol en el Operador CAE y a quién reporta.</summary>
        public void Cuenta(Guid usuarioId, string rol, Guid? coordinador)
        {
            Roles.Asignar(usuarioId, Operador, rol);
            Cuentas.Cuentas[usuarioId] = new DestinoCartera(true, rol, coordinador, EsOperadorDelegado: false);
        }

        public void Cartera(Guid usuarioId, bool principal = false, string rol = "GestorCae") =>
            Catalogo.CarterasVivas.Add((Operador, new CarteraVivaDeOperacion(
                Operacion.Id, Empresa.Id, Empresa.Nombre, usuarioId, rol, principal, null)));

        public void PasarLaMarcaA(Guid usuarioId)
        {
            for (var i = 0; i < Catalogo.CarterasVivas.Count; i++)
            {
                var (operador, cartera) = Catalogo.CarterasVivas[i];
                Catalogo.CarterasVivas[i] = (operador, cartera with { EsPrincipal = cartera.UsuarioId == usuarioId });
            }
        }

        public IEnumerable<Guid> ConCartera => Catalogo.CarterasVivas.Select(c => c.Cartera.UsuarioId);

        public Guid? Principal => Catalogo.CarterasVivas
            .Where(c => c.Cartera.EsPrincipal).Select(c => (Guid?)c.Cartera.UsuarioId).SingleOrDefault();

        /// <summary>El claim de la sesión; el rol de verdad sale de <see cref="Roles"/>.</summary>
        public CurrentUserServicePorAmbito Como(Guid usuarioId, Guid? origen = null, string? rolDeSesion = "GestorCae") =>
            new(usuarioId, origen ?? Operador, rolDeSesion, rolDeSesion);

        public Task<Result<Guid>> Proponer(Guid actor, Guid destinatario, DateOnly? ultimoDia = null) =>
            new ProponerApoyoCarteraCommandHandler(Como(actor), Roles, Catalogo, Repositorio)
                .Handle(new ProponerApoyoCarteraCommand(Operacion.Id, destinatario, ultimoDia), default);

        public Task<Result> Aceptar(Guid actor, Guid propuestaId) =>
            new AceptarPropuestaApoyoCarteraCommandHandler(
                    Como(actor), Roles, Catalogo, Repositorio, Transaccion, Bloqueo,
                    Cuentas, Notificaciones, Tenants, UnitOfWork,
                    NullLogger<AceptarPropuestaApoyoCarteraCommandHandler>.Instance)
                .Handle(new AceptarPropuestaApoyoCarteraCommand(propuestaId), default);

        /// <summary>GestorA propone a GestorB y este acepta: el apoyo vivo de partida. Limpia lo anotado.</summary>
        public async Task<PropuestaApoyoCartera> ApoyoAceptado(DateOnly? ultimoDia = null)
        {
            var propuestaId = await Proponer(GestorA, GestorB, ultimoDia);
            propuestaId.EsExitoso.Should().BeTrue(propuestaId.EsFallido ? propuestaId.Error.Codigo : null);
            (await Aceptar(GestorB, propuestaId.Valor)).EsExitoso.Should().BeTrue();

            Notificaciones.Notificaciones.Clear();
            UnitOfWork.TenantsAlGuardar.Clear();
            Bloqueo.Exclusivos.Clear();
            Catalogo.CambiosDeMarca.Clear();
            return Repositorio.Propuestas.Single(p => p.Id == propuestaId.Valor);
        }

        private TerminarApoyoCarteraCommandHandler Handler(Guid actor, Guid? origen, string? rolDeSesion) =>
            new(Como(actor, origen, rolDeSesion), Roles, Cuentas, Catalogo, Repositorio, Transaccion, Bloqueo,
                Notificaciones, Tenants, UnitOfWork, NullLogger<TerminarApoyoCarteraCommandHandler>.Instance);

        public Task<Result> Desasignarme(Guid actor, Guid propuestaId, string? rolDeSesion = "GestorCae") =>
            Handler(actor, null, rolDeSesion).Handle(new DesasignarmeDeApoyoCommand(propuestaId), default);

        public Task<Result> RetirarLoConcedido(Guid actor, Guid propuestaId, string? rolDeSesion = "GestorCae") =>
            Handler(actor, null, rolDeSesion).Handle(new RetirarApoyoConcedidoCommand(propuestaId), default);

        public Task<Result> Revocar(Guid actor, Guid propuestaId, Guid? origen = null, string? rolDeSesion = "CoordinadorCae") =>
            Handler(actor, origen, rolDeSesion).Handle(new RevocarApoyoCarteraCommand(propuestaId), default);

        public Task<ApoyosDeCarteraDto> Apoyos(Guid actor, string? rolDeSesion = "GestorCae") =>
            new ObtenerApoyosDeCarteraQueryHandler(Como(actor, null, rolDeSesion), Roles, Cuentas, Catalogo)
                .Handle(new ObtenerApoyosDeCarteraQuery(), default);

        /// <summary>El apoyo sigue exactamente como estaba: cartera viva, propuesta aceptada, sin avisos.</summary>
        public void ElApoyoSigueIntacto(PropuestaApoyoCartera propuesta)
        {
            ConCartera.Should().Contain(GestorB, "nada cerró la cartera de apoyo");
            propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Aceptada);
            Notificaciones.Notificaciones.Should().BeEmpty("lo que no pasó no se avisa");
        }
    }

    // ================= Desasignarme =================

    [Fact]
    public async Task El_Gestor_CAE_de_apoyo_se_desasigna_y_solo_se_cierra_su_cartera()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        var resultado = await e.Desasignarme(GestorB, propuesta.Id);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        e.ConCartera.Should().BeEquivalentTo([GestorA, GestorC], "solo se cierra la cartera de quien se desasigna");
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Terminada);
        e.Principal.Should().Be(GestorA);
        e.Catalogo.CambiosDeMarca.Select(c => c.Paso).Should().NotContain(["apagar", "encender", "relevar"],
            "cerrar una cartera de apoyo no toca la marca de principal ni dispara el relevo");
        e.Catalogo.ApoyosRetirados.Should().ContainSingle().Which.Should().Be(
            (propuesta.Id, GestorB, false, (Guid?)e.Empresa.Id),
            "la cartera se cierra con el Tenant propietario como ámbito, que es lo que deja escribir la RLS");
        e.Bloqueo.Exclusivos.Should().Equal([GestorB], "el único candado es el exclusivo sobre el Gestor CAE de apoyo");
        e.Catalogo.Retiradas.Should().BeEmpty("no pasa por la retirada de todas las carteras del usuario sobre el Tenant");
    }

    [Fact]
    public async Task El_principal_no_se_desasigna_por_este_comando()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        // GestorA es el principal y parte del apoyo (lo propuso), pero no es el Gestor CAE de apoyo.
        (await e.Desasignarme(GestorA, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.ApoyoNoEncontrado);

        e.ConCartera.Should().BeEquivalentTo([GestorA, GestorB, GestorC]);
        e.Principal.Should().Be(GestorA);
        e.Catalogo.ApoyosRetirados.Should().BeEmpty("ni se llegó a pedir el cierre");
        e.ElApoyoSigueIntacto(propuesta);
    }

    [Fact]
    public async Task Quien_entro_como_apoyo_y_hoy_es_el_principal_ya_no_puede_desasignarse()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();
        e.PasarLaMarcaA(GestorB);

        (await e.Desasignarme(GestorB, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.EresElPrincipal);

        e.Principal.Should().Be(GestorB, "soltar el principal es una retirada con relevo, no esto");
        e.ElApoyoSigueIntacto(propuesta);
    }

    [Fact]
    public async Task Nadie_se_desasigna_del_apoyo_de_otra_persona()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        (await e.Desasignarme(GestorC, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.ApoyoNoEncontrado);
        (await e.Desasignarme(CoordinadorDeB, propuesta.Id, "CoordinadorCae")).Error.Should().Be(ErroresPropuestaApoyo.ApoyoNoEncontrado,
            "el Coordinador CAE revoca; desasignarse es solo del propio apoyo");

        e.ElApoyoSigueIntacto(propuesta);
    }

    [Fact]
    public async Task Desasignarse_dos_veces_no_encuentra_el_apoyo_la_segunda()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();
        (await e.Desasignarme(GestorB, propuesta.Id)).EsExitoso.Should().BeTrue();

        (await e.Desasignarme(GestorB, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.ApoyoNoEncontrado);

        e.Catalogo.ApoyosRetirados.Should().ContainSingle();
    }

    [Fact]
    public async Task Si_la_cuenta_del_apoyo_ya_no_gestiona_CAE_no_puede_desasignarse()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();
        e.Roles.Desactivar(GestorB);

        (await e.Desasignarme(GestorB, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.SinPermiso,
            "una cuenta desactivada no escribe aunque su circuito siga vivo");

        e.ElApoyoSigueIntacto(propuesta);
    }

    // ================= D-6: el principal retira lo que concedió =================

    [Fact]
    public async Task El_principal_que_concedio_el_apoyo_lo_retira()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        var resultado = await e.RetirarLoConcedido(GestorA, propuesta.Id);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        e.ConCartera.Should().BeEquivalentTo([GestorA, GestorC]);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Terminada);
        e.Principal.Should().Be(GestorA);
        e.Catalogo.ApoyosRetirados.Should().ContainSingle().Which.ExigirProponentePrincipal.Should().BeTrue(
            "que siga siendo el principal lo decide el catálogo, con la cartera seguida y dentro de la transacción");
    }

    /// <summary>
    /// «Solo lo que yo concedí». El caso que lo aísla: quien propuso <b>sigue siendo el principal</b>
    /// (así que la otra mitad de D-6 se cumple) y quien intenta retirar es otra persona.
    /// </summary>
    [Fact]
    public async Task Otro_Gestor_CAE_no_retira_el_apoyo_que_concedio_el_principal_vigente()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        (await e.RetirarLoConcedido(GestorC, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.SoloRetirasLoQueConcediste);
        (await e.RetirarLoConcedido(GestorB, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.SoloRetirasLoQueConcediste,
            "el propio apoyo se desasigna; no «retira lo concedido»");

        e.Catalogo.ApoyosRetirados.Should().BeEmpty();
        e.ElApoyoSigueIntacto(propuesta);
    }

    [Fact]
    public async Task Quien_concedio_el_apoyo_ya_no_lo_retira_si_dejo_de_ser_el_principal()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();
        e.PasarLaMarcaA(GestorC);

        (await e.RetirarLoConcedido(GestorA, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.YaNoEresElPrincipal);
        // Y el principal nuevo tampoco: no lo concedió él. Lo revoca un Coordinador CAE.
        (await e.RetirarLoConcedido(GestorC, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.SoloRetirasLoQueConcediste);

        e.ElApoyoSigueIntacto(propuesta);
    }

    // ================= D-4: revocar =================

    public static TheoryData<string, Guid, string> QuienRevoca => new()
    {
        { "el Coordinador CAE de quien propuso el apoyo", CoordinadorDeA, "CoordinadorCae" },
        { "el Coordinador CAE del Gestor CAE de apoyo", CoordinadorDeB, "CoordinadorCae" },
        { "la Dirección CAE del Operador CAE", Direccion, "DireccionCae" },
        { "el Administrador del Operador CAE", Administrador, "Administrador" },
    };

    [Theory]
    [MemberData(nameof(QuienRevoca))]
    public async Task Revoca_el_apoyo(string quien, Guid actor, string rolDeSesion)
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        var resultado = await e.Revocar(actor, propuesta.Id, rolDeSesion: rolDeSesion);

        resultado.EsExitoso.Should().BeTrue($"{quien} puede revocar ({(resultado.EsFallido ? resultado.Error.Codigo : "ok")})");
        e.ConCartera.Should().BeEquivalentTo([GestorA, GestorC]);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Terminada);
        e.Principal.Should().Be(GestorA);
        e.Catalogo.ApoyosRetirados.Should().ContainSingle().Which.Should().Be((propuesta.Id, actor, false, (Guid?)e.Empresa.Id));
    }

    [Fact]
    public async Task Un_Coordinador_CAE_sin_relacion_con_ninguno_de_los_dos_no_revoca()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        (await e.Revocar(CoordinadorAjeno, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.ApoyoFueraDeTuEquipo);

        e.Catalogo.ApoyosRetirados.Should().BeEmpty();
        e.ElApoyoSigueIntacto(propuesta);
    }

    [Fact]
    public async Task La_jerarquia_se_lee_al_revocar_y_no_la_que_valia_al_proponer()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();
        // Después de aceptarse, los dos cambian de equipo.
        e.Cuenta(GestorA, "GestorCae", CoordinadorAjeno);
        e.Cuenta(GestorB, "GestorCae", null);

        (await e.Revocar(CoordinadorDeA, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.ApoyoFueraDeTuEquipo,
            "quien propuso ya no le reporta");
        (await e.Revocar(CoordinadorDeB, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.ApoyoFueraDeTuEquipo,
            "el Gestor CAE de apoyo ya no le reporta");
        e.ElApoyoSigueIntacto(propuesta);

        (await e.Revocar(CoordinadorAjeno, propuesta.Id)).EsExitoso.Should().BeTrue("hoy le reporta quien propuso el apoyo");
    }

    [Fact]
    public async Task El_rol_para_revocar_sale_de_Identity_y_no_del_claim_de_la_sesion()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        // Un Gestor CAE cuyo claim dice «CoordinadorCae» (o «Administrador») no revoca.
        (await e.Revocar(GestorC, propuesta.Id, rolDeSesion: "CoordinadorCae")).Error.Should().Be(ErroresPropuestaApoyo.SinPermiso);
        (await e.Revocar(GestorA, propuesta.Id, rolDeSesion: "Administrador")).Error.Should().Be(ErroresPropuestaApoyo.SinPermiso);
        // Un Coordinador CAE que perdió el rol en Identity, aunque el claim lo conserve, tampoco.
        e.Roles.Asignar(CoordinadorDeB, Operador, "GestorCae");
        (await e.Revocar(CoordinadorDeB, propuesta.Id, rolDeSesion: "CoordinadorCae")).Error.Should().Be(ErroresPropuestaApoyo.SinPermiso);
        // Ni una cuenta desactivada.
        e.Roles.Desactivar(Direccion);
        (await e.Revocar(Direccion, propuesta.Id, rolDeSesion: "DireccionCae")).Error.Should().Be(ErroresPropuestaApoyo.SinPermiso);
        e.ElApoyoSigueIntacto(propuesta);

        // Y al revés: dentro de un Workspace operativo derivado el claim es el de la cartera
        // («GestorCae»), pero quien es Coordinador CAE en Identity sí revoca.
        (await e.Revocar(CoordinadorDeA, propuesta.Id, rolDeSesion: "GestorCae")).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_rol_de_negocio_en_la_sesion_no_se_revoca()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        (await e.Revocar(Direccion, propuesta.Id, rolDeSesion: null)).Error.Should().Be(ErroresPropuestaApoyo.SinPermiso,
            "Soporte TALVEG nunca es Operador CAE");

        e.ElApoyoSigueIntacto(propuesta);
    }

    [Fact]
    public async Task El_Coordinador_CAE_de_otro_Operador_CAE_no_ve_el_apoyo()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        (await e.Revocar(CoordinadorDeOtroOperador, propuesta.Id, origen: OtroOperador))
            .Error.Should().Be(ErroresPropuestaApoyo.ApoyoNoEncontrado, "la propuesta de otro Operador CAE no existe para él");

        e.Catalogo.ApoyosRetirados.Should().BeEmpty();
        e.ElApoyoSigueIntacto(propuesta);
    }

    [Fact]
    public async Task Un_apoyo_que_hoy_lleva_la_marca_de_principal_no_se_revoca_por_aqui()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();
        e.PasarLaMarcaA(GestorB);

        (await e.Revocar(Direccion, propuesta.Id, rolDeSesion: "DireccionCae")).Error.Should().Be(ErroresPropuestaApoyo.ApoyoEsAhoraPrincipal);

        e.Principal.Should().Be(GestorB);
        e.ElApoyoSigueIntacto(propuesta);
    }

    [Fact]
    public async Task Si_la_cartera_cambio_a_la_vez_no_se_guarda_nada_ni_se_avisa()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();
        e.Catalogo.PierdeLaCarrera = true;

        (await e.Revocar(CoordinadorDeA, propuesta.Id)).Error.Should().Be(ErroresPropuestaApoyo.CambioMientrasDecidias);

        e.Notificaciones.Notificaciones.Should().BeEmpty();
    }

    // ================= El apoyo no depende de la cartera de quien lo propuso =================

    [Fact]
    public async Task Si_quien_propuso_pierde_su_cartera_el_apoyo_ya_aceptado_no_cae()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        await e.Catalogo.RetirarCarteraUniversalAsync(e.Empresa.Id, Operador, GestorA, CoordinadorDeA);

        e.ConCartera.Should().BeEquivalentTo([GestorB, GestorC], "la cartera de apoyo cuelga de la operación, no de la de quien la propuso");
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Aceptada);
        (await e.Apoyos(GestorB)).Mios.Select(a => a.PropuestaId).Should().Equal([propuesta.Id]);
        // Sigue siendo un apoyo como cualquier otro: quien lo tiene puede dejarlo.
        (await e.Desasignarme(GestorB, propuesta.Id)).EsExitoso.Should().BeTrue();
    }

    // ================= Avisos =================

    [Fact]
    public async Task Al_aceptarse_un_apoyo_se_avisa_a_los_Coordinadores_CAE_de_ambos_en_el_Tenant_de_origen()
    {
        var e = new Escenario();
        var propuestaId = (await e.Proponer(GestorA, GestorB)).Valor;

        (await e.Aceptar(GestorB, propuestaId)).EsExitoso.Should().BeTrue();

        e.Notificaciones.Notificaciones.Select(n => n.UsuarioDestinatarioId)
            .Should().BeEquivalentTo([CoordinadorDeA, CoordinadorDeB], "los dos tienen Coordinador CAE: la Dirección CAE no hace falta");
        e.Notificaciones.Notificaciones.Should().OnlyContain(n => n.Mensaje.Contains(e.Empresa.Nombre));
        e.UnitOfWork.TenantsAlGuardar.Should().Equal([Operador],
            "la notificación se sella con el Tenant de origen del Operador CAE, no con el Tenant propietario");
    }

    [Fact]
    public async Task Si_a_alguno_de_los_dos_le_falta_Coordinador_CAE_se_avisa_ademas_a_la_Direccion_CAE()
    {
        var e = new Escenario();
        e.Cuenta(GestorB, "GestorCae", null);
        var propuestaId = (await e.Proponer(GestorA, GestorB)).Valor;

        (await e.Aceptar(GestorB, propuestaId)).EsExitoso.Should().BeTrue();

        e.Notificaciones.Notificaciones.Select(n => n.UsuarioDestinatarioId)
            .Should().BeEquivalentTo([CoordinadorDeA, Direccion]);
    }

    [Fact]
    public async Task Un_Coordinador_CAE_desactivado_cuenta_como_que_falta_y_se_avisa_a_la_Direccion_CAE()
    {
        var e = new Escenario();
        e.Roles.Desactivar(CoordinadorDeB);
        var propuestaId = (await e.Proponer(GestorA, GestorB)).Valor;

        (await e.Aceptar(GestorB, propuestaId)).EsExitoso.Should().BeTrue();

        e.Notificaciones.Notificaciones.Select(n => n.UsuarioDestinatarioId)
            .Should().BeEquivalentTo([CoordinadorDeA, Direccion]);
    }

    [Fact]
    public async Task Al_desasignarse_el_apoyo_se_avisa_a_los_Coordinadores_CAE_de_ambos_y_no_a_el()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        (await e.Desasignarme(GestorB, propuesta.Id)).EsExitoso.Should().BeTrue();

        e.Notificaciones.Notificaciones.Select(n => n.UsuarioDestinatarioId)
            .Should().BeEquivalentTo([CoordinadorDeA, CoordinadorDeB]);
        e.UnitOfWork.TenantsAlGuardar.Should().Equal([Operador]);
    }

    [Fact]
    public async Task Al_revocar_se_avisa_al_otro_Coordinador_CAE_y_al_Gestor_CAE_que_pierde_el_acceso_pero_no_a_quien_revoca()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        (await e.Revocar(CoordinadorDeA, propuesta.Id)).EsExitoso.Should().BeTrue();

        e.Notificaciones.Notificaciones.Select(n => n.UsuarioDestinatarioId)
            .Should().BeEquivalentTo([CoordinadorDeB, GestorB], "quien revoca ya lo sabe");
    }

    [Fact]
    public async Task Un_aviso_que_no_se_puede_guardar_no_deshace_el_fin_del_apoyo()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();
        e.UnitOfWork.ExcepcionAlGuardar = new InvalidOperationException("sin base de datos");

        (await e.Desasignarme(GestorB, propuesta.Id)).EsExitoso.Should().BeTrue("el apoyo ya estaba terminado cuando se intentó avisar");

        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Terminada);
        e.Catalogo.VecesDescartado.Should().BeGreaterThan(0, "lo que quedó a medio escribir se descarta");
    }

    // ================= D-5: fecha de fin opcional =================

    [Fact]
    public async Task Una_fecha_de_fin_anterior_a_hoy_no_se_propone()
    {
        var e = new Escenario();

        (await e.Proponer(GestorA, GestorB, DiaDeNegocio.Hoy().AddDays(-1))).Error.Should().Be(ErroresPropuestaApoyo.FechaDeFinNoValida);

        e.Repositorio.Propuestas.Should().BeEmpty();
    }

    [Fact]
    public async Task El_apoyo_con_fecha_dura_hasta_el_final_de_ese_dia_de_negocio_y_asi_se_rotula()
    {
        var e = new Escenario();
        var ultimoDia = DiaDeNegocio.Hoy();

        var propuesta = await e.ApoyoAceptado(ultimoDia);

        propuesta.VigenciaHastaPropuesta.Should().Be(DiaDeNegocio.InicioEnUtc(ultimoDia.AddDays(1)),
            "«hasta hoy» incluye hoy entero, en hora peninsular");
        e.Catalogo.CarterasVivas.Single(c => c.Cartera.UsuarioId == GestorB).Cartera.VigenciaHasta
            .Should().Be(propuesta.VigenciaHastaPropuesta, "la fecha de la propuesta es la de la cartera que emite");
        (await e.Apoyos(GestorB)).Mios.Should().ContainSingle().Which.UltimoDia.Should().Be(ultimoDia);
    }

    [Fact]
    public async Task Sin_fecha_el_apoyo_no_caduca()
    {
        var e = new Escenario();

        var propuesta = await e.ApoyoAceptado();

        propuesta.VigenciaHastaPropuesta.Should().BeNull();
        (await e.Apoyos(GestorB)).Mios.Should().ContainSingle().Which.UltimoDia.Should().BeNull();
    }

    // ================= La consulta: qué se enseña a cada uno =================

    [Fact]
    public async Task Cada_uno_ve_el_apoyo_en_la_lista_de_lo_que_puede_hacer_con_el()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();

        (await e.Apoyos(GestorB)).Should().BeEquivalentTo(new { Mios = new[] { new { PropuestaId = propuesta.Id } }, Concedidos = Array.Empty<object>(), Revocables = Array.Empty<object>() });
        (await e.Apoyos(GestorA)).Concedidos.Select(a => a.PropuestaId).Should().Equal([propuesta.Id]);
        (await e.Apoyos(GestorC)).EstaVacio.Should().BeTrue("ni es parte del apoyo ni tiene autoridad");
        (await e.Apoyos(CoordinadorDeA, "CoordinadorCae")).Revocables.Select(a => a.PropuestaId).Should().Equal([propuesta.Id]);
        (await e.Apoyos(CoordinadorDeB, "CoordinadorCae")).Revocables.Select(a => a.PropuestaId).Should().Equal([propuesta.Id]);
        (await e.Apoyos(CoordinadorAjeno, "CoordinadorCae")).EstaVacio.Should().BeTrue("sin relación con ninguno de los dos");
        (await e.Apoyos(Direccion, "DireccionCae")).Revocables.Select(a => a.PropuestaId).Should().Equal([propuesta.Id]);
        (await e.Apoyos(Direccion, rolDeSesion: null)).EstaVacio.Should().BeTrue("sin rol de negocio en la sesión, nada");
    }

    [Fact]
    public async Task Quien_concedio_el_apoyo_deja_de_verlo_como_retirable_cuando_ya_no_es_el_principal()
    {
        var e = new Escenario();
        await e.ApoyoAceptado();
        e.PasarLaMarcaA(GestorC);

        (await e.Apoyos(GestorA)).EstaVacio.Should().BeTrue();
        (await e.Apoyos(GestorC)).EstaVacio.Should().BeTrue("el principal nuevo no lo concedió");
    }

    [Fact]
    public async Task Un_apoyo_terminado_ya_no_sale_en_ninguna_lista()
    {
        var e = new Escenario();
        var propuesta = await e.ApoyoAceptado();
        (await e.Desasignarme(GestorB, propuesta.Id)).EsExitoso.Should().BeTrue();

        (await e.Apoyos(GestorB)).EstaVacio.Should().BeTrue();
        (await e.Apoyos(GestorA)).EstaVacio.Should().BeTrue();
        (await e.Apoyos(Direccion, "DireccionCae")).EstaVacio.Should().BeTrue();
    }
}
