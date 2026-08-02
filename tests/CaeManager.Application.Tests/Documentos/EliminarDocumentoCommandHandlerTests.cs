using CaeManager.Application.Documentos.Commands.EliminarDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class EliminarDocumentoCommandHandlerTests
{
    [Fact]
    public async Task Marca_el_documento_como_eliminado()
    {
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), null);
        var repositorio = new DocumentoRepositorioFalso();
        repositorio.Agregar(documento);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarDocumentoCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), unitOfWork);

        var resultado = await handler.Handle(new EliminarDocumentoCommand(documento.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        documento.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_el_documento_no_existe()
    {
        var repositorio = new DocumentoRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarDocumentoCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), unitOfWork);

        var resultado = await handler.Handle(new EliminarDocumentoCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.NoEncontrado");
    }

    [Fact]
    public async Task Falla_cuando_el_documento_es_de_un_cliente_fuera_de_la_cartera()
    {
        var clienteAjeno = Guid.NewGuid();
        var documento = Documento.DeCliente(clienteAjeno, Guid.NewGuid(), new DateOnly(2026, 1, 1), null);
        var repositorio = new DocumentoRepositorioFalso();
        repositorio.Agregar(documento);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);
        var handler = new EliminarDocumentoCommandHandler(repositorio, alcance, new ProyectosQueryContextFalso(), unitOfWork);

        var resultado = await handler.Handle(new EliminarDocumentoCommand(documento.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.NoEncontrado");
        documento.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
