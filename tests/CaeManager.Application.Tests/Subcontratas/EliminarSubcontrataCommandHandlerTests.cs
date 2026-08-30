using CaeManager.Application.Subcontratas.Commands.EliminarSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

public class EliminarSubcontrataCommandHandlerTests
{
    [Fact]
    public async Task Marca_la_subcontrata_como_eliminada_cuando_no_tiene_trabajadores()
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Andamios del Sur S.L.", "B12345674", "Gestionada");
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(subcontrata);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarSubcontrataCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarSubcontrataCommand(subcontrata.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        subcontrata.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_la_subcontrata_no_existe()
    {
        var repositorio = new EmpresaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarSubcontrataCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarSubcontrataCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Subcontrata.NoEncontrada");
    }

    [Fact]
    public async Task Falla_cuando_la_subcontrata_tiene_trabajadores()
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Andamios del Sur S.L.", "B12345674", "Gestionada");
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(subcontrata);
        repositorio.IdsConTrabajadoresComoSubcontrata.Add(subcontrata.Id);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarSubcontrataCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarSubcontrataCommand(subcontrata.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Subcontrata.TieneTrabajadores");
        subcontrata.EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Falla_sin_borrar_cuando_no_hay_identidad_resuelta()
    {
        // Auditoría Módulo 5, hallazgo crítico 7/9: sin ICurrentUserService
        // resuelto, el borrado se aborta — nunca se atribuye a Guid.Empty.
        var subcontrata = Empresa.CrearComoSubcontrata("Andamios del Sur S.L.", "B12345674", "Gestionada");
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(subcontrata);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarSubcontrataCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarSubcontrataCommand(subcontrata.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Subcontrata.SinIdentidad");
        subcontrata.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
