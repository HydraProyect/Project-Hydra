using CaeManager.Application.Tests.Asignaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

public class ResolverDeteccionAusenteCommandHandlerTests
{
    private static ResolverDeteccionAusenteCommandHandler CrearHandler(
        DeteccionTrabajadorRepositorioFalso deteccionRepositorio, TrabajadorRepositorioFalso trabajadorRepositorio,
        UnitOfWorkFalso unitOfWork, Guid? usuarioActualId = null,
        AsignacionRepositorioFalso? asignaciones = null) =>
        new(deteccionRepositorio, trabajadorRepositorio, asignaciones ?? new AsignacionRepositorioFalso(),
            new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(usuarioActualId ?? Guid.NewGuid()), unitOfWork);

    [Fact]
    public async Task Desactivar_da_de_baja_al_trabajador_y_resuelve_la_deteccion()
    {
        var empresaId = Guid.NewGuid();
        var trabajador = Trabajador.DeEmpresa(empresaId, "Pedro", "Gomez Ruiz", "77189989B");
        var deteccion = DeteccionTrabajador.Ausente(Guid.NewGuid(), empresaId, trabajador.Id, trabajador.Nombre, trabajador.Apellidos, trabajador.Dni!);

        var deteccionRepositorio = new DeteccionTrabajadorRepositorioFalso();
        deteccionRepositorio.Agregar(deteccion);
        var trabajadorRepositorio = new TrabajadorRepositorioFalso();
        trabajadorRepositorio.Agregar(trabajador);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(deteccionRepositorio, trabajadorRepositorio, unitOfWork);

        var resultado = await handler.Handle(new ResolverDeteccionAusenteCommand(deteccion.Id, Desactivar: true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        trabajador.EstaEliminado.Should().BeTrue();
        deteccion.AccionTomada.Should().Be("Desactivado");
    }

    [Fact]
    public async Task Sin_desactivar_el_trabajador_se_mantiene_activo()
    {
        var empresaId = Guid.NewGuid();
        var trabajador = Trabajador.DeEmpresa(empresaId, "Pedro", "Gomez Ruiz", "77189989B");
        var deteccion = DeteccionTrabajador.Ausente(Guid.NewGuid(), empresaId, trabajador.Id, trabajador.Nombre, trabajador.Apellidos, trabajador.Dni!);

        var deteccionRepositorio = new DeteccionTrabajadorRepositorioFalso();
        deteccionRepositorio.Agregar(deteccion);
        var trabajadorRepositorio = new TrabajadorRepositorioFalso();
        trabajadorRepositorio.Agregar(trabajador);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(deteccionRepositorio, trabajadorRepositorio, unitOfWork);

        var resultado = await handler.Handle(new ResolverDeteccionAusenteCommand(deteccion.Id, Desactivar: false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        trabajador.EstaEliminado.Should().BeFalse();
        deteccion.AccionTomada.Should().Be("Mantenido");
    }

    [Fact]
    public async Task Falla_si_la_deteccion_ya_estaba_resuelta()
    {
        var empresaId = Guid.NewGuid();
        var trabajador = Trabajador.DeEmpresa(empresaId, "Pedro", "Gomez Ruiz", "77189989B");
        var deteccion = DeteccionTrabajador.Ausente(Guid.NewGuid(), empresaId, trabajador.Id, trabajador.Nombre, trabajador.Apellidos, trabajador.Dni!);
        deteccion.Resolver("Mantenido");

        var deteccionRepositorio = new DeteccionTrabajadorRepositorioFalso();
        deteccionRepositorio.Agregar(deteccion);
        var handler = CrearHandler(deteccionRepositorio, new TrabajadorRepositorioFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ResolverDeteccionAusenteCommand(deteccion.Id, Desactivar: true), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Deteccion.YaResuelta");
    }
}
