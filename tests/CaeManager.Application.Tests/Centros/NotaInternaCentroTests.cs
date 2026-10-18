using CaeManager.Application.Centros.Commands.GuardarNotaInternaCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// La «Nota interna» de Centro 360, gemela de la de Empresa 360: la orden que la guarda (solo la nota, alcance de
/// gestión con el mismo «no encontrado», concurrencia optimista y rol con escritura) y la lectura (solo viaja a quien
/// la lee según <see cref="IPoliticaNotaInterna"/>).
/// </summary>
public class NotaInternaCentroTests
{
    private static Centro Nave() => new(Guid.NewGuid(), Guid.NewGuid(), "Nave de Cogullada", "C-07", "Calle de prueba 1");

    private static (GuardarNotaInternaCentroCommandHandler Handler, UnitOfWorkFalso UnitOfWork) Montar(
        Centro? centro, AlcanceDatosServiceFalso? alcance = null)
    {
        var repositorio = new CentroRepositorioFalso();
        if (centro is not null)
            repositorio.Agregar(centro);
        var unitOfWork = new UnitOfWorkFalso();
        return (new GuardarNotaInternaCentroCommandHandler(repositorio, alcance ?? new AlcanceDatosServiceFalso(), unitOfWork), unitOfWork);
    }

    [Fact]
    public async Task Guarda_la_nota_y_no_toca_los_datos_del_centro()
    {
        var centro = Nave();
        var (handler, unitOfWork) = Montar(centro);

        var resultado = await handler.Handle(
            new GuardarNotaInternaCentroCommand(centro.Id, "Entrada por la puerta 3.\nLlave en garita.", centro.Version),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        centro.Notas.Should().Be("Entrada por la puerta 3.\nLlave en garita.");
        centro.Nombre.Should().Be("Nave de Cogullada");
        centro.CodigoCentro.Should().Be("C-07");
        centro.Direccion.Should().Be("Calle de prueba 1");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData(null)]
    public async Task Vacia_o_solo_espacios_deja_el_centro_sin_nota(string? notas)
    {
        var centro = Nave();
        centro.FijarNotaInterna("Nota anterior.");
        var (handler, _) = Montar(centro);

        var resultado = await handler.Handle(new GuardarNotaInternaCentroCommand(centro.Id, notas, centro.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        centro.Notas.Should().BeNull();
    }

    [Fact]
    public async Task Falla_cuando_el_centro_no_existe()
    {
        var (handler, unitOfWork) = Montar(centro: null);

        var resultado = await handler.Handle(
            new GuardarNotaInternaCentroCommand(Guid.NewGuid(), "Nota.", Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Centro.NoEncontrado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Un Gestor CAE fuera de la Asignación de Cartera del Centro (alcance de gestión vacío) recibe lo mismo que si no
    /// existiera, y la nota no cambia. El Centro sigue visible en lectura: la escritura exige gestión.
    /// </summary>
    [Fact]
    public async Task Fuera_de_la_cartera_no_guarda_y_responde_no_encontrado_aunque_lo_vea()
    {
        var centro = Nave();
        centro.FijarNotaInterna("Nota del equipo.");
        var (handler, unitOfWork) = Montar(centro, new AlcanceDatosServiceFalso(centroIdsParaGestion: [Guid.NewGuid()]));

        var resultado = await handler.Handle(
            new GuardarNotaInternaCentroCommand(centro.Id, "Nota de quien no lo gestiona.", centro.Version), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Centro.NoEncontrado");
        centro.Notas.Should().Be("Nota del equipo.");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_una_version_anterior_responde_conflicto_y_no_pisa_la_nota()
    {
        var centro = Nave();
        centro.FijarNotaInterna("Lo que guardó otra persona.");
        var (handler, unitOfWork) = Montar(centro);

        var resultado = await handler.Handle(
            new GuardarNotaInternaCentroCommand(centro.Id, "Lo mío.", Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        centro.Notas.Should().Be("Lo que guardó otra persona.");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_admite_el_maximo_y_rechaza_uno_mas_en_el_campo()
    {
        var validador = new GuardarNotaInternaCentroCommandValidator();

        validador.Validate(new GuardarNotaInternaCentroCommand(Guid.NewGuid(), new string('a', Centro.LongitudMaximaNotas), Guid.NewGuid()))
            .IsValid.Should().BeTrue();
        validador.Validate(new GuardarNotaInternaCentroCommand(Guid.NewGuid(), null, Guid.NewGuid())).IsValid.Should().BeTrue();
        validador.Validate(new GuardarNotaInternaCentroCommand(Guid.Empty, "Nota.", Guid.NewGuid())).IsValid.Should().BeFalse();

        var resultado = validador.Validate(
            new GuardarNotaInternaCentroCommand(Guid.NewGuid(), new string('a', Centro.LongitudMaximaNotas + 1), Guid.NewGuid()));

        resultado.Errors.Should().ContainSingle().Which.Should().Match<FluentValidation.Results.ValidationFailure>(e =>
            e.PropertyName == nameof(GuardarNotaInternaCentroCommand.Notas)
            && e.ErrorMessage == $"La nota interna no puede superar {Centro.LongitudMaximaNotas} caracteres.");
    }

    // ── Rol: lo decide AutorizacionEscrituraBehavior, por ser ICommand ────

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private static async Task<(Result Resultado, bool LlegoAlHandler)> EjecutarConRolAsync(string? rol)
    {
        var behavior = new AutorizacionEscrituraBehavior<GuardarNotaInternaCentroCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), new SinSesionPrivilegiada(), new SinTenant());
        var llego = false;

        var resultado = await behavior.Handle(
            new GuardarNotaInternaCentroCommand(Guid.NewGuid(), "Nota.", Guid.NewGuid()),
            _ =>
            {
                llego = true;
                return Task.FromResult(Result.Exito());
            },
            CancellationToken.None);

        return (resultado, llego);
    }

    [Theory]
    [InlineData("Cliente")]
    [InlineData("Consulta")]
    [InlineData("RolQueNoExiste")]
    [InlineData(null)]
    public async Task Ni_el_usuario_de_Cliente_ni_Consulta_ni_un_rol_desconocido_llegan_a_guardar_la_nota(string? rol)
    {
        var (resultado, llegoAlHandler) = await EjecutarConRolAsync(rol);

        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        llegoAlHandler.Should().BeFalse();
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Un_rol_con_escritura_llega_al_handler(string rol)
    {
        var (resultado, llegoAlHandler) = await EjecutarConRolAsync(rol);

        resultado.EsExitoso.Should().BeTrue();
        llegoAlHandler.Should().BeTrue();
    }

    // ── Lectura: ObtenerCentroPorIdQuery ──────────────────────────────────

    private static (Centro Centro, ObtenerCentroPorIdQueryHandler Handler) ConNota(IPoliticaNotaInterna politica,
        AlcanceDatosServiceFalso? alcance = null)
    {
        var titular = new Empresa("Empresa contraparte de prueba");
        var ejecutora = new Empresa("Empresa ejecutora de prueba");
        var centro = new Centro(titular.Id, ejecutora.Id, "Centro de prueba");
        centro.FijarNotaInterna("Entrada por la puerta 3.");
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(centro);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.AddRange([titular, ejecutora]);
        return (centro, new ObtenerCentroPorIdQueryHandler(centros, empresas, alcance ?? new AlcanceDatosServiceFalso(), politica));
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task El_equipo_del_Tenant_recibe_la_nota(string rol)
    {
        var (centro, handler) = ConNota(PoliticaNotaInternaPruebas.Con(rol));

        var dto = await handler.Handle(new(centro.Id), CancellationToken.None);

        dto!.Notas.Should().Be("Entrada por la puerta 3.");
        dto.NotaInternaVisible.Should().BeTrue();
    }

    /// <summary>Como en Empresa 360, la lectura de la nota no depende del alcance de gestión: Consulta la lee.</summary>
    [Fact]
    public async Task La_nota_viaja_aunque_el_lector_no_tenga_alcance_de_gestion()
    {
        var (centro, handler) = ConNota(PoliticaNotaInternaPruebas.Con("GestorCae"), new AlcanceDatosServiceFalso(centroIdsParaGestion: []));

        var dto = await handler.Handle(new(centro.Id), CancellationToken.None);

        dto!.FichaVisible.Should().BeFalse();
        dto.Notas.Should().Be("Entrada por la puerta 3.");
        dto.NotaInternaVisible.Should().BeTrue();
    }

    [Theory]
    [InlineData("Cliente")]
    [InlineData("RolQueNoExiste")]
    [InlineData(null)]
    public async Task El_usuario_de_Cliente_y_un_rol_desconocido_no_reciben_la_nota(string? rol)
    {
        var (centro, handler) = ConNota(PoliticaNotaInternaPruebas.Con(rol));

        var dto = await handler.Handle(new(centro.Id), CancellationToken.None);

        dto.Should().NotBeNull("el Centro sigue siendo visible; solo se retira la nota");
        dto!.Notas.Should().BeNull();
        dto.NotaInternaVisible.Should().BeFalse();
    }

    [Fact]
    public async Task La_impersonacion_no_recibe_la_nota()
    {
        var (centro, handler) = ConNota(
            PoliticaNotaInternaPruebas.Con("GestorCae", PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.Impersonacion)));

        var dto = await handler.Handle(new(centro.Id), CancellationToken.None);

        dto!.Notas.Should().BeNull();
        dto.NotaInternaVisible.Should().BeFalse();
    }
}
