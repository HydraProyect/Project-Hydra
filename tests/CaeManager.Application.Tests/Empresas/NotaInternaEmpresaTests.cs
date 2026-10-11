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

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// La «Nota interna» de Empresa 360 (<c>Empresa.Notas</c>, una sola por Empresa). Lectura: solo viaja a quien opera
/// desde el lado de gestión; el usuario de Cliente (portal) recibe la ficha básica sin la nota y sin la tarjeta.
/// Escritura: <see cref="GuardarNotaInternaEmpresaCommand"/> solo escribe la nota, exige alcance de gestión, no pisa
/// lo que otra persona guardó y solo la ejecuta un rol con escritura. Qué responde el alcance real por rol lo mide
/// <c>NotaInternaDeEmpresaPorRolTests</c>, y el otro Tenant <c>NotaInternaDeEmpresaBajoRlsTests</c> (integración).
/// Guardar la nota renueva <c>Version</c>: un formulario de Empresa, Cliente o Subcontrata abierto antes dará conflicto.
/// </summary>
public class NotaInternaEmpresaTests
{
    private const string Nota = "Llamar antes de las 10.";

    private static Empresa Contratista()
    {
        var empresa = Empresa.CrearComoCliente("Montajes Repro S.L.", "B10380392", false, null, null);
        empresa.FijarNotaInterna(Nota);
        return empresa;
    }

    // ── Lectura ───────────────────────────────────────────────────────────

    private static Task<EmpresaDetalleDto?> LeerAsync(Empresa empresa, AlcanceDatosServiceFalso alcance)
    {
        var contexto = new EmpresasQueryContextFalso();
        contexto.ListaEmpresas.Add(empresa);
        return new ObtenerEmpresaPorIdQueryHandler(contexto, alcance)
            .Handle(new ObtenerEmpresaPorIdQuery(empresa.Id), CancellationToken.None);
    }

    [Fact]
    public async Task El_usuario_de_Cliente_recibe_la_ficha_sin_la_nota_ni_la_tarjeta()
    {
        var empresa = Contratista();
        var portal = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id], ladoDeGestion: false);

        var detalle = await LeerAsync(empresa, portal);

        detalle.Should().NotBeNull("la ficha básica está en su alcance de lectura");
        detalle!.Notas.Should().BeNull();
        detalle.NotaInternaVisible.Should().BeFalse();
    }

    [Fact]
    public async Task El_lado_de_gestion_recibe_la_nota_y_la_tarjeta()
    {
        var empresa = Contratista();
        var gestion = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id]);

        var detalle = await LeerAsync(empresa, gestion);

        detalle!.Notas.Should().Be(Nota);
        detalle.NotaInternaVisible.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_nota_el_lado_de_gestion_ve_la_tarjeta_vacia()
    {
        var empresa = Empresa.CrearComoCliente("Sin Nota S.L.", "B10380392", false, null, null);
        var gestion = new AlcanceDatosServiceFalso(tieneAccesoTotal: true);

        var detalle = await LeerAsync(empresa, gestion);

        detalle!.Notas.Should().BeNull();
        detalle.NotaInternaVisible.Should().BeTrue();
    }

    [Fact]
    public async Task Fuera_del_alcance_de_lectura_no_hay_ficha()
    {
        var empresa = Contratista();

        (await LeerAsync(empresa, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: []))).Should().BeNull();
    }

    // ── Escritura ─────────────────────────────────────────────────────────

    private static (GuardarNotaInternaEmpresaCommandHandler Handler, UnitOfWorkFalso UnitOfWork) Montar(
        Empresa? empresa, AlcanceDatosServiceFalso? alcance = null)
    {
        var repositorio = new Clientes.EmpresaRepositorioFalso();
        if (empresa is not null)
            repositorio.Agregar(empresa);
        var unitOfWork = new UnitOfWorkFalso();
        return (new GuardarNotaInternaEmpresaCommandHandler(repositorio, alcance ?? new AlcanceDatosServiceFalso(), unitOfWork), unitOfWork);
    }

    [Fact]
    public async Task Guarda_la_nota_y_no_toca_la_identidad()
    {
        var empresa = Contratista();
        var (handler, unitOfWork) = Montar(empresa);

        var resultado = await handler.Handle(
            new GuardarNotaInternaEmpresaCommand(empresa.Id, "Preguntar por Leire.\nSolo mañanas.", empresa.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        empresa.Notas.Should().Be("Preguntar por Leire.\nSolo mañanas.");
        empresa.RazonSocial.Should().Be("Montajes Repro S.L.");
        empresa.Cif.Should().Be("B10380392");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData(null)]
    public async Task Vacia_o_solo_espacios_deja_la_empresa_sin_nota(string? notas)
    {
        var empresa = Contratista();
        var (handler, _) = Montar(empresa);

        var resultado = await handler.Handle(new GuardarNotaInternaEmpresaCommand(empresa.Id, notas, empresa.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        empresa.Notas.Should().BeNull();
    }

    /// <summary>
    /// Una Empresa de otro Tenant no la devuelve el repositorio (filtro de Tenant y RLS): el handler la ve igual que
    /// una que no existe. Que la base la oculte de verdad lo mide la prueba de integración.
    /// </summary>
    [Fact]
    public async Task Una_empresa_que_el_repositorio_no_devuelve_responde_no_encontrada()
    {
        var (handler, unitOfWork) = Montar(empresa: null);

        var resultado = await handler.Handle(new GuardarNotaInternaEmpresaCommand(Guid.NewGuid(), "Nota.", Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_alcance_de_gestion_no_guarda_y_responde_no_encontrada()
    {
        var empresa = Contratista();
        var (handler, unitOfWork) = Montar(
            empresa, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id], empresaIdsParaGestion: []));

        var resultado = await handler.Handle(new GuardarNotaInternaEmpresaCommand(empresa.Id, "Otra.", empresa.Version), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
        empresa.Notas.Should().Be(Nota);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_una_version_anterior_responde_conflicto_y_no_pisa_la_nota()
    {
        var empresa = Contratista();
        var (handler, unitOfWork) = Montar(empresa);

        var resultado = await handler.Handle(new GuardarNotaInternaEmpresaCommand(empresa.Id, "Lo mío.", Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        empresa.Notas.Should().Be(Nota);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_limita_la_longitud_y_exige_el_identificador()
    {
        var validador = new GuardarNotaInternaEmpresaCommandValidator();

        validador.Validate(new GuardarNotaInternaEmpresaCommand(Guid.NewGuid(), new string('a', Empresa.LongitudMaximaNotas), Guid.NewGuid()))
            .IsValid.Should().BeTrue();
        validador.Validate(new GuardarNotaInternaEmpresaCommand(Guid.NewGuid(), new string('a', Empresa.LongitudMaximaNotas + 1), Guid.NewGuid()))
            .IsValid.Should().BeFalse();
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

    [Theory]
    [InlineData("Cliente", false)]
    [InlineData("Consulta", false)]
    [InlineData("RolQueNoExiste", false)]
    [InlineData(null, false)]
    [InlineData("Administrador", true)]
    [InlineData("DireccionCae", true)]
    [InlineData("CoordinadorCae", true)]
    [InlineData("GestorCae", true)]
    public async Task Solo_un_rol_con_escritura_llega_a_guardar_la_nota(string? rol, bool llega)
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

        llego.Should().Be(llega);
        if (!llega)
            resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
    }
}
