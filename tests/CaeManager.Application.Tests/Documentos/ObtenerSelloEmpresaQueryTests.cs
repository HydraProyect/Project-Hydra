using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class ObtenerSelloEmpresaQueryTests
{
    [Fact]
    public async Task Devuelve_null_cuando_la_empresa_no_tiene_sello_guardado()
    {
        var contexto = new DocumentosQueryContextFalso();
        var handler = new ObtenerSelloEmpresaQueryHandler(contexto, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerSelloEmpresaQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Devuelve_el_sello_de_la_empresa_indicada_y_no_el_de_otra()
    {
        var empresaId = Guid.NewGuid();
        var contexto = new DocumentosQueryContextFalso();
        contexto.ListaSellosEmpresa.Add(new SelloEmpresa(empresaId, "url/mio.png", DateTime.UtcNow));
        contexto.ListaSellosEmpresa.Add(new SelloEmpresa(Guid.NewGuid(), "url/ajeno.png", DateTime.UtcNow));
        var handler = new ObtenerSelloEmpresaQueryHandler(contexto, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerSelloEmpresaQuery(empresaId), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.ImagenUrl.Should().Be("url/mio.png");
    }

    [Fact]
    public async Task Devuelve_null_cuando_la_empresa_esta_fuera_de_la_cartera()
    {
        var empresaId = Guid.NewGuid();
        var contexto = new DocumentosQueryContextFalso();
        contexto.ListaSellosEmpresa.Add(new SelloEmpresa(empresaId, "url/mio.png", DateTime.UtcNow));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false);
        var handler = new ObtenerSelloEmpresaQueryHandler(contexto, alcance);

        var resultado = await handler.Handle(new ObtenerSelloEmpresaQuery(empresaId), CancellationToken.None);

        resultado.Should().BeNull();
    }
}
