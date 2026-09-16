using CaeManager.Application.TiposDocumento.Queries.ObtenerConfiguracionIaPorCliente;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.TiposDocumento;

public class ObtenerConfiguracionIaPorClienteQueryHandlerTests
{
    [Fact]
    public async Task Proyecta_la_marca_de_deteccion_de_personal_del_tipo_de_documento()
    {
        var tipoConDeteccion = new TipoDocumento(
            "Informe de trabajadores", null, false, 1, AmbitoAplicacion.Empresa);
        tipoConDeteccion.EstablecerDeteccionTrabajadoresActiva(true);
        var tipoSinDeteccion = new TipoDocumento(
            "Seguro", null, false, 2, AmbitoAplicacion.Empresa);
        var contexto = new TiposDocumentoQueryContextFalso();
        contexto.ListaTiposDocumento.Add(tipoConDeteccion);
        contexto.ListaTiposDocumento.Add(tipoSinDeteccion);
        var handler = new ObtenerConfiguracionIaPorClienteQueryHandler(
            contexto, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(
            new ObtenerConfiguracionIaPorClienteQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.Should().HaveCount(2, "el control positivo confirma que ambos tipos del catálogo se proyectaron");
        resultado.Should().ContainSingle(t => t.Nombre == "Informe de trabajadores" && t.DeteccionTrabajadoresActiva);
        resultado.Should().ContainSingle(t => t.Nombre == "Seguro" && !t.DeteccionTrabajadoresActiva);
    }
}
