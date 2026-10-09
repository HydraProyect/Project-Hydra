using CaeManager.Application.Clientes;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Application.Tests.Tenants;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Operaciones;

/// <summary>
/// «Al reactivar vuelve a ser principal quien lo era antes» (decisión del propietario, 2026-10-08, D-9):
/// la decisión de a quién vuelve la marca al reactivar una delegación. Aquí se prueba la regla
/// —quién sigue pudiendo llevarla, cuándo se releva, en qué Tenant se escribe y en qué orden se toma
/// el candado—; que el dato sobreviva al cierre en cascada y que la base de datos no deje dos
/// principales lo prueban Domain e integración.
/// </summary>
public class RestaurarPrincipalAlReactivarTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly Guid Operacion = Guid.NewGuid();
    private static readonly Guid AnteriorPrincipal = Guid.NewGuid();
    private static readonly Guid Apoyo = Guid.NewGuid();
    private static readonly Guid Coordinador = Guid.NewGuid();

    private readonly CatalogoIncorporacionCarteraFalso _catalogo = new();
    private readonly DirectorioRolesEnOrigen _cuentas = new();
    private readonly List<string> _eventos = [];
    private readonly BloqueoCarteraUsuarioFalso _bloqueo;
    private readonly DirectorioDestinosCarteraFalso _destinos;

    public RestaurarPrincipalAlReactivarTests()
    {
        _bloqueo = new BloqueoCarteraUsuarioFalso { Eventos = _eventos };
        // Toda cuenta reporta al mismo Coordinador CAE, que es lo que el relevo lee del anterior principal.
        _destinos = new DirectorioDestinosCarteraFalso(new DestinoCartera(true, "GestorCae", Coordinador, false))
        {
            Eventos = _eventos,
        };

        _cuentas.Asignar(AnteriorPrincipal, Operador, "GestorCae");
        _cuentas.Asignar(Apoyo, Operador, "GestorCae");
        _cuentas.Asignar(Coordinador, Operador, "CoordinadorCae");

        // Lo que deja la reactivación ya guardada: las carteras repuestas, todas sin marca.
        Repuesta(AnteriorPrincipal, "GestorCae");
        Repuesta(Apoyo, "GestorCae");
    }

    [Fact]
    public async Task Vuelve_a_ser_principal_quien_lo_era_y_se_escribe_en_el_Tenant_propietario()
    {
        (await Restaurar()).Should().BeTrue();

        Principales().Should().Equal(AnteriorPrincipal);
        _catalogo.CambiosDeMarca.Should().Equal(
            ("encender", Operacion, AnteriorPrincipal, Propietario),
            ("guardar", Guid.Empty, Guid.Empty, Propietario));
        _bloqueo.Compartidos.Should().Equal([AnteriorPrincipal],
            "se protege su cuenta de una desactivación simultánea, y sin relevo no se toca la del Coordinador CAE");
    }

    [Fact]
    public async Task Un_Coordinador_CAE_que_era_el_principal_recupera_la_marca()
    {
        _cuentas.Asignar(AnteriorPrincipal, Operador, "CoordinadorCae");

        (await Restaurar()).Should().BeTrue();

        Principales().Should().Equal(AnteriorPrincipal);
    }

    [Fact]
    public async Task Si_su_cuenta_esta_desactivada_no_recupera_la_marca_y_se_releva_a_su_Coordinador_CAE()
    {
        _cuentas.Desactivar(AnteriorPrincipal);

        (await Restaurar()).Should().BeTrue();

        Principales().Should().Equal(Coordinador);
        _catalogo.CambiosDeMarca.Select(c => c.Paso).Should().Equal("relevar", "guardar");
        _catalogo.CambiosDeMarca.Should().OnlyContain(c => c.TenantActivo == Propietario);
        _bloqueo.Compartidos.Should().Equal(AnteriorPrincipal, Coordinador);
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Administrador")]
    [InlineData(null)]
    public async Task Si_ya_no_es_Gestor_CAE_ni_Coordinador_CAE_no_recupera_la_marca_y_se_releva(string? rolDeHoy)
    {
        _cuentas.Asignar(AnteriorPrincipal, Operador, rolDeHoy);

        (await Restaurar()).Should().BeTrue();

        Principales().Should().Equal(Coordinador);
        _catalogo.CambiosDeMarca.Should().NotContain(c => c.Paso == "encender");
    }

    [Fact]
    public async Task Si_su_cartera_no_se_repuso_se_releva_y_la_marca_no_pasa_a_un_Gestor_CAE_de_apoyo()
    {
        _catalogo.CarterasVivas.RemoveAll(c => c.Cartera.UsuarioId == AnteriorPrincipal);

        (await Restaurar()).Should().BeTrue();

        Principales().Should().Equal(Coordinador);
    }

    [Fact]
    public async Task Sin_Coordinador_CAE_al_que_relevar_la_operacion_queda_sin_principal()
    {
        _cuentas.Desactivar(AnteriorPrincipal);
        _cuentas.Desactivar(Coordinador);

        (await Restaurar()).Should().BeTrue("quedar sin principal no es un fallo: lo recoge el escalado");

        Principales().Should().BeEmpty("nadie decidió que el Gestor CAE de apoyo responda del Tenant");
    }

    [Fact]
    public async Task El_candado_de_cartera_se_toma_antes_de_leer_la_cuenta()
    {
        _cuentas.Desactivar(AnteriorPrincipal);

        await Restaurar();

        // El primer «compartido» es el del anterior principal y va antes de cualquier lectura de cuentas.
        _eventos.First().Should().Be("compartido");
    }

    [Fact]
    public async Task Si_el_guardado_pierde_una_carrera_devuelve_falso_para_que_el_comando_deshaga_todo()
    {
        _catalogo.PierdeLaCarrera = true;

        (await Restaurar()).Should().BeFalse();
    }

    // ── El comando ────────────────────────────────────────────────────────

    [Fact]
    public async Task Reactivar_restaura_al_anterior_principal_que_le_dice_el_escritor_dentro_de_su_transaccion()
    {
        var (handler, delegacion, transaccion) = Comando(anteriorPrincipal: AnteriorPrincipal);

        (await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None))
            .EsExitoso.Should().BeTrue();

        Principales().Should().Equal(AnteriorPrincipal);
        _catalogo.CambiosDeMarca.Select(c => c.Paso).Should().Equal("encender", "guardar");
        (transaccion.Ejecutadas, transaccion.Confirmadas).Should().Be((1, 1));
    }

    [Fact]
    public async Task Reactivar_sin_anterior_principal_no_toca_ninguna_marca()
    {
        var (handler, delegacion, _) = Comando(anteriorPrincipal: null);

        (await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None))
            .EsExitoso.Should().BeTrue();

        _catalogo.CambiosDeMarca.Should().BeEmpty();
        _bloqueo.Compartidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Reactivar_falla_entero_si_devolver_la_marca_pierde_una_carrera()
    {
        _catalogo.PierdeLaCarrera = true;
        var (handler, delegacion, transaccion) = Comando(anteriorPrincipal: AnteriorPrincipal);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Should().Be(ReactivarDelegacionTenantCommandHandler.CambioMientrasReactivabas);
        transaccion.Deshechas.Should().Be(1, "la delegación no puede quedar reactivada con el principal a medias");
    }

    // ── Arnés ─────────────────────────────────────────────────────────────

    private Task<bool> Restaurar() =>
        RelevoDePrincipalDeCartera.RestaurarAlReactivarAsync(
            _catalogo, new OperacionConPrincipal(Propietario, Operacion), Operador, AnteriorPrincipal,
            _destinos, _cuentas, _bloqueo, CancellationToken.None);

    private (ReactivarDelegacionTenantCommandHandler, DelegacionTenant, TransaccionDeComandoFalsa) Comando(Guid? anteriorPrincipal)
    {
        var delegacion = new DelegacionTenant(Operador, Propietario);
        delegacion.Desactivar();
        var repositorio = new DelegacionTenantRepositorioFalso();
        repositorio.Agregar(delegacion);

        // El escritor falso abre una operación con su propio Id: las carteras repuestas cuelgan de ella.
        var escritor = new AsignacionesOperativasWriterFalso
        {
            AnteriorPrincipal = anteriorPrincipal,
            AlReabrirCarteras = operacion =>
            {
                _catalogo.CarterasVivas.Clear();
                Repuesta(AnteriorPrincipal, "GestorCae", operacion.Id);
                Repuesta(Apoyo, "GestorCae", operacion.Id);
            },
        };
        var transaccion = new TransaccionDeComandoFalsa();
        var handler = new ReactivarDelegacionTenantCommandHandler(
            repositorio, AutorizacionDelegacionFalsa.AdministradorDe(Propietario),
            new CurrentUserServiceFalso(Guid.NewGuid()), escritor, new UnitOfWorkFalso(), new TenantsQueryContextFalso(),
            transaccion, _catalogo, _destinos, _cuentas, _bloqueo);

        return (handler, delegacion, transaccion);
    }

    private void Repuesta(Guid usuarioId, string rol, Guid? operacionId = null) =>
        _catalogo.CarterasVivas.Add((Operador, new CarteraVivaDeOperacion(
            operacionId ?? Operacion, Propietario, "Empresa", usuarioId, rol, EsPrincipal: false, VigenciaHasta: null)));

    private List<Guid> Principales() =>
        _catalogo.CarterasVivas.Where(c => c.Cartera.EsPrincipal).Select(c => c.Cartera.UsuarioId).ToList();
}
