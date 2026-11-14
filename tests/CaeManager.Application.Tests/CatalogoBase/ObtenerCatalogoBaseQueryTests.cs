using CaeManager.Application.CatalogoBase;
using CaeManager.Application.CatalogoBase.Queries;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.CatalogoBase;
using FluentAssertions;

namespace CaeManager.Application.Tests.CatalogoBase;

/// <summary>
/// La Query del catálogo base de Plataforma (B-13): devuelve las Actividades activas con sus códigos oficiales y los
/// riesgos activos con los requisitos de base activos que proponen, la opción «otro» la última. Siembra inventada:
/// el contenido real espera al técnico habilitado (CA-7).
/// </summary>
public class ObtenerCatalogoBaseQueryTests
{
    private sealed class Contexto : ICatalogoBaseQueryContext
    {
        public List<ActividadCatalogo> Actividades { get; } = [];
        public List<RiesgoCatalogo> Riesgos { get; } = [];
        public List<RequisitoBase> Requisitos { get; } = [];
        public List<ReglaRiesgoRequisito> Reglas { get; } = [];

        public IQueryable<ActividadCatalogo> CatalogoActividades => new TestAsyncQueryable<ActividadCatalogo>(Actividades.AsQueryable());
        public IQueryable<CodigoOficialActividad> CatalogoActividadesCodigosOficiales =>
            new TestAsyncQueryable<CodigoOficialActividad>(Actividades.SelectMany(a => a.CodigosOficiales).AsQueryable());
        public IQueryable<RiesgoCatalogo> CatalogoRiesgos => new TestAsyncQueryable<RiesgoCatalogo>(Riesgos.AsQueryable());
        public IQueryable<RequisitoBase> CatalogoRequisitosBase => new TestAsyncQueryable<RequisitoBase>(Requisitos.AsQueryable());
        public IQueryable<ReglaRiesgoRequisito> CatalogoReglasRiesgoRequisito => new TestAsyncQueryable<ReglaRiesgoRequisito>(Reglas.AsQueryable());
    }

    private static Task<CatalogoBaseDto> LeerAsync(Contexto contexto) =>
        new ObtenerCatalogoBaseQueryHandler(contexto).Handle(new ObtenerCatalogoBaseQuery(), CancellationToken.None);

    [Fact]
    public async Task Sin_contenido_cargado_devuelve_el_catalogo_vacio()
    {
        var catalogo = await LeerAsync(new Contexto());

        catalogo.Actividades.Should().BeEmpty();
        catalogo.Riesgos.Should().BeEmpty();
    }

    [Fact]
    public async Task Devuelve_las_Actividades_activas_por_nombre_con_sus_codigos_oficiales()
    {
        var contexto = new Contexto();
        var b = new ActividadCatalogo("actividad-b", "Bravo");
        b.LigarCodigoOficial(SistemaCodigoOficial.Cno11, "0002");
        b.LigarCodigoOficial(SistemaCodigoOficial.Cnae2025, "00.02");
        b.LigarCodigoOficial(SistemaCodigoOficial.Cnae2025, "00.01");
        var a = new ActividadCatalogo("actividad-a", "Alfa");
        var retirada = new ActividadCatalogo("actividad-retirada", "Retirada");
        retirada.LigarCodigoOficial(SistemaCodigoOficial.Regcon, "0009");
        retirada.Retirar();
        contexto.Actividades.AddRange([b, retirada, a]);

        var catalogo = await LeerAsync(contexto);

        catalogo.Actividades.Select(x => x.Codigo).Should().Equal("actividad-a", "actividad-b");
        catalogo.Actividades[0].CodigosOficiales.Should().BeEmpty();
        catalogo.Actividades[1].Id.Should().Be(b.Id);
        catalogo.Actividades[1].CodigosOficiales.Should().Equal(
            new CodigoOficialDto(SistemaCodigoOficial.Cnae2025, "00.01"),
            new CodigoOficialDto(SistemaCodigoOficial.Cnae2025, "00.02"),
            new CodigoOficialDto(SistemaCodigoOficial.Cno11, "0002"));
    }

    [Fact]
    public async Task Devuelve_los_riesgos_activos_con_la_opcion_otro_la_ultima_y_sus_requisitos_activos()
    {
        var contexto = new Contexto();
        var otro = RiesgoCatalogo.Otro("otro", "Aaa otro riesgo");
        var zulu = RiesgoCatalogo.Nuevo("riesgo-z", "Zulu");
        var alfa = RiesgoCatalogo.Nuevo("riesgo-a", "Alfa");
        var retirado = RiesgoCatalogo.Nuevo("riesgo-retirado", "Retirado");
        retirado.Retirar();
        contexto.Riesgos.AddRange([otro, zulu, alfa, retirado]);

        var n3 = new RequisitoBase("requisito-n3", "Requisito tres", "Práctica de prueba", NivelExigencia.N3);
        var n1 = new RequisitoBase("requisito-n1", "Requisito uno", "Norma de prueba, art. 1", NivelExigencia.N1);
        var requisitoRetirado = new RequisitoBase("requisito-retirado", "Retirado", "Norma", NivelExigencia.N1);
        requisitoRetirado.Retirar();
        contexto.Requisitos.AddRange([n3, n1, requisitoRetirado]);
        contexto.Reglas.AddRange([
            new ReglaRiesgoRequisito(zulu, n3), new ReglaRiesgoRequisito(zulu, n1), new ReglaRiesgoRequisito(zulu, requisitoRetirado),
            new ReglaRiesgoRequisito(alfa, n1), new ReglaRiesgoRequisito(retirado, n1),
        ]);

        var catalogo = await LeerAsync(contexto);

        catalogo.Riesgos.Select(r => (r.Codigo, r.EsOtro)).Should().Equal(("riesgo-a", false), ("riesgo-z", false), ("otro", true));
        catalogo.Riesgos[0].Requisitos.Select(q => q.Codigo).Should().Equal("requisito-n1");
        catalogo.Riesgos[1].Requisitos.Should().Equal(
            new RequisitoBaseDto(n1.Id, "requisito-n1", "Requisito uno", "Norma de prueba, art. 1", NivelExigencia.N1),
            new RequisitoBaseDto(n3.Id, "requisito-n3", "Requisito tres", "Práctica de prueba", NivelExigencia.N3));
        catalogo.Riesgos[2].Requisitos.Should().BeEmpty();
    }
}
