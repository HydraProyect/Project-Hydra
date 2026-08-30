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
            repositorio, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarDocumentoCommand(documento.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        documento.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_el_documento_no_existe()
    {
        var repositorio = new DocumentoRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarDocumentoCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarDocumentoCommand(Guid.NewGuid()), CancellationToken.None);

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
        var handler = new EliminarDocumentoCommandHandler(repositorio, alcance, new ProyectosQueryContextFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarDocumentoCommand(documento.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.NoEncontrado");
        documento.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_borra_nada_sin_identidad_resuelta()
    {
        // El llamador de Web pasaba `usuarioId ?? Guid.Empty`: sin sesión
        // resuelta, el borrado quedaba atribuido a nadie y la auditoría decía
        // que alguien lo hizo. Ahora se aborta, y sobre todo NO se marca el
        // documento: un borrado sin autor no es medio correcto, es inservible.
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), null);
        var repositorio = new DocumentoRepositorioFalso();
        repositorio.Agregar(documento);
        var handler = new EliminarDocumentoCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
            new UnitOfWorkFalso(), new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarDocumentoCommand(documento.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.SinIdentidad");
        documento.EstaEliminado.Should().BeFalse();
    }
}
