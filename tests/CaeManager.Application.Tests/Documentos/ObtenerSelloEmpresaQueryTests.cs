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

    /// <summary>
    /// REC-153: la Empresa está en la cartera de LECTURA del usuario de portal
    /// (rol Cliente) — es su propia contratista, y por eso el portal le enseña
    /// su documentación — pero el sello es un instrumento de firma, no
    /// documentación, y su cartera de GESTIÓN es vacía. Antes del arreglo esta
    /// consulta usaba la cartera de lectura como puerta, y ese mismo usuario
    /// podía descargar la imagen del sello.
    /// </summary>
    [Fact]
    public async Task Devuelve_null_para_un_usuario_de_portal_aunque_la_empresa_este_en_su_cartera_de_lectura()
    {
        var empresaId = Guid.NewGuid();
        var contexto = new DocumentosQueryContextFalso();
        contexto.ListaSellosEmpresa.Add(new SelloEmpresa(empresaId, "url/mio.png", DateTime.UtcNow));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresaId], empresaIdsParaGestion: []);
        var handler = new ObtenerSelloEmpresaQueryHandler(contexto, alcance);

        var resultado = await handler.Handle(new ObtenerSelloEmpresaQuery(empresaId), CancellationToken.None);

        resultado.Should().BeNull();
    }
}
