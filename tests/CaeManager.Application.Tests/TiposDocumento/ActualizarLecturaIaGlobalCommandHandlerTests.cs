using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaGlobal;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.TiposDocumento;

public class ActualizarLecturaIaGlobalCommandHandlerTests
{
    private static CaeManager.Domain.Documentos.TipoDocumento CrearTipo() =>
        new("Apto médico laboral", 12, true, 1, AmbitoAplicacion.Trabajador);

    [Fact]
    public async Task Administrador_puede_desactivar_la_lectura_ia_global()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = CrearTipo();
        repositorio.Agregar(tipo);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ActualizarLecturaIaGlobalCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"));

        var resultado = await handler.Handle(new ActualizarLecturaIaGlobalCommand(tipo.Id, false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        tipo.LecturaIaActiva.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    public async Task Otros_roles_no_pueden_cambiar_el_interruptor_global(string rol)
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = CrearTipo();
        repositorio.Agregar(tipo);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ActualizarLecturaIaGlobalCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid(), rol));

        var resultado = await handler.Handle(new ActualizarLecturaIaGlobalCommand(tipo.Id, false), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("LecturaIa.SoloAdministrador");
        tipo.LecturaIaActiva.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
