using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Bandeja.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using static CaeManager.Web.Tests.BandejaDatosDePrueba;

namespace CaeManager.Web.Tests;

/// <summary>
/// Red de seguridad de las <c>@key</c> construidas (continuación de #1064: la misma Faltante de un Trabajador en dos Centros
/// compartía <c>ItemBandejaDto.Id</c>, es decir, dos <c>@key</c> hermanas, y el diff de Blazor mataba el circuito).
///
/// <para>
/// Dos mitades. <b>El instrumento</b> (<see cref="ClavesDeRender"/>): un control positivo que demuestra que ve una clave repetida
/// entre hermanos, en elementos, en componentes y dentro de un descendiente, y que no da falso positivo con la misma clave bajo
/// padres distintos. <b>Las páginas con datos que generan hermanos</b>: la cola se pinta con Ids repetidos (dentro de un grupo,
/// entre Trabajadores de una misma Empresa, en la lista sin grupo) y el árbol resultante no tiene claves repetidas, además de
/// sobrevivir a un repintado (que es donde Blazor lanza). La unicidad la da la vista (<see cref="ClavesDeHermanos"/>), no el dato:
/// el productor que repite un Id sigue siendo un defecto, que vigilan los tests de Application, pero ya no mata el circuito.
/// </para>
/// </summary>
public class ClavesUnicasEnListasTests : BunitContext
{
    public ClavesUnicasEnListasTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    // ------------------------------------------------------------ el instrumento

    private sealed class ListaConClaves : ComponentBase
    {
        [Parameter] public string[] Claves { get; set; } = [];
        [Parameter] public bool EnPadresDistintos { get; set; }
        [Parameter] public bool ComoComponentes { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            foreach (var clave in Claves)
            {
                if (EnPadresDistintos) builder.OpenElement(1, "section");
                if (ComoComponentes)
                {
                    builder.OpenComponent<Hoja>(2);
                    builder.SetKey(clave);
                    builder.CloseComponent();
                }
                else
                {
                    builder.OpenElement(3, "li");
                    builder.SetKey(clave);
                    builder.CloseElement();
                }

                if (EnPadresDistintos) builder.CloseElement();
            }

            builder.CloseElement();
        }
    }

    private sealed class Hoja : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "hoja");
    }

    /// <summary>Un componente cuyo propio árbol (no el de la raíz) repite claves: el detector tiene que bajar a los descendientes.</summary>
    private sealed class ContenedorConDescendienteRepetido : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ListaConClaves>(0);
            builder.AddComponentParameter(1, nameof(ListaConClaves.Claves), new[] { "x", "x" });
            builder.CloseComponent();
        }
    }

    [Fact]
    public void El_detector_ve_una_clave_repetida_entre_elementos_hermanos()
    {
        var cut = Render<ListaConClaves>(p => p.Add(c => c.Claves, ["a", "b", "a"]));

        ClavesDeRender.Duplicadas(this, cut).Should().ContainSingle().Which.Should().Contain("repetida").And.Contain("a");
    }

    [Fact]
    public void El_detector_ve_una_clave_repetida_entre_componentes_hermanos()
    {
        var cut = Render<ListaConClaves>(p => p.Add(c => c.Claves, ["k", "k"]).Add(c => c.ComoComponentes, true));

        ClavesDeRender.Duplicadas(this, cut).Should().ContainSingle();
    }

    [Fact]
    public void El_detector_baja_a_los_componentes_descendientes()
    {
        var cut = Render<ContenedorConDescendienteRepetido>();

        ClavesDeRender.Duplicadas(this, cut).Should().ContainSingle().Which.Should().Contain("ListaConClaves");
    }

    [Fact]
    public void El_detector_no_da_falso_positivo_con_claves_distintas_ni_con_la_misma_clave_bajo_padres_distintos()
    {
        var distintas = Render<ListaConClaves>(p => p.Add(c => c.Claves, ["a", "b", "c"]));
        ClavesDeRender.Duplicadas(this, distintas).Should().BeEmpty();
        ClavesDeRender.ClavesObservadas(this, distintas).Should().Be(3, "el control positivo de que el detector observa claves: sin él, «ninguna repetida» valdría por vacío");

        var padresDistintos = Render<ListaConClaves>(p => p.Add(c => c.Claves, ["a", "a"]).Add(c => c.EnPadresDistintos, true));
        ClavesDeRender.Duplicadas(this, padresDistintos).Should().BeEmpty("la misma clave bajo padres distintos es legal: la clave solo compite con sus hermanos");
    }

    [Fact]
    public void Blazor_solo_lanza_al_repintar_y_el_detector_ve_el_duplicado_en_el_primer_pintado()
    {
        // La razón de ser del detector: el primer pintado con claves repetidas pasa en silencio.
        var cut = Render<ListaConClaves>(p => p.Add(c => c.Claves, ["a", "a"]));

        ClavesDeRender.Duplicadas(this, cut).Should().NotBeEmpty();
    }

    // ------------------------------------------------ la unicidad la da la vista

    [Fact]
    public void La_fuente_de_claves_numera_las_repeticiones_y_no_confunde_identidades_distintas()
    {
        var claves = new ClavesDeHermanos();

        var a0 = claves.De("a");
        var b0 = claves.De("b");
        var a1 = claves.De("a");
        var a2 = claves.De("a");

        new[] { a0, b0, a1, a2 }.Distinct().Should().HaveCount(4);
        a0.Should().Be(new ClaveDeHermano("a", 0));
        a1.Repeticion.Should().Be(1);
        a2.Repeticion.Should().Be(2);

        // Identidad compuesta: (Tenant, Id) con el mismo Id en dos Tenants son dos identidades, no una repetida.
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var compuestas = new ClavesDeHermanos();
        compuestas.De((tenantA, "alerta-1")).Repeticion.Should().Be(0);
        compuestas.De((tenantB, "alerta-1")).Repeticion.Should().Be(0);
        compuestas.De((tenantA, "alerta-1")).Repeticion.Should().Be(1);
    }

    [Fact]
    public void Las_claves_son_estables_entre_pintados_con_el_mismo_orden()
    {
        // Una fuente nueva por pintado (se rehace en el marcado): mismos datos, mismas claves.
        static ClaveDeHermano[] Pintar() { var f = new ClavesDeHermanos(); return [f.De("x"), f.De("y"), f.De("x")]; }

        Pintar().Should().Equal(Pintar());
    }

    private (IRenderedComponent<Bandeja> Cut, MediatorDeLaBandeja Mediator) PintarBandeja(params ItemBandejaDto[] items) =>
        PintarBandeja(PerfilVocabularioTenant.ClienteDirecto, items);

    private (IRenderedComponent<Bandeja> Cut, MediatorDeLaBandeja Mediator) PintarBandeja(PerfilVocabularioTenant perfil, params ItemBandejaDto[] items)
    {
        var mediator = new MediatorDeLaBandeja(items) { Perfil = perfil };
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("bandeja");
        return (Render<Bandeja>(), mediator);
    }

    [Fact]
    public void Bandeja_con_Ids_repetidos_dentro_de_un_grupo_entre_Trabajadores_y_en_la_lista_sin_grupo_pinta_todo_sin_claves_repetidas()
    {
        var cliente = Guid.NewGuid();
        var ana = Guid.NewGuid();
        var luis = Guid.NewGuid();
        var (cut, _) = PintarBandeja(
            // Mismo Id para dos Trabajadores del mismo Cliente empresarial: filas hermanas bajo el mismo grupo.
            Item("alerta-repetida", TipoItemBandeja.Vencido, cliente, "Refrielectric S.A.", trabajadorId: ana, trabajadorNombre: "Ana"),
            Item("alerta-repetida", TipoItemBandeja.Vencido, cliente, "Refrielectric S.A.", trabajadorId: luis, trabajadorNombre: "Luis"),
            // Y dos sin Cliente ni Empresa (SinGrupo) con el mismo Id.
            Item("suelta", TipoItemBandeja.RevisionIa, Guid.NewGuid(), "x") with { ClienteId = null, ClienteNombre = null },
            Item("suelta", TipoItemBandeja.RevisionIa, Guid.NewGuid(), "x") with { ClienteId = null, ClienteNombre = null });

        cut.WaitForAssertion(() => cut.FindAll(".panel-resolver-item").Count.Should().Be(4));

        ClavesDeRender.Duplicadas(this, cut).Should().BeEmpty();
        ClavesDeRender.ClavesObservadas(this, cut).Should().BeGreaterThanOrEqualTo(4, "se pintan al menos las cuatro filas keyed");

        // El diff contra el árbol anterior es lo que lanzaba: un repintado no puede romper.
        cut.Render();
        ClavesDeRender.Duplicadas(this, cut).Should().BeEmpty();
    }

    [Fact]
    public void Bandeja_en_vocabulario_Consultora_con_el_mismo_Id_en_dos_Empresas_contratistas_del_mismo_grupo_no_repite_claves()
    {
        // Con Perfil Consultora el grupo se parte en Empresa contratista → Trabajador (bucle anidado): las filas de las dos
        // Empresas son hermanas de un mismo padre, así que la fuente de claves debe cubrir TODO el grupo, no cada Empresa.
        var cliente = Guid.NewGuid();
        var empresaA = Guid.NewGuid();
        var empresaB = Guid.NewGuid();
        var (cut, _) = PintarBandeja(
            PerfilVocabularioTenant.Consultora,
            Item("alerta-repetida", TipoItemBandeja.Vencido, cliente, "Refrielectric S.A.", trabajadorId: Guid.NewGuid(), trabajadorNombre: "Ana") with { EmpresaId = empresaA, EmpresaNombre = "Montajes A" },
            Item("alerta-repetida", TipoItemBandeja.Vencido, cliente, "Refrielectric S.A.", trabajadorId: Guid.NewGuid(), trabajadorNombre: "Luis") with { EmpresaId = empresaB, EmpresaNombre = "Montajes B" });

        cut.WaitForAssertion(() => cut.FindAll(".panel-resolver-item").Count.Should().Be(2));
        cut.FindAll(".grupo-cola-subcabecera-empresa").Should().HaveCount(2, "el vocabulario Consultora parte el grupo por Empresa contratista");

        ClavesDeRender.Duplicadas(this, cut).Should().BeEmpty();
        cut.Render();
        ClavesDeRender.Duplicadas(this, cut).Should().BeEmpty();
    }
}
