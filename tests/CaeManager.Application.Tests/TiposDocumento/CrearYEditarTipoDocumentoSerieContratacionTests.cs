using CaeManager.Application.TiposDocumento.Commands.CrearTipoDocumento;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.TiposDocumento;

/// <summary>
/// La serie documental por tipo de contratación se fija desde el alta y la edición del Tipo de documento: sin
/// valor, el alta pide de serie a todos y la edición conserva la que había; fuera del Ámbito Trabajador no se toca.
/// </summary>
public class CrearYEditarTipoDocumentoSerieContratacionTests
{
    private const TiposContratacion SinVoluntario = TiposContratacion.CuentaAjena | TiposContratacion.Autonomo;

    private static CrearTipoDocumentoCommand ComandoCrear(AmbitoAplicacion ambito, TiposContratacion? serie) =>
        new("Aptitud médica", 12, true, 1, ambito, RequisitoDocumental.Si, NaturalezaJuridica.ObligacionLegal,
            null, null, null, null, null, [], null, serie);

    private static EditarTipoDocumentoCommand ComandoEditar(Guid id, TiposContratacion? serie) =>
        new(id, "Aptitud médica", 12, true, 1, RequisitoDocumental.Si, NaturalezaJuridica.ObligacionLegal,
            null, null, null, null, null, [], null, serie);

    private static CrearTipoDocumentoCommandHandler HandlerCrear(TipoDocumentoRepositorioFalso repositorio) =>
        new(repositorio, new TipoDocumentoCentroRepositorioFalso(), new CentrosQueryContextFalso(), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

    private static EditarTipoDocumentoCommandHandler HandlerEditar(TipoDocumentoRepositorioFalso repositorio) =>
        new(repositorio, new TipoDocumentoCentroRepositorioFalso(), new CentrosQueryContextFalso(),
            new AltaAcreditacionesPlataformaServiceFalso(), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

    [Fact]
    public async Task Crear_sin_serie_pide_de_serie_a_todos()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();

        (await HandlerCrear(repositorio).Handle(ComandoCrear(AmbitoAplicacion.Trabajador, null), CancellationToken.None))
            .EsExitoso.Should().BeTrue();

        repositorio.Tipos.Single().TiposContratacionDeSerie.Should().Be(TiposContratacion.Todos);
    }

    [Fact]
    public async Task Crear_con_serie_sin_voluntario_la_guarda()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();

        await HandlerCrear(repositorio).Handle(ComandoCrear(AmbitoAplicacion.Trabajador, SinVoluntario), CancellationToken.None);

        repositorio.Tipos.Single().TiposContratacionDeSerie.Should().Be(SinVoluntario);
    }

    [Fact]
    public async Task Crear_fuera_del_ambito_trabajador_ignora_la_serie()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();

        await HandlerCrear(repositorio).Handle(ComandoCrear(AmbitoAplicacion.Empresa, TiposContratacion.Voluntario), CancellationToken.None);

        repositorio.Tipos.Single().TiposContratacionDeSerie.Should().Be(TiposContratacion.Todos);
    }

    [Fact]
    public async Task Editar_con_serie_la_reemplaza_y_sin_serie_la_conserva()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = new TipoDocumento("Aptitud médica", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        repositorio.Agregar(tipo);

        (await HandlerEditar(repositorio).Handle(ComandoEditar(tipo.Id, SinVoluntario), CancellationToken.None))
            .EsExitoso.Should().BeTrue();
        tipo.TiposContratacionDeSerie.Should().Be(SinVoluntario);

        (await HandlerEditar(repositorio).Handle(ComandoEditar(tipo.Id, null), CancellationToken.None))
            .EsExitoso.Should().BeTrue();
        tipo.TiposContratacionDeSerie.Should().Be(SinVoluntario);
    }

    [Fact]
    public void Los_validadores_rechazan_una_serie_vacia()
    {
        new CrearTipoDocumentoCommandValidator()
            .Validate(ComandoCrear(AmbitoAplicacion.Trabajador, TiposContratacion.Ninguno)).IsValid.Should().BeFalse();
        new EditarTipoDocumentoCommandValidator()
            .Validate(ComandoEditar(Guid.NewGuid(), TiposContratacion.Ninguno)).IsValid.Should().BeFalse();
        new EditarTipoDocumentoCommandValidator()
            .Validate(ComandoEditar(Guid.NewGuid(), null)).IsValid.Should().BeTrue();
    }
}
