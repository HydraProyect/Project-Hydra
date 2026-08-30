using CaeManager.Application.Subcontratas.Commands.EliminarVerificacionExterna;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

public class EliminarVerificacionExternaSubcontrataCommandHandlerTests
{
    private static VerificacionExternaSubcontrata CrearVerificacion(Guid subcontrataId) => new(
        subcontrataId, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1),
        ResultadoVerificacionExterna.Valido, Guid.NewGuid());

    [Fact]
    public async Task Marca_la_verificacion_como_eliminada()
    {
        var verificacion = CrearVerificacion(Guid.NewGuid());
        var repositorio = new VerificacionExternaSubcontrataRepositorioFalso();
        repositorio.Agregar(verificacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVerificacionExternaSubcontrataCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarVerificacionExternaSubcontrataCommand(verificacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        verificacion.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_la_verificacion_no_existe()
    {
        var repositorio = new VerificacionExternaSubcontrataRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVerificacionExternaSubcontrataCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarVerificacionExternaSubcontrataCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificacionExterna.NoEncontrada");
    }

    [Fact]
    public async Task Falla_sin_borrar_cuando_no_hay_identidad_resuelta()
    {
        // Auditoría Módulo 5, hallazgo crítico 7/9: sin ICurrentUserService
        // resuelto, el borrado se aborta — nunca se atribuye a Guid.Empty.
        var verificacion = CrearVerificacion(Guid.NewGuid());
        var repositorio = new VerificacionExternaSubcontrataRepositorioFalso();
        repositorio.Agregar(verificacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVerificacionExternaSubcontrataCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarVerificacionExternaSubcontrataCommand(verificacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VerificacionExterna.SinIdentidad");
        verificacion.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
