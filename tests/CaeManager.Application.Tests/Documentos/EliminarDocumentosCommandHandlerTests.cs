using CaeManager.Application.Documentos.Commands.EliminarDocumentos;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class EliminarDocumentosCommandHandlerTests
{
    [Fact]
    public async Task Elimina_todos_los_documentos_existentes()
    {
        var uno = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), null);
        var dos = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), null);
        var repositorio = new DocumentoRepositorioFalso();
        repositorio.Agregar(uno);
        repositorio.Agregar(dos);
        var unitOfWork = new Clientes.UnitOfWorkFalso();
        var handler = new EliminarDocumentosCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(new EliminarDocumentosCommand([uno.Id, dos.Id], Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(2);
        resultado.Valor.Errores.Should().BeEmpty();
        uno.EstaEliminado.Should().BeTrue();
        dos.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Reporta_error_por_cada_id_inexistente_sin_fallar_el_resto()
    {
        var existente = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), null);
        var repositorio = new DocumentoRepositorioFalso();
        repositorio.Agregar(existente);
        var unitOfWork = new Clientes.UnitOfWorkFalso();
        var handler = new EliminarDocumentosCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(
            new EliminarDocumentosCommand([existente.Id, Guid.NewGuid()], Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle();
        existente.EstaEliminado.Should().BeTrue();
    }
}
