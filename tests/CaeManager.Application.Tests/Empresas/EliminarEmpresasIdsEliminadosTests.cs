using CaeManager.Application.Empresas.Commands.EliminarEmpresas;
using CaeManager.Application.Tests.Asignaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// FS-09 (auditoría UX de flujos sin salida, 2026-09-24): el lote devuelve los ids
/// que sí eliminó para que la lista ofrezca «Deshacer» sobre ellos y retire solo sus
/// fichas; un id que no se pudo eliminar no se ofrece.
/// </summary>
public class EliminarEmpresasIdsEliminadosTests
{
    [Fact]
    public async Task Devuelve_solo_los_ids_que_elimino()
    {
        var existente = Empresa.CrearComoCliente("Refrielectric S.A.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(existente);
        var handler = new EliminarEmpresasCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), new UnitOfWorkFalso(), new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarEmpresasCommand([existente.Id, Guid.NewGuid()]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle();
        resultado.Valor.IdsEliminados.Should().Equal([existente.Id]);
    }
}
