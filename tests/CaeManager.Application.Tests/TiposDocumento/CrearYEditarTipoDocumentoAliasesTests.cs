using CaeManager.Application.TiposDocumento.Commands.CrearTipoDocumento;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.TiposDocumento;

/// <summary>
/// Cubre la precondición de la limpieza de nombres del catálogo (taxonomía
/// documental CAE §2bis): el campo de alias, sin el cual la única forma de
/// encontrar "TC2" era que estuviera escrito dentro del nombre.
/// </summary>
public class CrearYEditarTipoDocumentoAliasesTests
{
    private static CrearTipoDocumentoCommand ComandoCrear(IReadOnlyList<string>? aliases) =>
        new("Relación Nominal de Trabajadores", null, false, 1, AmbitoAplicacion.Empresa,
            RequisitoDocumental.No, NaturalezaJuridica.RequisitoCliente,
            null, null, null, null, null, [], aliases);

    [Fact]
    public async Task Crear_guarda_los_alias_indicados()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var handler = new CrearTipoDocumentoCommandHandler(
            repositorio, new TipoDocumentoCentroRepositorioFalso(), new CentrosQueryContextFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoCrear(["TC2", "RNT"]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var creado = repositorio.Tipos.Single();
        creado.Aliases.Select(a => a.Texto).Should().BeEquivalentTo(["TC2", "RNT"]);
    }

    [Fact]
    public async Task Crear_sin_alias_no_falla()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var handler = new CrearTipoDocumentoCommandHandler(
            repositorio, new TipoDocumentoCentroRepositorioFalso(), new CentrosQueryContextFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(ComandoCrear(null), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        repositorio.Tipos.Single().Aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task Editar_reemplaza_el_conjunto_de_alias()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = new TipoDocumento("Relación Nominal de Trabajadores", null, false, 1, AmbitoAplicacion.Empresa);
        tipo.EstablecerAliases(["TC2"]);
        repositorio.Agregar(tipo);
        var handler = new EditarTipoDocumentoCommandHandler(
            repositorio, new TipoDocumentoCentroRepositorioFalso(), new CentrosQueryContextFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new EditarTipoDocumentoCommand(
                tipo.Id, tipo.Nombre, null, false, 1,
                RequisitoDocumental.No, NaturalezaJuridica.RequisitoCliente,
                null, null, null, null, null, [], ["RNT"]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        tipo.Aliases.Select(a => a.Texto).Should().BeEquivalentTo(["RNT"]);
    }
}
