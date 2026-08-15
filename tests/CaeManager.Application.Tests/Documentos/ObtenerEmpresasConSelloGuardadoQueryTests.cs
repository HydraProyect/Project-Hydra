using CaeManager.Application.Documentos.Queries.ObtenerEmpresasConSelloGuardado;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class ObtenerEmpresasConSelloGuardadoQueryTests
{
    [Fact]
    public async Task Devuelve_solo_las_empresas_con_sello_guardado_ordenadas_por_razon_social()
    {
        var empresaConSello = new Empresa("Zeta S.L.", "B12345674");
        var empresaConSello2 = new Empresa("Alfa S.L.", "B12345674");
        var empresaSinSello = new Empresa("Beta S.L.", "B12345674");

        var documentosContext = new DocumentosQueryContextFalso();
        documentosContext.ListaSellosEmpresa.Add(new SelloEmpresa(empresaConSello.Id, "url/1.png", DateTime.UtcNow));
        documentosContext.ListaSellosEmpresa.Add(new SelloEmpresa(empresaConSello2.Id, "url/2.png", DateTime.UtcNow));

        var empresasContext = new EmpresasQueryContextFalso();
        empresasContext.ListaEmpresas.AddRange([empresaConSello, empresaConSello2, empresaSinSello]);

        var handler = new ObtenerEmpresasConSelloGuardadoQueryHandler(documentosContext, empresasContext, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerEmpresasConSelloGuardadoQuery(), CancellationToken.None);

        resultado.Should().HaveCount(2);
        resultado.Select(e => e.RazonSocial).Should().Equal("Alfa S.L.", "Zeta S.L.");
    }

    [Fact]
    public async Task Respeta_la_cartera_visible_del_usuario_actual()
    {
        var empresaVisible = new Empresa("Empresa visible", "B12345674");
        var empresaFueraDeCartera = new Empresa("Empresa fuera de cartera", "B12345674");

        var documentosContext = new DocumentosQueryContextFalso();
        documentosContext.ListaSellosEmpresa.Add(new SelloEmpresa(empresaVisible.Id, "url/1.png", DateTime.UtcNow));
        documentosContext.ListaSellosEmpresa.Add(new SelloEmpresa(empresaFueraDeCartera.Id, "url/2.png", DateTime.UtcNow));

        var empresasContext = new EmpresasQueryContextFalso();
        empresasContext.ListaEmpresas.AddRange([empresaVisible, empresaFueraDeCartera]);

        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresaVisible.Id]);
        var handler = new ObtenerEmpresasConSelloGuardadoQueryHandler(documentosContext, empresasContext, alcance);

        var resultado = await handler.Handle(new ObtenerEmpresasConSelloGuardadoQuery(), CancellationToken.None);

        resultado.Should().ContainSingle(e => e.EmpresaId == empresaVisible.Id);
    }
}
