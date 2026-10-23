using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.LecturaInbound;
using CaeManager.Application.ContratosAcceso.Queries.ObtenerLecturaInbound;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.ContratosAcceso;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.ContratosAcceso;

/// <summary>
/// Autoridad de la lectura Inbound dentro del Tenant del Cliente empresarial (Inbound M1, PR 3): rol que valida y cartera
/// sobre el Cliente empresarial del contrato, antes de llamar a la base. Lo que no pasa responde null («no encontrado») y no
/// llega al lector, así que tampoco deja traza en el Tenant de la Empresa contratista. Que la base limite el Tenant, el
/// contrato y la autorización lo prueba LecturaInboundBajoRuntimeTests.
/// </summary>
public class LecturaInboundAutoridadTests
{
    private readonly Guid _cliente = Guid.NewGuid();
    private readonly ContratosAccesoQueryContextFalso _contratos = new();
    private readonly LectorFalso _lector = new();
    private readonly ContratoAcceso _contrato;

    public LecturaInboundAutoridadTests()
    {
        _contrato = ContratoAcceso.Crear(_cliente, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), null, null);
        _contratos.ListaContratos.Add(_contrato);
    }

    private static AlcanceDatosServiceFalso Alcance(Guid? clienteVisible) =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: clienteVisible is { } id ? [id] : []);

    private ObtenerDocumentosInboundQueryHandler Documentos(string? rol, AlcanceDatosServiceFalso alcance) =>
        new(_contratos, alcance, new UsuarioConRol(rol), _lector);

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Un_rol_que_valida_con_el_Cliente_empresarial_en_cartera_llega_al_lector(string rol)
    {
        var documentos = await Documentos(rol, Alcance(_cliente))
            .Handle(new ObtenerDocumentosInboundQuery(_contrato.Id), CancellationToken.None);

        documentos.Should().ContainSingle();
        _lector.Llamadas.Should().Equal(_contrato.Id);
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    [InlineData("RolDesconocido")]
    public async Task Un_rol_que_no_valida_responde_no_encontrado_sin_llamar_al_lector(string? rol)
    {
        var documentos = await Documentos(rol, Alcance(_cliente))
            .Handle(new ObtenerDocumentosInboundQuery(_contrato.Id), CancellationToken.None);

        documentos.Should().BeNull();
        _lector.Llamadas.Should().BeEmpty("sin autoridad no se llama a la base, que dejaría traza en el Tenant ajeno");
    }

    [Fact]
    public async Task Un_contrato_cuyo_Cliente_empresarial_no_esta_en_cartera_responde_no_encontrado()
    {
        var documentos = await Documentos("GestorCae", Alcance(Guid.NewGuid()))
            .Handle(new ObtenerDocumentosInboundQuery(_contrato.Id), CancellationToken.None);

        documentos.Should().BeNull();
        _lector.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_contrato_inexistente_o_eliminado_responde_igual_que_uno_fuera_de_cartera()
    {
        var eliminado = ContratoAcceso.Crear(_cliente, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 3, 1), null, null);
        eliminado.MarcarComoEliminado(Guid.NewGuid());
        _contratos.ListaContratos.Add(eliminado);
        var handler = Documentos("Administrador", new AlcanceDatosServiceFalso(tieneAccesoTotal: true));

        (await handler.Handle(new ObtenerDocumentosInboundQuery(Guid.NewGuid()), CancellationToken.None)).Should().BeNull();
        (await handler.Handle(new ObtenerDocumentosInboundQuery(eliminado.Id), CancellationToken.None)).Should().BeNull();
        _lector.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task La_autorizacion_y_la_apertura_aplican_la_misma_autoridad()
    {
        var fueraDeCartera = Alcance(Guid.NewGuid());
        var documento = Guid.NewGuid();

        (await new ObtenerAutorizacionInboundQueryHandler(_contratos, fueraDeCartera, new UsuarioConRol("GestorCae"), _lector)
            .Handle(new ObtenerAutorizacionInboundQuery(_contrato.Id), CancellationToken.None)).Should().BeNull();
        (await new ObtenerArchivoInboundQueryHandler(_contratos, Alcance(_cliente), new UsuarioConRol("Consulta"), _lector)
            .Handle(new ObtenerArchivoInboundQuery(_contrato.Id, documento), CancellationToken.None)).Should().BeNull();
        _lector.Llamadas.Should().BeEmpty();

        // Control positivo: con autoridad, las dos llegan al lector.
        (await new ObtenerAutorizacionInboundQueryHandler(_contratos, Alcance(_cliente), new UsuarioConRol("GestorCae"), _lector)
            .Handle(new ObtenerAutorizacionInboundQuery(_contrato.Id), CancellationToken.None)).Should().NotBeNull();
        (await new ObtenerArchivoInboundQueryHandler(_contratos, Alcance(_cliente), new UsuarioConRol("GestorCae"), _lector)
            .Handle(new ObtenerArchivoInboundQuery(_contrato.Id, documento), CancellationToken.None))!.DocumentoId.Should().Be(documento);
        _lector.Llamadas.Should().Equal(_contrato.Id, _contrato.Id);
    }

    private sealed class LectorFalso : ILectorDocumentosInbound
    {
        public List<Guid> Llamadas { get; } = [];

        public Task<AutorizacionInboundDto?> AutorizacionDelContratoAsync(Guid contratoAccesoId, CancellationToken cancellationToken = default)
        {
            Llamadas.Add(contratoAccesoId);
            return Task.FromResult<AutorizacionInboundDto?>(new(Guid.NewGuid(), new DateOnly(2026, 3, 1), null, false, true, 1));
        }

        public Task<IReadOnlyList<DocumentoInboundDto>> DocumentosAutorizadosAsync(Guid contratoAccesoId, CancellationToken cancellationToken = default)
        {
            Llamadas.Add(contratoAccesoId);
            return Task.FromResult<IReadOnlyList<DocumentoInboundDto>>(
                [new(Guid.NewGuid(), null, null, null, null, "Tipo", new DateOnly(2026, 3, 1), null, EstadoVigenciaDocumento.SinConfirmar)]);
        }

        public Task<ArchivoInboundDto?> ArchivoAutorizadoAsync(Guid contratoAccesoId, Guid documentoId, CancellationToken cancellationToken = default)
        {
            Llamadas.Add(contratoAccesoId);
            return Task.FromResult<ArchivoInboundDto?>(new(documentoId, "archivo"));
        }
    }

    private sealed class UsuarioConRol(string? rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
