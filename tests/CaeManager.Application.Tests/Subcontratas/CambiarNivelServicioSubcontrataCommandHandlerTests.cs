using CaeManager.Application.Subcontratas.Commands.CambiarNivelServicioSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

public class CambiarNivelServicioSubcontrataCommandHandlerTests
{
    [Fact]
    public async Task Cambia_el_nivel_de_servicio()
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Andamios del Sur S.L.", "B12345674", NivelServicioSubcontrata.Gestionada.ToString());
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(subcontrata);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CambiarNivelServicioSubcontrataCommandHandler(repositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new CambiarNivelServicioSubcontrataCommand(subcontrata.Id, NivelServicioSubcontrata.Supervisada, subcontrata.Version),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        subcontrata.NivelServicio.Should().Be(NivelServicioSubcontrata.Supervisada.ToString());
    }

    [Fact]
    public async Task Falla_cuando_la_subcontrata_no_existe()
    {
        var repositorio = new EmpresaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CambiarNivelServicioSubcontrataCommandHandler(repositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new CambiarNivelServicioSubcontrataCommand(Guid.NewGuid(), NivelServicioSubcontrata.Supervisada), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Subcontrata.NoEncontrada");
    }

    /// <summary>
    /// REC-172, gemelo de REC-153/159 en Empresa: un usuario de portal tiene
    /// la Subcontrata en su cartera de LECTURA (por eso ve su documentación,
    /// derivada de su propio Cliente) pero no en la de GESTIÓN — y cambiar el
    /// nivel de servicio contratado es un acto de gestión, no de lectura.
    /// </summary>
    [Fact]
    public async Task Usuario_de_portal_no_puede_cambiar_el_nivel_de_servicio_de_una_subcontrata_de_su_cliente()
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Contrata de mi Cliente S.L.", "B12345674", NivelServicioSubcontrata.Gestionada.ToString());
        var repositorio = new EmpresaRepositorioFalso();
        repositorio.Agregar(subcontrata);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CambiarNivelServicioSubcontrataCommandHandler(
            repositorio,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrata.Id], subcontrataIdsParaGestion: []),
            unitOfWork);

        var resultado = await handler.Handle(
            new CambiarNivelServicioSubcontrataCommand(subcontrata.Id, NivelServicioSubcontrata.Supervisada, subcontrata.Version),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Subcontrata.NoEncontrada");
        subcontrata.NivelServicio.Should().Be(NivelServicioSubcontrata.Gestionada.ToString());
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
