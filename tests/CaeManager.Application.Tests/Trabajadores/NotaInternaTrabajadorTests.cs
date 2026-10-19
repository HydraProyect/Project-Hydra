using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Trabajadores.Commands.GuardarNotaInternaTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

/// <summary>
/// La «Nota interna» de Trabajador 360, gemela de la de Empresa 360: la orden que la guarda (solo la nota, alcance del
/// Trabajador con el mismo «no encontrado», concurrencia optimista, rol con escritura y nunca sobre un anonimizado) y la
/// lectura (solo viaja a quien la lee según <see cref="IPoliticaNotaInterna"/>).
/// </summary>
public class NotaInternaTrabajadorTests
{
    private static Trabajador Paula() =>
        Trabajador.DeEmpresa(Guid.NewGuid(), "Paula", "Campos", "12345678Z", observaciones: "Observación pública");

    private static (GuardarNotaInternaTrabajadorCommandHandler Handler, UnitOfWorkFalso UnitOfWork) Montar(
        Trabajador? trabajador, AlcanceDatosServiceFalso? alcance = null)
    {
        var repositorio = new TrabajadorRepositorioFalso();
        if (trabajador is not null)
            repositorio.Agregar(trabajador);
        var unitOfWork = new UnitOfWorkFalso();
        return (new GuardarNotaInternaTrabajadorCommandHandler(repositorio, alcance ?? new AlcanceDatosServiceFalso(), unitOfWork), unitOfWork);
    }

    [Fact]
    public async Task Guarda_la_nota_y_no_toca_ni_las_observaciones_ni_la_identidad()
    {
        var trabajador = Paula();
        var (handler, unitOfWork) = Montar(trabajador);

        var resultado = await handler.Handle(
            new GuardarNotaInternaTrabajadorCommand(trabajador.Id, "Prefiere turno de tarde.\nLlamar antes de las 10.", trabajador.Version),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        trabajador.Notas.Should().Be("Prefiere turno de tarde.\nLlamar antes de las 10.");
        trabajador.Observaciones.Should().Be("Observación pública");
        trabajador.Nombre.Should().Be("Paula");
        trabajador.Dni.Should().Be("12345678Z");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData(null)]
    public async Task Vacia_o_solo_espacios_deja_al_trabajador_sin_nota(string? notas)
    {
        var trabajador = Paula();
        trabajador.FijarNotaInterna("Nota anterior.");
        var (handler, _) = Montar(trabajador);

        var resultado = await handler.Handle(
            new GuardarNotaInternaTrabajadorCommand(trabajador.Id, notas, trabajador.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        trabajador.Notas.Should().BeNull();
    }

    [Fact]
    public async Task Falla_cuando_el_trabajador_no_existe()
    {
        var (handler, unitOfWork) = Montar(trabajador: null);

        var resultado = await handler.Handle(
            new GuardarNotaInternaTrabajadorCommand(Guid.NewGuid(), "Nota.", Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Trabajador.NoEncontrado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Un Gestor CAE cuya Asignación de Cartera no alcanza al Trabajador recibe lo mismo que si no existiera, y la nota
    /// no cambia.
    /// </summary>
    [Fact]
    public async Task Fuera_de_la_cartera_no_guarda_y_responde_no_encontrado()
    {
        var trabajador = Paula();
        trabajador.FijarNotaInterna("Nota del equipo.");
        var (handler, unitOfWork) = Montar(trabajador,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()]));

        var resultado = await handler.Handle(
            new GuardarNotaInternaTrabajadorCommand(trabajador.Id, "Nota de quien no lo alcanza.", trabajador.Version),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Trabajador.NoEncontrado");
        trabajador.Notas.Should().Be("Nota del equipo.");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_una_version_anterior_responde_conflicto_y_no_pisa_la_nota()
    {
        var trabajador = Paula();
        trabajador.FijarNotaInterna("Lo que guardó otra persona.");
        var (handler, unitOfWork) = Montar(trabajador);

        var resultado = await handler.Handle(
            new GuardarNotaInternaTrabajadorCommand(trabajador.Id, "Lo mío.", Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        trabajador.Notas.Should().Be("Lo que guardó otra persona.");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_trabajador_anonimizado_no_admite_nota()
    {
        var trabajador = Paula();
        trabajador.Anonimizar(DateTime.UtcNow);
        var (handler, unitOfWork) = Montar(trabajador);

        var resultado = await handler.Handle(
            new GuardarNotaInternaTrabajadorCommand(trabajador.Id, "Dato reintroducido.", trabajador.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Trabajador.Anonimizado");
        trabajador.Notas.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_admite_el_maximo_y_rechaza_uno_mas_en_el_campo()
    {
        var validador = new GuardarNotaInternaTrabajadorCommandValidator();

        validador.Validate(new GuardarNotaInternaTrabajadorCommand(Guid.NewGuid(), new string('a', Trabajador.LongitudMaximaNotas), Guid.NewGuid()))
            .IsValid.Should().BeTrue();
        validador.Validate(new GuardarNotaInternaTrabajadorCommand(Guid.NewGuid(), null, Guid.NewGuid())).IsValid.Should().BeTrue();
        validador.Validate(new GuardarNotaInternaTrabajadorCommand(Guid.Empty, "Nota.", Guid.NewGuid())).IsValid.Should().BeFalse();

        var resultado = validador.Validate(
            new GuardarNotaInternaTrabajadorCommand(Guid.NewGuid(), new string('a', Trabajador.LongitudMaximaNotas + 1), Guid.NewGuid()));

        resultado.Errors.Should().ContainSingle().Which.Should().Match<FluentValidation.Results.ValidationFailure>(e =>
            e.PropertyName == nameof(GuardarNotaInternaTrabajadorCommand.Notas)
            && e.ErrorMessage == $"La nota interna no puede superar {Trabajador.LongitudMaximaNotas} caracteres.");
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
        var behavior = new AutorizacionEscrituraBehavior<GuardarNotaInternaTrabajadorCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), new SinSesionPrivilegiada(), new SinTenant());
        var llego = false;

        var resultado = await behavior.Handle(
            new GuardarNotaInternaTrabajadorCommand(Guid.NewGuid(), "Nota.", Guid.NewGuid()),
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

    // ── Lectura: ObtenerTrabajadorPorIdQuery ──────────────────────────────

    private static (Trabajador Trabajador, ObtenerTrabajadorPorIdQueryHandler Handler) ConNota(IPoliticaNotaInterna politica)
    {
        var empresa = new Empresa("Montajes", "B12345674");
        var t = Trabajador.DeEmpresa(empresa.Id, "Paula", "Campos", "12345678Z", observaciones: "Observación pública");
        t.FijarNotaInterna("Prefiere turno de tarde.");
        var empresas = new EmpresasQueryContextFalso(); empresas.ListaEmpresas.Add(empresa);
        var trabajadores = new TrabajadoresQueryContextFalso(); trabajadores.ListaTrabajadores.Add(t);
        return (t, new ObtenerTrabajadorPorIdQueryHandler(empresas, trabajadores, new AlcanceDatosServiceFalso(), politica));
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task El_equipo_del_Tenant_recibe_la_nota(string rol)
    {
        var (t, handler) = ConNota(PoliticaNotaInternaPruebas.Con(rol));

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);

        dto!.Notas.Should().Be("Prefiere turno de tarde.");
        dto.NotaInternaVisible.Should().BeTrue();
    }

    [Theory]
    [InlineData("Cliente")]
    [InlineData("RolQueNoExiste")]
    [InlineData(null)]
    public async Task El_usuario_de_Cliente_y_un_rol_desconocido_no_reciben_la_nota(string? rol)
    {
        var (t, handler) = ConNota(PoliticaNotaInternaPruebas.Con(rol));

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);

        dto.Should().NotBeNull("el Trabajador sigue siendo visible; solo se retira la nota");
        dto!.Notas.Should().BeNull();
        dto.NotaInternaVisible.Should().BeFalse();
        dto.Observaciones.Should().Be("Observación pública", "Observaciones no es la nota interna y no cambia de regla");
    }

    [Fact]
    public async Task La_impersonacion_no_recibe_la_nota()
    {
        var (t, handler) = ConNota(
            PoliticaNotaInternaPruebas.Con("GestorCae", PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.Impersonacion)));

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);

        dto!.Notas.Should().BeNull();
        dto.NotaInternaVisible.Should().BeFalse();
    }
}
