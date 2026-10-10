using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Subcontratas.Commands.GuardarNotaInternaSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>
/// La orden que guarda la «Nota interna» de Subcontrata 360. Cuatro contratos: solo escribe la nota (ni identidad ni
/// discriminadores de rol), exige alcance de gestión con el mismo «no encontrada» que el resto de órdenes de
/// subcontrata, no pisa lo que otra persona guardó entre medias, y solo la ejecuta un rol con escritura.
/// </summary>
public class GuardarNotaInternaSubcontrataCommandTests
{
    private static Empresa Subcontrata() =>
        Empresa.CrearComoSubcontrata("Andamios del Sur S.L.", "B12345674", NivelServicioSubcontrata.Supervisada.ToString());

    private static (GuardarNotaInternaSubcontrataCommandHandler Handler, UnitOfWorkFalso UnitOfWork) Montar(
        Empresa? subcontrata, AlcanceDatosServiceFalso? alcance = null)
    {
        var repositorio = new EmpresaRepositorioFalso();
        if (subcontrata is not null)
            repositorio.Agregar(subcontrata);
        var unitOfWork = new UnitOfWorkFalso();
        return (new GuardarNotaInternaSubcontrataCommandHandler(repositorio, alcance ?? new AlcanceDatosServiceFalso(), unitOfWork), unitOfWork);
    }

    [Fact]
    public async Task Guarda_la_nota_y_no_toca_la_identidad_ni_los_discriminadores_de_rol()
    {
        var subcontrata = Subcontrata();
        var (handler, unitOfWork) = Montar(subcontrata);

        var resultado = await handler.Handle(
            new GuardarNotaInternaSubcontrataCommand(subcontrata.Id, "Llamar antes de las 10.\nPreguntar por Leire.", subcontrata.Version),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        subcontrata.Notas.Should().Be("Llamar antes de las 10.\nPreguntar por Leire.");
        subcontrata.RazonSocial.Should().Be("Andamios del Sur S.L.");
        subcontrata.Cif.Should().Be("B12345674");
        subcontrata.NivelServicio.Should().Be(NivelServicioSubcontrata.Supervisada.ToString());
        subcontrata.EsCritico.Should().BeNull("guardar la nota no convierte a la subcontrata en Cliente");
        subcontrata.EsPropia.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData(null)]
    public async Task Vacia_o_solo_espacios_deja_la_subcontrata_sin_nota(string? notas)
    {
        var subcontrata = Subcontrata();
        subcontrata.FijarNotaInterna("Nota anterior.");
        var (handler, unitOfWork) = Montar(subcontrata);

        var resultado = await handler.Handle(
            new GuardarNotaInternaSubcontrataCommand(subcontrata.Id, notas, subcontrata.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        subcontrata.Notas.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Falla_cuando_la_subcontrata_no_existe()
    {
        var (handler, unitOfWork) = Montar(subcontrata: null);

        var resultado = await handler.Handle(
            new GuardarNotaInternaSubcontrataCommand(Guid.NewGuid(), "Nota.", Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Subcontrata.NoEncontrada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Gemelo de REC-172 (<c>EditarSubcontrataCommandHandlerTests</c>): quien tiene la subcontrata en su cartera de
    /// LECTURA pero no en la de GESTIÓN no escribe su nota, y recibe el mismo «no encontrada» que si no existiera.
    /// </summary>
    [Fact]
    public async Task Sin_alcance_de_gestion_no_guarda_y_responde_no_encontrada()
    {
        var subcontrata = Subcontrata();
        subcontrata.FijarNotaInterna("Nota del equipo.");
        var (handler, unitOfWork) = Montar(
            subcontrata,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrata.Id], subcontrataIdsParaGestion: []));

        var resultado = await handler.Handle(
            new GuardarNotaInternaSubcontrataCommand(subcontrata.Id, "Nota de quien solo lee.", subcontrata.Version), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Subcontrata.NoEncontrada");
        subcontrata.Notas.Should().Be("Nota del equipo.");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_una_version_anterior_responde_conflicto_y_no_pisa_la_nota()
    {
        var subcontrata = Subcontrata();
        subcontrata.FijarNotaInterna("Lo que guardó otra persona.");
        var (handler, unitOfWork) = Montar(subcontrata);
        var versionVieja = Guid.NewGuid();

        var resultado = await handler.Handle(
            new GuardarNotaInternaSubcontrataCommand(subcontrata.Id, "Lo mío.", versionVieja), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        subcontrata.Notas.Should().Be("Lo que guardó otra persona.");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    // ── Validador ─────────────────────────────────────────────────────────

    [Fact]
    public void El_validador_admite_la_nota_del_tamano_maximo_y_la_nota_ausente()
    {
        var validador = new GuardarNotaInternaSubcontrataCommandValidator();

        validador.Validate(new GuardarNotaInternaSubcontrataCommand(Guid.NewGuid(), new string('a', Empresa.LongitudMaximaNotas), Guid.NewGuid()))
            .IsValid.Should().BeTrue();
        validador.Validate(new GuardarNotaInternaSubcontrataCommand(Guid.NewGuid(), null, Guid.NewGuid())).IsValid.Should().BeTrue();
    }

    [Fact]
    public void El_validador_rechaza_una_nota_mas_larga_que_el_maximo_y_lo_dice_en_el_campo()
    {
        var validador = new GuardarNotaInternaSubcontrataCommandValidator();

        var resultado = validador.Validate(
            new GuardarNotaInternaSubcontrataCommand(Guid.NewGuid(), new string('a', Empresa.LongitudMaximaNotas + 1), Guid.NewGuid()));

        resultado.IsValid.Should().BeFalse();
        resultado.Errors.Should().ContainSingle()
            .Which.Should().Match<FluentValidation.Results.ValidationFailure>(e =>
                e.PropertyName == nameof(GuardarNotaInternaSubcontrataCommand.Notas)
                && e.ErrorMessage == $"La nota interna no puede superar {Empresa.LongitudMaximaNotas} caracteres.");
    }

    [Fact]
    public void El_validador_exige_el_identificador()
    {
        var validador = new GuardarNotaInternaSubcontrataCommandValidator();

        validador.Validate(new GuardarNotaInternaSubcontrataCommand(Guid.Empty, "Nota.", Guid.NewGuid())).IsValid.Should().BeFalse();
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
        var behavior = new AutorizacionEscrituraBehavior<GuardarNotaInternaSubcontrataCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), new SinSesionPrivilegiada(), new SinTenant());
        var llego = false;

        var resultado = await behavior.Handle(
            new GuardarNotaInternaSubcontrataCommand(Guid.NewGuid(), "Nota.", Guid.NewGuid()),
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

        resultado.EsFallido.Should().BeTrue();
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
}
