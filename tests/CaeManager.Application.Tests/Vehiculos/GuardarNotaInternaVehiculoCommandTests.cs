using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Vehiculos.Commands.GuardarNotaInternaVehiculo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Vehiculos;

/// <summary>
/// La orden que guarda la «Nota interna» de Vehículo 360. Cuatro contratos: solo escribe la nota (ni nombre, ni
/// modelo, ni matrícula), exige que el vehículo esté en el alcance de quien guarda con el mismo «no encontrado» que
/// <c>EditarVehiculoCommand</c>, no pisa lo que otra persona guardó entre medias, y solo la ejecuta un rol con
/// escritura.
/// </summary>
public class GuardarNotaInternaVehiculoCommandTests
{
    private static Vehiculo CamionGrua() => Vehiculo.DeEmpresa(Guid.NewGuid(), "Camión grúa", "Iveco Daily", "9012 GHI");

    private static (GuardarNotaInternaVehiculoCommandHandler Handler, UnitOfWorkFalso UnitOfWork) Montar(
        Vehiculo? vehiculo, AlcanceDatosServiceFalso? alcance = null)
    {
        var repositorio = new VehiculoRepositorioFalso();
        if (vehiculo is not null)
            repositorio.Agregar(vehiculo);
        var unitOfWork = new UnitOfWorkFalso();
        return (new GuardarNotaInternaVehiculoCommandHandler(repositorio, alcance ?? new AlcanceDatosServiceFalso(), unitOfWork), unitOfWork);
    }

    [Fact]
    public async Task Guarda_la_nota_y_no_toca_nombre_modelo_ni_matricula()
    {
        var vehiculo = CamionGrua();
        var (handler, unitOfWork) = Montar(vehiculo);

        var resultado = await handler.Handle(
            new GuardarNotaInternaVehiculoCommand(vehiculo.Id, "Aparca en la nave 2.\nLas llaves las tiene Leire.", vehiculo.Version),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        vehiculo.Notas.Should().Be("Aparca en la nave 2.\nLas llaves las tiene Leire.");
        vehiculo.Nombre.Should().Be("Camión grúa");
        vehiculo.Modelo.Should().Be("Iveco Daily");
        vehiculo.NumeroPlaca.Should().Be("9012 GHI");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData(null)]
    public async Task Vacia_o_solo_espacios_deja_el_vehiculo_sin_nota(string? notas)
    {
        var vehiculo = CamionGrua();
        vehiculo.FijarNotaInterna("Nota anterior.");
        var (handler, unitOfWork) = Montar(vehiculo);

        var resultado = await handler.Handle(
            new GuardarNotaInternaVehiculoCommand(vehiculo.Id, notas, vehiculo.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        vehiculo.Notas.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Falla_cuando_el_vehiculo_no_existe()
    {
        var (handler, unitOfWork) = Montar(vehiculo: null);

        var resultado = await handler.Handle(
            new GuardarNotaInternaVehiculoCommand(Guid.NewGuid(), "Nota.", Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Vehiculo.NoEncontrado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>Un vehículo fuera del alcance de quien guarda responde lo mismo que uno que no existe, y su nota no cambia.</summary>
    [Fact]
    public async Task Fuera_del_alcance_no_guarda_y_responde_no_encontrado()
    {
        var vehiculo = CamionGrua();
        vehiculo.FijarNotaInterna("Nota del equipo.");
        var (handler, unitOfWork) = Montar(vehiculo, new AlcanceDatosServiceFalso(vehiculoIdsVisibles: [Guid.NewGuid()]));

        var resultado = await handler.Handle(
            new GuardarNotaInternaVehiculoCommand(vehiculo.Id, "Nota de quien no lo alcanza.", vehiculo.Version), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Vehiculo.NoEncontrado");
        vehiculo.Notas.Should().Be("Nota del equipo.");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_una_version_anterior_responde_conflicto_y_no_pisa_la_nota()
    {
        var vehiculo = CamionGrua();
        vehiculo.FijarNotaInterna("Lo que guardó otra persona.");
        var (handler, unitOfWork) = Montar(vehiculo);
        var versionVieja = Guid.NewGuid();

        var resultado = await handler.Handle(
            new GuardarNotaInternaVehiculoCommand(vehiculo.Id, "Lo mío.", versionVieja), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        vehiculo.Notas.Should().Be("Lo que guardó otra persona.");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    // ── Validador ─────────────────────────────────────────────────────────

    [Fact]
    public void El_validador_admite_la_nota_del_tamano_maximo_y_la_nota_ausente()
    {
        var validador = new GuardarNotaInternaVehiculoCommandValidator();

        validador.Validate(new GuardarNotaInternaVehiculoCommand(Guid.NewGuid(), new string('a', Vehiculo.LongitudMaximaNotas), Guid.NewGuid()))
            .IsValid.Should().BeTrue();
        validador.Validate(new GuardarNotaInternaVehiculoCommand(Guid.NewGuid(), null, Guid.NewGuid())).IsValid.Should().BeTrue();
    }

    [Fact]
    public void El_validador_rechaza_una_nota_mas_larga_que_el_maximo_y_lo_dice_en_el_campo()
    {
        var validador = new GuardarNotaInternaVehiculoCommandValidator();

        var resultado = validador.Validate(
            new GuardarNotaInternaVehiculoCommand(Guid.NewGuid(), new string('a', Vehiculo.LongitudMaximaNotas + 1), Guid.NewGuid()));

        resultado.IsValid.Should().BeFalse();
        resultado.Errors.Should().ContainSingle()
            .Which.Should().Match<FluentValidation.Results.ValidationFailure>(e =>
                e.PropertyName == nameof(GuardarNotaInternaVehiculoCommand.Notas)
                && e.ErrorMessage == $"La nota interna no puede superar {Vehiculo.LongitudMaximaNotas} caracteres.");
    }

    [Fact]
    public void El_validador_exige_el_identificador()
    {
        var validador = new GuardarNotaInternaVehiculoCommandValidator();

        validador.Validate(new GuardarNotaInternaVehiculoCommand(Guid.Empty, "Nota.", Guid.NewGuid())).IsValid.Should().BeFalse();
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
        var behavior = new AutorizacionEscrituraBehavior<GuardarNotaInternaVehiculoCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), new SinSesionPrivilegiada(), new SinTenant());
        var llego = false;

        var resultado = await behavior.Handle(
            new GuardarNotaInternaVehiculoCommand(Guid.NewGuid(), "Nota.", Guid.NewGuid()),
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
