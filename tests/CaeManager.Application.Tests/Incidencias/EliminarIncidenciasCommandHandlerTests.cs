using CaeManager.Application.Incidencias.Commands.EliminarIncidencias;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Incidencias;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Incidencias;

public class EliminarIncidenciasCommandHandlerTests
{
    private static Incidencia CrearIncidencia() =>
        new(Guid.NewGuid(), null, TipoIncidencia.Accidente, GravedadIncidencia.Leve, new DateOnly(2026, 1, 1), "Caída en almacén.");

    [Fact]
    public async Task Marca_todas_las_incidencias_del_lote_como_eliminadas()
    {
        var uno = CrearIncidencia();
        var dos = CrearIncidencia();
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Agregar(uno);
        repositorio.Agregar(dos);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarIncidenciasCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarIncidenciasCommand([uno.Id, dos.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(2);
        uno.EstaEliminado.Should().BeTrue();
        dos.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Reporta_error_parcial_cuando_una_incidencia_ya_no_existe()
    {
        var existente = CrearIncidencia();
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Agregar(existente);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarIncidenciasCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarIncidenciasCommand([existente.Id, Guid.NewGuid()]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle();
    }

    [Fact]
    public async Task No_borra_ninguna_del_lote_sin_identidad_resuelta()
    {
        var incidencia = CrearIncidencia();
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Agregar(incidencia);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarIncidenciasCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarIncidenciasCommand([incidencia.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Incidencia.SinIdentidad");
        incidencia.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
