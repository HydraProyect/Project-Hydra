using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.GuardarNotaInternaEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;
using EmpresaRepositorioFalso = CaeManager.Application.Tests.Clientes.EmpresaRepositorioFalso;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// La orden que guarda la «Nota interna» de Empresa 360. Cuatro contratos: solo escribe la nota (ni identidad ni
/// discriminadores), exige alcance de gestión con el mismo «no encontrada» que <c>EditarEmpresaCommand</c>, no pisa lo
/// que otra persona guardó entre medias, y solo la ejecuta un rol con escritura.
/// </summary>
public class GuardarNotaInternaEmpresaCommandTests
{
    private static Empresa Montajes() => new("Montajes Skynet S.L.");

    private static (GuardarNotaInternaEmpresaCommandHandler Handler, UnitOfWorkFalso UnitOfWork) Montar(
        Empresa? empresa, AlcanceDatosServiceFalso? alcance = null)
    {
        var repositorio = new EmpresaRepositorioFalso();
        if (empresa is not null)
            repositorio.Agregar(empresa);
        var unitOfWork = new UnitOfWorkFalso();
        return (new GuardarNotaInternaEmpresaCommandHandler(repositorio, alcance ?? new AlcanceDatosServiceFalso(), unitOfWork), unitOfWork);
    }

    [Fact]
    public async Task Guarda_la_nota_y_no_toca_la_identidad()
    {
        var empresa = Montajes();
        var (handler, unitOfWork) = Montar(empresa);

        var resultado = await handler.Handle(
            new GuardarNotaInternaEmpresaCommand(empresa.Id, "Llamar antes de las 10.\nPreguntar por Leire.", empresa.Version),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        empresa.Notas.Should().Be("Llamar antes de las 10.\nPreguntar por Leire.");
        empresa.RazonSocial.Should().Be("Montajes Skynet S.L.");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData(null)]
    public async Task Vacia_o_solo_espacios_deja_la_empresa_sin_nota(string? notas)
    {
        var empresa = Montajes();
        empresa.FijarNotaInterna("Nota anterior.");
        var (handler, unitOfWork) = Montar(empresa);

        var resultado = await handler.Handle(new GuardarNotaInternaEmpresaCommand(empresa.Id, notas, empresa.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        empresa.Notas.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Falla_cuando_la_empresa_no_existe()
    {
        var (handler, unitOfWork) = Montar(empresa: null);

        var resultado = await handler.Handle(
            new GuardarNotaInternaEmpresaCommand(Guid.NewGuid(), "Nota.", Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>Quien tiene la Empresa en su cartera de lectura pero no en la de gestión no escribe su nota.</summary>
    [Fact]
    public async Task Sin_alcance_de_gestion_no_guarda_y_responde_no_encontrada()
    {
        var empresa = Montajes();
        empresa.FijarNotaInterna("Nota del equipo.");
        var (handler, unitOfWork) = Montar(
            empresa,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id], empresaIdsParaGestion: []));

        var resultado = await handler.Handle(
            new GuardarNotaInternaEmpresaCommand(empresa.Id, "Nota de quien solo lee.", empresa.Version), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
        empresa.Notas.Should().Be("Nota del equipo.");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_una_version_anterior_responde_conflicto_y_no_pisa_la_nota()
    {
        var empresa = Montajes();
        empresa.FijarNotaInterna("Lo que guardó otra persona.");
        var (handler, unitOfWork) = Montar(empresa);

        var resultado = await handler.Handle(
            new GuardarNotaInternaEmpresaCommand(empresa.Id, "Lo mío.", Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        empresa.Notas.Should().Be("Lo que guardó otra persona.");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_admite_el_maximo_y_rechaza_una_nota_mas_larga()
    {
        var validador = new GuardarNotaInternaEmpresaCommandValidator();

        validador.Validate(new GuardarNotaInternaEmpresaCommand(Guid.NewGuid(), new string('a', Empresa.LongitudMaximaNotas), Guid.NewGuid()))
            .IsValid.Should().BeTrue();
        validador.Validate(new GuardarNotaInternaEmpresaCommand(Guid.NewGuid(), null, Guid.NewGuid())).IsValid.Should().BeTrue();

        var resultado = validador.Validate(
            new GuardarNotaInternaEmpresaCommand(Guid.NewGuid(), new string('a', Empresa.LongitudMaximaNotas + 1), Guid.NewGuid()));
        resultado.IsValid.Should().BeFalse();
        resultado.Errors.Should().ContainSingle().Which.PropertyName.Should().Be(nameof(GuardarNotaInternaEmpresaCommand.Notas));

        validador.Validate(new GuardarNotaInternaEmpresaCommand(Guid.Empty, "Nota.", Guid.NewGuid())).IsValid.Should().BeFalse();
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
        var behavior = new AutorizacionEscrituraBehavior<GuardarNotaInternaEmpresaCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), new SinSesionPrivilegiada(), new SinTenant());
        var llego = false;

        var resultado = await behavior.Handle(
            new GuardarNotaInternaEmpresaCommand(Guid.NewGuid(), "Nota.", Guid.NewGuid()),
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

    /// <summary>
    /// Quien puede guardar la nota tiene que haberla visto: un rol de escritura que la lectura no entregue abriría el
    /// editor vacío y «Guardar» borraría una nota que nadie le enseñó. Se mide con la lista real de escritura.
    /// </summary>
    [Fact]
    public async Task Todo_rol_que_puede_guardar_la_nota_de_la_empresa_la_recibe_al_leer()
    {
        var rolesConEscritura = AutorizacionEscrituraBehavior<GuardarNotaInternaEmpresaCommand, Result>.RolesConEscritura;
        rolesConEscritura.Should().NotBeEmpty("con la lista real vacía, el bucle de abajo no mediría nada");

        foreach (var rol in rolesConEscritura)
        {
            var empresa = Montajes();
            empresa.FijarNotaInterna("Nota.");
            var empresas = new EmpresasQueryContextFalso();
            empresas.ListaEmpresas.Add(empresa);
            var handler = new ObtenerEmpresaPorIdQueryHandler(
                empresas, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), rol));

            var dto = await handler.Handle(new ObtenerEmpresaPorIdQuery(empresa.Id), CancellationToken.None);

            dto!.NotaInternaVisible.Should().BeTrue($"{rol} puede guardar la nota");
            dto.Notas.Should().Be("Nota.");
        }
    }
}
