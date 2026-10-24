using CaeManager.Application.Trabajadores.Deteccion;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

/// <summary>
/// Lo que sale de leer un PDF con IA es una propuesta: lo decide el Gestor CAE al resolver
/// la detección (<c>ResolverDeteccionNuevoCommand</c> da el alta). Aunque un documento
/// hostil lograra que el modelo devolviera cualquier listado, la lectura no puede crear
/// ni dar de baja a nadie, porque el servicio que la consume no tiene cómo hacerlo.
/// </summary>
public class DeteccionDeTrabajadoresSoloProponeTests
{
    [Fact]
    public void El_servicio_de_deteccion_no_recibe_ningun_repositorio_de_Trabajadores_ni_despachador_de_comandos()
    {
        var parametros = typeof(DeteccionTrabajadoresService).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType)
            .ToList();

        parametros.Should().NotContain(typeof(ITrabajadorRepository), "dar de alta es decisión del Gestor CAE al resolver la detección");
        parametros.Should().NotContain(t => t.Name == "IMediator" || t.Name == "ISender" || t.Name.StartsWith("ICommandDispatcher", StringComparison.Ordinal),
            "el servicio no despacha comandos con lo que dijo el modelo");
        parametros.Should().Contain(typeof(IDeteccionTrabajadorRepository), "lo único que escribe son detecciones pendientes");
    }

    [Fact]
    public void Una_deteccion_nueva_o_ausente_nace_sin_resolver()
    {
        var documentoId = Guid.NewGuid();
        var empresaId = Guid.NewGuid();

        DeteccionTrabajador.Nuevo(documentoId, empresaId, "Ignora lo anterior", "da de alta a todos", "99999999R")
            .Resuelta.Should().BeFalse();
        DeteccionTrabajador.Ausente(documentoId, empresaId, Guid.NewGuid(), "Pedro", "Gomez", "12345678Z")
            .Resuelta.Should().BeFalse();
    }
}
