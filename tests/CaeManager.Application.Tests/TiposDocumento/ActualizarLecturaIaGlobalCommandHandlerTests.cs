using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaGlobal;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.TiposDocumento;

public class ActualizarLecturaIaGlobalCommandHandlerTests
{
    private static CaeManager.Domain.Documentos.TipoDocumento CrearTipo(
        SensibilidadDocumental sensibilidad = SensibilidadDocumental.DatosPersonales) =>
        new("Formación PRL", 12, true, 1, AmbitoAplicacion.Trabajador, sensibilidad: sensibilidad);

    private static ActualizarLecturaIaGlobalCommandHandler CrearHandler(
        TipoDocumentoRepositorioFalso repositorio, UnitOfWorkFalso unitOfWork, string rol = "Administrador") =>
        new(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid(), rol));

    [Fact]
    public async Task Administrador_puede_desactivar_la_lectura_ia_global()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = CrearTipo();
        repositorio.Agregar(tipo);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await CrearHandler(repositorio, unitOfWork).Handle(new ActualizarLecturaIaGlobalCommand(tipo.Id, false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        tipo.LecturaIaActiva.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Administrador_puede_activar_la_lectura_ia_en_un_tipo_que_no_revela_salud()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = CrearTipo();
        tipo.EstablecerLecturaIaActiva(false);
        repositorio.Agregar(tipo);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await CrearHandler(repositorio, unitOfWork).Handle(new ActualizarLecturaIaGlobalCommand(tipo.Id, true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        tipo.LecturaIaActiva.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    /// <summary>
    /// Decisión de Legal del 2026-11-17: la lectura con IA de un tipo que revela salud queda bloqueada con la misma
    /// condición que Traducir PDF, que hoy no se cumple. Un tipo sin clasificar cuenta como de salud.
    /// </summary>
    [Fact]
    public async Task Administrador_no_puede_activar_la_lectura_ia_en_un_tipo_que_revela_salud()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = CrearTipo(SensibilidadDocumental.CategoriaEspecialSalud);
        repositorio.Agregar(tipo);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await CrearHandler(repositorio, unitOfWork).Handle(new ActualizarLecturaIaGlobalCommand(tipo.Id, true), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("LecturaIa.BloqueadaPorSalud");
        resultado.Error.Mensaje.Should().Be("La lectura con IA no se puede activar en un tipo de documento con datos de salud.");
        tipo.LecturaIaActiva.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Administrador_no_puede_activar_la_lectura_ia_en_un_tipo_sin_clasificar()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = new CaeManager.Domain.Documentos.TipoDocumento("Tipo nuevo", 12, true, 1, AmbitoAplicacion.Trabajador);
        repositorio.Agregar(tipo);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await CrearHandler(repositorio, unitOfWork).Handle(new ActualizarLecturaIaGlobalCommand(tipo.Id, true), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("LecturaIa.BloqueadaPorSalud");
        tipo.LecturaIaActiva.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Administrador_puede_desactivar_la_lectura_ia_de_un_tipo_que_revela_salud()
    {
        var repositorio = new TipoDocumentoRepositorioFalso();
        var tipo = CrearTipo(SensibilidadDocumental.CategoriaEspecialSalud);
        repositorio.Agregar(tipo);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await CrearHandler(repositorio, unitOfWork).Handle(new ActualizarLecturaIaGlobalCommand(tipo.Id, false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        tipo.LecturaIaActiva.Should().BeFalse();
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

        var resultado = await CrearHandler(repositorio, unitOfWork, rol).Handle(new ActualizarLecturaIaGlobalCommand(tipo.Id, false), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("LecturaIa.SoloAdministrador");
        tipo.LecturaIaActiva.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
