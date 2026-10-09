using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using CaeManager.Application.Usuarios.Queries.ObtenerCarteraDeGestorCae;
using CaeManager.Domain.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// Asignar y retirar Tenants beneficiarios enteros de la cartera de un Gestor CAE que ya existe.
/// Las negativas de autorización viven aquí, en Application; que la escritura sea atómica bajo
/// RLS lo prueba <c>AsignarCarteraGestorCaeBajoRuntimeTests</c> en integración.
/// </summary>
public class AsignarCarteraGestorCaeCommandTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid OtroOperador = Guid.NewGuid();
    private static readonly Guid Beneficiario1 = Guid.NewGuid();
    private static readonly Guid Beneficiario2 = Guid.NewGuid();
    private static readonly Guid BeneficiarioSinOperacion = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid Gestor = Guid.NewGuid();

    private sealed class Escenario
    {
        public CatalogoIncorporacionCarteraFalso Catalogo { get; } = new();
        public TransaccionDeComandoFalsa Transaccion { get; } = new();
        public BloqueoCarteraUsuarioFalso Bloqueo { get; } = new();
        public DirectorioRolesEnOrigen Directorio { get; } = new();
        public Guid TenantOrigenDelActor { get; init; } = Operador;
        public string? RolDeSesion { get; init; } = "Administrador";
        public DestinoCartera Destino { get; init; } = new(Activa: true, RolEfectivo: "GestorCae", CoordinadorUsuarioId: null, EsOperadorDelegado: false);

        public Escenario()
        {
            Catalogo.RegistrarAsignable(Operador, Operacion(Beneficiario1, Operador), "Beneficiario Uno");
            Catalogo.RegistrarAsignable(Operador, Operacion(Beneficiario2, Operador), "Beneficiario Dos");
            Catalogo.RegistrarAsignable(OtroOperador, Operacion(BeneficiarioSinOperacion, OtroOperador), "Ajeno");
        }

        public Escenario ActorConRol(string rol)
        {
            Directorio.Asignar(Actor, TenantOrigenDelActor, rol);
            return this;
        }

        public AsignarCarteraGestorCaeCommandHandler Handler() => new(
            new CurrentUserServicePorAmbito(Actor, TenantOrigenDelActor, RolDeSesion),
            Directorio,
            new DirectorioDestinosCarteraFalso(Destino),
            Catalogo,
            Transaccion,
            Bloqueo);

        public AsignarCarteraGestorCaeCommandHandler Handler(DestinoCartera destino) => new(
            new CurrentUserServicePorAmbito(Actor, TenantOrigenDelActor, RolDeSesion),
            Directorio,
            new DirectorioDestinosCarteraFalso(destino),
            Catalogo,
            Transaccion,
            Bloqueo);
    }

    private static AsignacionOperacion Operacion(Guid propietario, Guid operador) =>
        AsignacionOperacion.Externa(
            propietario, operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            DateTime.UtcNow.AddDays(-30), null, DateTime.UtcNow);

    private static AsignarCarteraGestorCaeCommand Asignar(params Guid[] tenants) => new(Gestor, tenants, null);

    private static AsignarCarteraGestorCaeCommand Retirar(params Guid[] tenants) => new(Gestor, null, tenants);

    private static void NadaEscrito(Escenario e)
    {
        e.Catalogo.IncorporacionesDirectas.Should().BeEmpty();
        e.Catalogo.Retiradas.Should().BeEmpty();
        e.Catalogo.TenantsAlGuardar.Should().BeEmpty();
        e.Transaccion.Ejecutadas.Should().Be(0);
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    public async Task Quien_tiene_autoridad_asigna_dos_Tenants_en_una_transaccion_con_el_propietario_como_ambito(string rol)
    {
        var e = new Escenario { Destino = new(true, "GestorCae", Actor, false) }.ActorConRol(rol);

        var resultado = await e.Handler().Handle(Asignar(Beneficiario1, Beneficiario2), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        e.Catalogo.IncorporacionesDirectas.Select(i => (i.PropietarioTenantId, i.OperadorTenantId, i.UsuarioId, i.TenantActivo))
            .Should().BeEquivalentTo(new[]
            {
                (Beneficiario1, Operador, Gestor, (Guid?)Beneficiario1),
                (Beneficiario2, Operador, Gestor, (Guid?)Beneficiario2),
            });
        e.Catalogo.TenantsAlGuardar.Should().Equal(Beneficiario1, Beneficiario2);
        e.Transaccion.Confirmadas.Should().Be(1);
        e.Transaccion.Deshechas.Should().Be(0);
    }

    [Fact]
    public async Task Toma_el_candado_compartido_de_cartera_del_Gestor_CAE_dentro_de_la_transaccion()
    {
        var e = new Escenario().ActorConRol("Administrador");

        (await e.Handler().Handle(Asignar(Beneficiario1), default)).EsExitoso.Should().BeTrue();

        e.Bloqueo.Compartidos.Should().Equal(Gestor);
        e.Bloqueo.Exclusivos.Should().BeEmpty();
    }

    [Fact]
    public async Task Retira_un_Tenant_entero_de_la_cartera_con_el_propietario_como_ambito_y_el_actor()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Catalogo.CarterasUniversales.Add((Operador, Gestor, new TenantEnCarteraDeGestor(Beneficiario1, "Beneficiario Uno")));

        var resultado = await e.Handler().Handle(Retirar(Beneficiario1), default);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        e.Catalogo.Retiradas.Should().ContainSingle().Which.Should().Be((Beneficiario1, Operador, Gestor, Actor, (Guid?)Beneficiario1));
        e.Catalogo.TenantsAlGuardar.Should().Equal(Beneficiario1);
        e.Bloqueo.Exclusivos.Should().BeEquivalentTo(new[] { Gestor }, "retirar decide si la fila heredada sobra: excluye a las reasignaciones de Cliente hacia este Gestor CAE");
        e.Bloqueo.Compartidos.Should().BeEmpty();
        e.Transaccion.Confirmadas.Should().Be(1);
    }

    [Fact]
    public async Task Asigna_y_retira_en_una_sola_transaccion()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Catalogo.CarterasUniversales.Add((Operador, Gestor, new TenantEnCarteraDeGestor(Beneficiario1, "Beneficiario Uno")));

        var resultado = await e.Handler().Handle(new AsignarCarteraGestorCaeCommand(Gestor, [Beneficiario2], [Beneficiario1]), default);

        resultado.EsExitoso.Should().BeTrue();
        e.Catalogo.Retiradas.Should().ContainSingle();
        e.Catalogo.IncorporacionesDirectas.Should().ContainSingle();
        e.Transaccion.Ejecutadas.Should().Be(1);
        e.Transaccion.Confirmadas.Should().Be(1);
    }

    [Fact]
    public async Task Sin_cambios_no_abre_transaccion_ni_escribe()
    {
        var e = new Escenario().ActorConRol("Administrador");

        (await e.Handler().Handle(new AsignarCarteraGestorCaeCommand(Gestor, [], []), default)).EsExitoso.Should().BeTrue();

        NadaEscrito(e);
    }

    // --- Autorización: quién ---

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public async Task Un_rol_sin_autoridad_no_asigna_ni_retira(string rol)
    {
        var e = new Escenario { RolDeSesion = rol }.ActorConRol(rol);
        e.Catalogo.CarterasUniversales.Add((Operador, Gestor, new TenantEnCarteraDeGestor(Beneficiario1, "Beneficiario Uno")));

        (await e.Handler().Handle(Asignar(Beneficiario2), default)).Error.Should().Be(AutoridadSobreCarteraDeGestorCae.SinAutoridad);
        (await e.Handler().Handle(Retirar(Beneficiario1), default)).Error.Should().Be(AutoridadSobreCarteraDeGestorCae.SinAutoridad);

        NadaEscrito(e);
    }

    [Fact]
    public async Task Una_cuenta_sin_rol_en_su_organizacion_no_tiene_autoridad_aunque_la_sesion_diga_Administrador()
    {
        // Claim de sesión Administrador, pero Identity no le da ese rol en el Operador CAE
        // (rol de otro Tenant o degradada): el claim no es autoridad.
        var e = new Escenario { RolDeSesion = "Administrador" };
        e.Directorio.Asignar(Actor, OtroOperador, "Administrador");

        (await e.Handler().Handle(Asignar(Beneficiario1), default)).Error.Should().Be(AutoridadSobreCarteraDeGestorCae.SinAutoridad);

        NadaEscrito(e);
    }

    [Fact]
    public async Task Una_cuenta_desactivada_no_asigna_aunque_su_sesion_siga_viva()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Directorio.Desactivar(Actor);

        (await e.Handler().Handle(Asignar(Beneficiario1), default)).Error.Should().Be(AutoridadSobreCarteraDeGestorCae.SinAutoridad);

        NadaEscrito(e);
    }

    [Fact]
    public async Task Sin_rol_de_negocio_en_la_sesion_falla_cerrado_como_una_sesion_privilegiada_de_plataforma()
    {
        var e = new Escenario { RolDeSesion = null }.ActorConRol("Administrador");

        (await e.Handler().Handle(Asignar(Beneficiario1), default)).Error.Should().Be(AutoridadSobreCarteraDeGestorCae.SinAutoridad);

        NadaEscrito(e);
    }

    [Fact]
    public async Task Un_Administrador_de_otro_Operador_CAE_no_puede_asignar_los_Tenants_de_este()
    {
        // Es Administrador en su propio Tenant (OtroOperador): sus asignables son los suyos.
        var e = new Escenario { TenantOrigenDelActor = OtroOperador }.ActorConRol("Administrador");

        var resultado = await e.Handler().Handle(Asignar(Beneficiario1), default);

        resultado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.EmpresaNoAsignable);
        e.Catalogo.IncorporacionesDirectas.Should().BeEmpty();
        e.Transaccion.Ejecutadas.Should().Be(0);
    }

    // --- Autorización: sobre quién ---

    [Fact]
    public async Task Un_Coordinador_CAE_no_toca_la_cartera_de_un_Gestor_CAE_que_no_es_de_su_equipo()
    {
        var otroCoordinador = Guid.NewGuid();
        var e = new Escenario { Destino = new(true, "GestorCae", otroCoordinador, false) }.ActorConRol("CoordinadorCae");
        e.Catalogo.CarterasUniversales.Add((Operador, Gestor, new TenantEnCarteraDeGestor(Beneficiario1, "Beneficiario Uno")));

        (await e.Handler().Handle(Asignar(Beneficiario2), default)).Error.Should().Be(AutoridadSobreCarteraDeGestorCae.GestorFueraDeTuEquipo);
        (await e.Handler().Handle(Retirar(Beneficiario1), default)).Error.Should().Be(AutoridadSobreCarteraDeGestorCae.GestorFueraDeTuEquipo);

        NadaEscrito(e);
    }

    [Fact]
    public async Task Un_Coordinador_CAE_no_toca_a_un_Gestor_CAE_sin_coordinador()
    {
        var e = new Escenario { Destino = new(true, "GestorCae", null, false) }.ActorConRol("CoordinadorCae");

        (await e.Handler().Handle(Asignar(Beneficiario1), default)).Error.Should().Be(AutoridadSobreCarteraDeGestorCae.GestorFueraDeTuEquipo);

        NadaEscrito(e);
    }

    [Fact]
    public async Task Un_Administrador_asigna_a_cualquier_Gestor_CAE_del_Operador_CAE_aunque_reporte_a_otro_Coordinador()
    {
        var e = new Escenario { Destino = new(true, "GestorCae", Guid.NewGuid(), false) }.ActorConRol("Administrador");

        (await e.Handler().Handle(Asignar(Beneficiario1), default)).EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("Consulta")]
    public async Task La_cartera_no_se_asigna_a_una_cuenta_que_no_es_Gestor_CAE_y_la_Operacion_no_concede_roles_de_Propiedad(string rolDestino)
    {
        var e = new Escenario { Destino = new(true, rolDestino, Actor, false) }.ActorConRol("Administrador");

        (await e.Handler().Handle(Asignar(Beneficiario1), default)).Error.Should().Be(AutoridadSobreCarteraDeGestorCae.CuentaNoEsGestorCae);

        NadaEscrito(e);
    }

    [Fact]
    public async Task Un_Gestor_CAE_de_un_Operador_CAE_externo_delegado_no_es_alcanzable()
    {
        var e = new Escenario { Destino = new(true, "GestorCae", null, EsOperadorDelegado: true) }.ActorConRol("Administrador");

        (await e.Handler().Handle(Asignar(Beneficiario1), default)).Error.Should().Be(AutoridadSobreCarteraDeGestorCae.GestorNoAlcanzable);

        NadaEscrito(e);
    }

    [Fact]
    public async Task Una_cuenta_que_no_existe_no_es_alcanzable()
    {
        var e = new Escenario().ActorConRol("Administrador");

        var resultado = await new AsignarCarteraGestorCaeCommandHandler(
                new CurrentUserServicePorAmbito(Actor, Operador, "Administrador"), e.Directorio,
                new DirectorioDestinosCarteraFalso(null), e.Catalogo, e.Transaccion, e.Bloqueo)
            .Handle(Asignar(Beneficiario1), default);

        resultado.Error.Should().Be(AutoridadSobreCarteraDeGestorCae.GestorNoAlcanzable);
    }

    // --- Qué Tenants ---

    [Fact]
    public async Task Un_Tenant_sin_Asignacion_de_Operacion_vigente_del_Operador_CAE_no_se_asigna_y_no_se_escribe_nada()
    {
        var e = new Escenario().ActorConRol("Administrador");

        var resultado = await e.Handler().Handle(Asignar(Beneficiario1, BeneficiarioSinOperacion), default);

        resultado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.EmpresaNoAsignable);
        NadaEscrito(e);
    }

    [Fact]
    public async Task Si_la_operacion_deja_de_estar_vigente_a_mitad_se_deshace_toda_la_transaccion()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Catalogo.AnularAlIncorporar = MotivoAnulacionSolicitudCartera.OperacionNoVigente;

        var resultado = await e.Handler().Handle(Asignar(Beneficiario1, Beneficiario2), default);

        resultado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.EmpresaNoAsignable);
        e.Transaccion.Ejecutadas.Should().Be(1);
        e.Transaccion.Deshechas.Should().Be(1);
        e.Transaccion.Confirmadas.Should().Be(0);
    }

    [Fact]
    public async Task Si_ya_la_tenia_en_cartera_no_se_le_asigna_otra_vez_y_se_deshace()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Catalogo.AnularAlIncorporar = MotivoAnulacionSolicitudCartera.YaEnCartera;

        var resultado = await e.Handler().Handle(Asignar(Beneficiario1), default);

        resultado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.YaTieneCartera);
        // El mensaje visible describe el modelo vigente (D-7): sin «en parte» ni reparto por Cliente empresarial.
        resultado.Error.Mensaje.Should().Contain("por otra vía").And.NotContainAny("en parte", "reparto", "ampliar");
        e.Transaccion.Deshechas.Should().Be(1);
    }

    [Fact]
    public async Task Perder_una_carrera_al_guardar_deshace_la_transaccion()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Catalogo.PierdeLaCarrera = true;

        var resultado = await e.Handler().Handle(Asignar(Beneficiario1), default);

        resultado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.CarteraNoGuardada);
        e.Transaccion.Deshechas.Should().Be(1);
    }

    [Fact]
    public async Task No_se_retira_un_Tenant_que_el_Gestor_CAE_no_tiene_entero_y_no_se_escribe_nada()
    {
        var e = new Escenario().ActorConRol("Administrador");

        var resultado = await e.Handler().Handle(Retirar(Beneficiario1), default);

        resultado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.EmpresaNoEnCartera);
        NadaEscrito(e);
    }

    [Fact]
    public async Task No_se_retira_la_cartera_de_otro_Gestor_CAE_del_mismo_Tenant()
    {
        var e = new Escenario().ActorConRol("Administrador");
        e.Catalogo.CarterasUniversales.Add((Operador, Guid.NewGuid(), new TenantEnCarteraDeGestor(Beneficiario1, "Beneficiario Uno")));

        (await e.Handler().Handle(Retirar(Beneficiario1), default)).Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.EmpresaNoEnCartera);
    }

    [Fact]
    public async Task Una_empresa_no_puede_asignarse_y_retirarse_a_la_vez()
    {
        var e = new Escenario().ActorConRol("Administrador");

        var resultado = await e.Handler().Handle(new AsignarCarteraGestorCaeCommand(Gestor, [Beneficiario1], [Beneficiario1]), default);

        resultado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.EmpresaEnAmbasListas);
        NadaEscrito(e);
    }

    [Fact]
    public async Task A_un_Gestor_CAE_desactivado_no_se_le_asigna_pero_se_le_puede_retirar()
    {
        var e = new Escenario { Destino = new(Activa: false, "GestorCae", null, false) }.ActorConRol("Administrador");
        e.Catalogo.CarterasUniversales.Add((Operador, Gestor, new TenantEnCarteraDeGestor(Beneficiario1, "Beneficiario Uno")));

        (await e.Handler().Handle(Asignar(Beneficiario2), default)).Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.GestorDesactivado);
        e.Catalogo.IncorporacionesDirectas.Should().BeEmpty();
        e.Transaccion.Ejecutadas.Should().Be(0, "se rechaza antes de abrir la transacción");

        (await e.Handler().Handle(Retirar(Beneficiario1), default)).EsExitoso.Should().BeTrue();
        e.Catalogo.Retiradas.Should().ContainSingle();
    }

    [Fact]
    public async Task Si_la_cuenta_queda_desactivada_tras_el_candado_no_se_le_asigna_y_se_deshace()
    {
        // El candado compartido espera a una desactivación con traspaso en curso; al recuperar
        // el paso, la lectura dentro de la transacción ve la cuenta ya desactivada.
        var e = new Escenario().ActorConRol("Administrador");
        var lecturas = new DestinoQueSeDesactivaAlSegundaLectura();

        var resultado = await new AsignarCarteraGestorCaeCommandHandler(
                new CurrentUserServicePorAmbito(Actor, Operador, "Administrador"), e.Directorio,
                lecturas, e.Catalogo, e.Transaccion, e.Bloqueo)
            .Handle(Asignar(Beneficiario1), default);

        resultado.Error.Should().Be(AsignarCarteraGestorCaeCommandHandler.GestorDesactivado);
        e.Catalogo.IncorporacionesDirectas.Should().BeEmpty();
        e.Transaccion.Deshechas.Should().Be(1);
    }

    // --- La autoridad se repite con el candado (Codex, #996, pasada 1) ---

    /// <summary>Un destino que cambia entre la primera lectura (fuera del candado) y la segunda (dentro).</summary>
    private sealed class DestinoQueCambia(DestinoCartera antes, DestinoCartera despues) : IDirectorioDestinosCartera
    {
        private int _lecturas;

        public Task<DestinoCartera?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DestinoCartera?>(++_lecturas == 1 ? antes : despues);
    }

    private static async Task<(Escenario Escenario, CaeManager.Domain.Common.Result Resultado)> EjecutarConCambioDeDestino(
        DestinoCartera antes, DestinoCartera despues, AsignarCarteraGestorCaeCommand comando)
    {
        var e = new Escenario().ActorConRol("CoordinadorCae");
        e.Catalogo.CarterasUniversales.Add((Operador, Gestor, new TenantEnCarteraDeGestor(Beneficiario1, "Beneficiario Uno")));
        var resultado = await new AsignarCarteraGestorCaeCommandHandler(
                new CurrentUserServicePorAmbito(Actor, Operador, "CoordinadorCae"), e.Directorio,
                new DestinoQueCambia(antes, despues), e.Catalogo, e.Transaccion, e.Bloqueo)
            .Handle(comando, default);
        return (e, resultado);
    }

    [Fact]
    public async Task Si_el_Gestor_CAE_cambia_de_equipo_durante_el_comando_no_se_asigna_ni_se_retira_nada()
    {
        var suyo = new DestinoCartera(true, "GestorCae", Actor, false);
        var deOtro = new DestinoCartera(true, "GestorCae", Guid.NewGuid(), false);

        var (asignar, r1) = await EjecutarConCambioDeDestino(suyo, deOtro, Asignar(Beneficiario2));
        r1.Error.Should().Be(AutoridadSobreCarteraDeGestorCae.GestorFueraDeTuEquipo);
        asignar.Catalogo.IncorporacionesDirectas.Should().BeEmpty();
        asignar.Transaccion.Deshechas.Should().Be(1);

        // Solo retirar: antes no había una segunda lectura y la retirada seguía adelante.
        var (retirar, r2) = await EjecutarConCambioDeDestino(suyo, deOtro, Retirar(Beneficiario1));
        r2.Error.Should().Be(AutoridadSobreCarteraDeGestorCae.GestorFueraDeTuEquipo);
        retirar.Catalogo.Retiradas.Should().BeEmpty();
        retirar.Catalogo.CarterasUniversales.Should().ContainSingle();
        retirar.Transaccion.Deshechas.Should().Be(1);
    }

    [Fact]
    public async Task Si_la_cuenta_deja_de_ser_Gestor_CAE_durante_el_comando_no_se_retira_nada()
    {
        var suyo = new DestinoCartera(true, "GestorCae", Actor, false);
        var (e, resultado) = await EjecutarConCambioDeDestino(suyo, suyo with { RolEfectivo = "Consulta" }, Retirar(Beneficiario1));

        resultado.Error.Should().Be(AutoridadSobreCarteraDeGestorCae.CuentaNoEsGestorCae);
        e.Catalogo.Retiradas.Should().BeEmpty();
    }

    private sealed class DestinoQueSeDesactivaAlSegundaLectura : IDirectorioDestinosCartera
    {
        private int _lecturas;

        public Task<DestinoCartera?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DestinoCartera?>(new(Activa: ++_lecturas == 1, "GestorCae", null, false));
    }

    // --- La lista del diálogo ---

    [Fact]
    public async Task La_lista_une_los_asignables_con_lo_que_ya_tiene_y_marca_lo_que_solo_se_puede_retirar()
    {
        var e = new Escenario().ActorConRol("Administrador");
        var caducada = Guid.NewGuid();
        var parcial = Guid.NewGuid();
        e.Catalogo.CarterasUniversales.Add((Operador, Gestor, new TenantEnCarteraDeGestor(Beneficiario1, "Beneficiario Uno")));
        e.Catalogo.CarterasUniversales.Add((Operador, Gestor, new TenantEnCarteraDeGestor(caducada, "Caducada")));
        // Asignable por el Operador CAE, pero el Gestor CAE ya tiene una parte de sus clientes: no es candidato.
        e.Catalogo.RegistrarAsignable(Operador, Operacion(parcial, Operador), "Parcial");
        // Candidato: asignable y sin cartera de ninguna forma.
        e.Catalogo.RegistrarCandidato(Operador, Gestor, Operacion(Beneficiario2, Operador), "Beneficiario Dos");

        var lista = await Consulta(e).Handle(new ObtenerCarteraDeGestorCaeQuery(Gestor), default);

        lista.Should().Equal(
            new EmpresaDeCarteraDeGestor(Beneficiario2, "Beneficiario Dos", EnCartera: false, Asignable: true),
            new EmpresaDeCarteraDeGestor(Beneficiario1, "Beneficiario Uno", EnCartera: true, Asignable: true),
            new EmpresaDeCarteraDeGestor(caducada, "Caducada", EnCartera: true, Asignable: false),
            new EmpresaDeCarteraDeGestor(parcial, "Parcial", EnCartera: false, Asignable: false, CarteraParcial: true));
    }

    [Fact]
    public async Task La_lista_va_vacia_sin_autoridad_o_fuera_del_equipo()
    {
        var sinAutoridad = new Escenario { RolDeSesion = "GestorCae" }.ActorConRol("GestorCae");
        (await Consulta(sinAutoridad).Handle(new ObtenerCarteraDeGestorCaeQuery(Gestor), default)).Should().BeEmpty();

        var fueraDeEquipo = new Escenario { Destino = new(true, "GestorCae", Guid.NewGuid(), false) }.ActorConRol("CoordinadorCae");
        (await Consulta(fueraDeEquipo).Handle(new ObtenerCarteraDeGestorCaeQuery(Gestor), default)).Should().BeEmpty();
    }

    private static ObtenerCarteraDeGestorCaeQueryHandler Consulta(Escenario e) => new(
        new CurrentUserServicePorAmbito(Actor, e.TenantOrigenDelActor, e.RolDeSesion),
        e.Directorio,
        new DirectorioDestinosCarteraFalso(e.Destino),
        e.Catalogo);
}
