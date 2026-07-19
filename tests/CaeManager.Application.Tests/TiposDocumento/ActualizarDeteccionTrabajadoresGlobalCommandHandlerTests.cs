using CaeManager.Application.TiposDocumento.Commands.ActualizarDeteccionTrabajadoresGlobal;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.TiposDocumento;

public class ActualizarDeteccionTrabajadoresGlobalCommandHandlerTests
{
    private static CaeManager.Domain.Documentos.TipoDocumento CrearTipoEmpresa() =>
        new("ITA", 1, true, 18, AmbitoAplicacion.Empresa);

    private static CaeManager.Domain.Documentos.TipoDocumento CrearTipoTrabajador() =>
        new("Apto médico laboral", 12, true, 1, AmbitoAplicacion.Trabajador);

    [Fact]
    public async Task Administrador_puede_activar_la_deteccion_para_un_tipo_de_empresa()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = CrearTipoEmpresa();
        repositorio.Agregar(tipo);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ActualizarDeteccionTrabajadoresGlobalCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"));

        var resultado = await handler.Handle(new ActualizarDeteccionTrabajadoresGlobalCommand(tipo.Id, true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        tipo.DeteccionTrabajadoresActiva.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task No_se_puede_activar_para_un_tipo_de_documento_que_no_es_de_empresa()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = CrearTipoTrabajador();
        repositorio.Agregar(tipo);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ActualizarDeteccionTrabajadoresGlobalCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"));

        var resultado = await handler.Handle(new ActualizarDeteccionTrabajadoresGlobalCommand(tipo.Id, true), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DeteccionTrabajadores.AmbitoIncorrecto");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("DireccionCae")]
    public async Task Otros_roles_no_pueden_cambiar_el_interruptor(string rol)
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = CrearTipoEmpresa();
        repositorio.Agregar(tipo);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ActualizarDeteccionTrabajadoresGlobalCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid(), rol));

        var resultado = await handler.Handle(new ActualizarDeteccionTrabajadoresGlobalCommand(tipo.Id, true), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DeteccionTrabajadores.SoloAdministrador");
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
