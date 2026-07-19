using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Trabajadores.Commands.ResolverDeteccionNuevo;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

public class ResolverDeteccionNuevoCommandHandlerTests
{
    private static ResolverDeteccionNuevoCommandHandler CrearHandler(
        DeteccionTrabajadorRepositorioFalso deteccionRepositorio, TrabajadorRepositorioFalso trabajadorRepositorio, UnitOfWorkFalso unitOfWork) =>
        new(deteccionRepositorio, trabajadorRepositorio, new AlcanceDatosServiceFalso(), unitOfWork);

    [Fact]
    public async Task Crear_da_de_alta_al_trabajador_y_resuelve_la_deteccion()
    {
        var empresaId = Guid.NewGuid();
        var deteccion = DeteccionTrabajador.Nuevo(Guid.NewGuid(), empresaId, "Ana", "Lopez Diaz", "77189989B");

        var deteccionRepositorio = new DeteccionTrabajadorRepositorioFalso();
        deteccionRepositorio.Agregar(deteccion);
        var trabajadorRepositorio = new TrabajadorRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(deteccionRepositorio, trabajadorRepositorio, unitOfWork);

        var resultado = await handler.Handle(new ResolverDeteccionNuevoCommand(deteccion.Id, Crear: true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        trabajadorRepositorio.Trabajadores.Should().ContainSingle(t => t.Dni == "77189989B" && t.EmpresaId == empresaId);
        deteccion.Resuelta.Should().BeTrue();
        deteccion.AccionTomada.Should().Be("Creado");
    }

    [Fact]
    public async Task Sin_crear_solo_descarta_la_deteccion()
    {
        var deteccion = DeteccionTrabajador.Nuevo(Guid.NewGuid(), Guid.NewGuid(), "Ana", "Lopez Diaz", "77189989B");
        var deteccionRepositorio = new DeteccionTrabajadorRepositorioFalso();
        deteccionRepositorio.Agregar(deteccion);
        var trabajadorRepositorio = new TrabajadorRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(deteccionRepositorio, trabajadorRepositorio, unitOfWork);

        var resultado = await handler.Handle(new ResolverDeteccionNuevoCommand(deteccion.Id, Crear: false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        trabajadorRepositorio.Trabajadores.Should().BeEmpty();
        deteccion.AccionTomada.Should().Be("Descartado");
    }

    [Fact]
    public async Task Falla_si_ya_existe_un_trabajador_con_ese_dni()
    {
        var empresaId = Guid.NewGuid();
        var deteccion = DeteccionTrabajador.Nuevo(Guid.NewGuid(), empresaId, "Ana", "Lopez Diaz", "77189989B");
        var deteccionRepositorio = new DeteccionTrabajadorRepositorioFalso();
        deteccionRepositorio.Agregar(deteccion);
        var trabajadorRepositorio = new TrabajadorRepositorioFalso();
        trabajadorRepositorio.Agregar(Trabajador.DeEmpresa(empresaId, "Otro", "Ya Existente", "77189989B"));
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(deteccionRepositorio, trabajadorRepositorio, unitOfWork);

        var resultado = await handler.Handle(new ResolverDeteccionNuevoCommand(deteccion.Id, Crear: true), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Deteccion.DniDuplicado");
        deteccion.Resuelta.Should().BeFalse();
    }

    [Fact]
    public async Task Falla_si_la_deteccion_ya_estaba_resuelta()
    {
        var deteccion = DeteccionTrabajador.Nuevo(Guid.NewGuid(), Guid.NewGuid(), "Ana", "Lopez Diaz", "77189989B");
        deteccion.Resolver("Descartado");
        var deteccionRepositorio = new DeteccionTrabajadorRepositorioFalso();
        deteccionRepositorio.Agregar(deteccion);
        var handler = CrearHandler(deteccionRepositorio, new TrabajadorRepositorioFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ResolverDeteccionNuevoCommand(deteccion.Id, Crear: true), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Deteccion.YaResuelta");
    }
}
