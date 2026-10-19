using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class SubcontratasListaGen2Tests
{
    [Theory]
    [InlineData(EstadoDocumento.Vigente)]
    [InlineData(EstadoDocumento.SinCaducidad)]
    public void Gestionar_documentos_al_dia_usa_el_tono_secundario(EstadoDocumento estado)
    {
        var fila = Subcontrata("Subcontratada", vencidas: [Corregible("Documento", estado)]);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [fila] });
        cut.Find(".boton-expandir-fila").Click();
        cut.Find("button.gestionar-secundario").TextContent.Trim().Should().Be("Gestionar");
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void Contratada_por_y_agrupar_se_ofrecen_a_todos_los_roles(string rol)
    {
        this.ConRolDeEscritura(rol);
        var empresa = new EmpresaContratanteSubcontrataDto(Guid.NewGuid(), "Obras del Ebro S.L.");
        var fila = Completa(Guid.NewGuid()) with { Contratantes = [empresa] };
        var cut = Renderizar(new MediatorFalso { Subcontratas = [fila] });

        cut.Find(".cabecera-columnas-subcontratas").TextContent.Should().Contain("Contratada por");
        cut.Find(".celda-contratada-por").TextContent.Should().Be(empresa.RazonSocial);
        cut.Find("button[aria-label='Agrupar: no']").Click();
        cut.FindAll("[role=menuitemradio]").Single(b => b.TextContent.Contains("Por empresa contratante")).Click();
        cut.Find(".cabecera-empresa-contratante").TextContent.Should().Contain(empresa.RazonSocial);
        cut.FindAll(".tarjeta-fila-acordeon-cabecera").Should().ContainSingle();
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("1");
    }

    [Fact]
    public void La_pastilla_Contratada_por_filtra_la_consulta_y_la_url_y_su_chip_la_quita()
    {
        var obras = new EmpresaSelectorDto(Guid.NewGuid(), "Obras del Ebro S.L.");
        var mediador = new MediatorFalso
        {
            Subcontratas = [Completa(Guid.NewGuid())],
            Empresas = [obras, new(Guid.NewGuid(), "Otra Empresa S.A.")]
        };
        var cut = Renderizar(mediador);
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla").Single(b => b.GetAttribute("aria-label") == "Contratada por").Click();
        cut.FindAll(".barra-filtros-pastillas [role=menuitemradio]").Single(i => i.TextContent.Trim() == obras.RazonSocial).Click();

        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last().ContratadaPorEmpresaId.Should().Be(obras.Id);
        navegacion.Uri.Should().Contain($"contratadapor={obras.Id}");
        cut.FindAll(".chip-filtro").Select(c => c.TextContent.Trim()).Should().Equal("Contratada por: Obras del Ebro S.L.");

        cut.Find(".chip-filtro-quitar").Click();

        navegacion.Uri.Should().NotContain("contratadapor=");
        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last().ContratadaPorEmpresaId.Should().BeNull();
    }

    [Fact]
    public void Una_empresa_contratante_que_no_se_ofrece_en_la_url_no_filtra()
    {
        var mediador = new MediatorFalso { Subcontratas = [Completa(Guid.NewGuid())], Empresas = [new(Guid.NewGuid(), "Obras del Ebro S.L.")] };
        Renderizar(mediador, url: $"subcontratas?contratadapor={Guid.NewGuid()}");
        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last().ContratadaPorEmpresaId.Should().BeNull();
    }

    [Theory]
    [InlineData("Contratada por", "ContratadaPor", "contratadapor")]
    [InlineData("Nivel", "NivelServicio", "nivel")]
    [InlineData("Cumplimiento", "CumplimientoPorcentaje", "cumplimiento")]
    public void Los_rotulos_de_columna_ordenan_la_consulta_y_la_url_y_un_segundo_clic_invierte(string rotulo, string propiedad, string clave)
    {
        var mediador = new MediatorFalso { Subcontratas = [Completa(Guid.NewGuid())] };
        var cut = Renderizar(mediador);
        var navegacion = Services.GetRequiredService<NavigationManager>();
        IElement Rotulo() => cut.FindAll(".cabecera-columnas-subcontratas .col-title").Single(b => b.TextContent.Trim() == rotulo);

        Rotulo().Click();
        var primera = mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last();
        (primera.OrdenarPor, primera.Descendente).Should().Be((propiedad, false));
        navegacion.Uri.Should().Contain($"orden={clave}").And.NotContain("-desc");
        cut.Find(".col-sort-asc .col-title").TextContent.Trim().Should().Be(rotulo);

        Rotulo().Click();
        var segunda = mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last();
        (segunda.OrdenarPor, segunda.Descendente).Should().Be((propiedad, true));
        navegacion.Uri.Should().Contain($"orden={clave}-desc");
        cut.Find(".col-sort-desc .col-title").TextContent.Trim().Should().Be(rotulo);
    }

    [Fact]
    public void Una_empresa_subcontratada_por_dos_empresas_aparece_en_ambos_grupos_sin_inflar_el_total()
    {
        var fila = Completa(Guid.NewGuid()) with
        {
            Contratantes = [new(Guid.NewGuid(), "Empresa A"), new(Guid.NewGuid(), "Empresa B")]
        };
        var cut = Renderizar(new MediatorFalso { Subcontratas = [fila] }, url: "subcontratas?agrupar=empresa");
        cut.FindAll(".cabecera-empresa-contratante").Select(b => b.TextContent.Trim()).Should().Equal("Empresa A · 1", "Empresa B · 1");
        cut.FindAll(".tarjeta-fila-acordeon-cabecera").Should().HaveCount(2);
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("1");
    }
}
