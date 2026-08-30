using CaeManager.Application.Subcontratas.Commands.EliminarSubcontratas;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

public class EliminarSubcontratasCommandHandlerTests
{
    private static Empresa CrearSubcontrata(string cif) => Empresa.CrearComoSubcontrata("Andamios del Sur S.L.", cif, "Gestionada");

    [Fact]
    public async Task Marca_todas_las_subcontratas_del_lote_como_eliminadas()
    {
        var uno = CrearSubcontrata("B10380210");
        var dos = CrearSubcontrata("B10380202");
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(uno);
        repositorio.Agregar(dos);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarSubcontratasCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarSubcontratasCommand([uno.Id, dos.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(2);
        uno.EstaEliminado.Should().BeTrue();
        dos.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Reporta_error_parcial_cuando_una_subcontrata_tiene_trabajadores()
    {
        var sinTrabajadores = CrearSubcontrata("B10380210");
        var conTrabajadores = CrearSubcontrata("B10380202");
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(sinTrabajadores);
        repositorio.Agregar(conTrabajadores);
        repositorio.IdsConTrabajadoresComoSubcontrata.Add(conTrabajadores.Id);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarSubcontratasCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarSubcontratasCommand([sinTrabajadores.Id, conTrabajadores.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle();
        conTrabajadores.EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task No_borra_ninguna_del_lote_sin_identidad_resuelta()
    {
        var subcontrata = CrearSubcontrata("B10380210");
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(subcontrata);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarSubcontratasCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarSubcontratasCommand([subcontrata.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Subcontrata.SinIdentidad");
        subcontrata.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
