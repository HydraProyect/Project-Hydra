using Bunit;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Domain.Documentos;
using FluentAssertions;

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
