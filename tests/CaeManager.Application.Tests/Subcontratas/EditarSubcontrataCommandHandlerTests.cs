using CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>
/// Cobertura mínima de autorización — el resto del comportamiento (diff de
/// contrapartes, guard de cierre de arista, enmarcado) se prueba a nivel de
/// integración, donde <c>IRelacionEmpresarialRepository</c> y
/// <c>IGuardDeCierreDeArista</c> tienen dobles reales; aquí solo hace falta
/// que el handler DENIEGUE antes de tocarlos, así que se pasan <c>null!</c> a
/// propósito: si algún día el handler los invocara antes de comprobar el
/// alcance, este test fallaría con un <see cref="NullReferenceException"/>
/// en vez de dar un falso verde.
/// </summary>
public class EditarSubcontrataCommandHandlerTests
{
    [Fact]
    public async Task Falla_cuando_la_subcontrata_no_existe()
    {
        var repositorio = new EmpresaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EditarSubcontrataCommandHandler(
            repositorio, relacionEmpresarialRepositorio: null!, empresasContext: null!, guardDeCierre: null!,
            new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new EditarSubcontrataCommand(Guid.NewGuid(), "Andamios del Sur S.L.", null, [], []), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Subcontrata.NoEncontrada");
    }

    /// <summary>
    /// REC-172, gemelo de REC-153/159 en Empresa: un usuario de portal tiene
    /// la Subcontrata en su cartera de LECTURA (por eso ve su documentación,
    /// derivada de su propio Cliente) pero no en la de GESTIÓN — y editar sus
    /// datos es un acto de gestión, no de lectura.
    /// </summary>
    [Fact]
    public async Task Usuario_de_portal_no_puede_editar_una_subcontrata_de_su_cliente()
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Contrata de mi Cliente S.L.", "B12345674", NivelServicioSubcontrata.Gestionada.ToString());
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(subcontrata);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EditarSubcontrataCommandHandler(
            repositorio, relacionEmpresarialRepositorio: null!, empresasContext: null!, guardDeCierre: null!,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrata.Id], subcontrataIdsParaGestion: []),
            unitOfWork);

        var resultado = await handler.Handle(
            new EditarSubcontrataCommand(subcontrata.Id, "Otra razón social", null, [], [], subcontrata.Version),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Subcontrata.NoEncontrada");
        subcontrata.RazonSocial.Should().Be("Contrata de mi Cliente S.L.");
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
