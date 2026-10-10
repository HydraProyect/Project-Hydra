using Bunit;
using CaeManager.Application.Importacion.Queries;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// Foco y anuncios al avanzar por el asistente (revisión de Codex): el control
/// que tenía el foco desaparece con el paso anterior, así que la página se lo
/// da al título del paso nuevo; y el progreso del análisis se anuncia desde
/// una región de estado que ya estaba ahí antes de que empezara.
///
/// <para>
/// Del foco solo se observa que la página LLAMA a <c>FocusAsync</c> con la
/// referencia del título pintado; que el navegador lo mueva de verdad sería un
/// E2E. La referencia se lee del campo <c>_tituloPaso</c> tras cada render:
/// cada paso pinta un <c>&lt;h2&gt;</c> distinto, y cada uno recibe una
/// referencia nueva.
/// </para>
/// </summary>
public partial class ImportarClientesGen2Tests
{
    [Fact]
    public async Task Avanzar_de_paso_lleva_el_foco_al_titulo_del_paso_nuevo_y_entrar_no_lo_roba()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("A", [Fila("Alfa S.L.")]);
        var (cut, _) = Renderizar(escenario);

        PeticionesDeFoco().Should().BeEmpty("al entrar en la página no se mueve el foco de nadie");

        var vistos = new List<string>();
        async Task AvanzarYComprobar(string boton, string tituloEsperado, int peticiones)
        {
            // La referencia de ANTES: si el título del paso nuevo no captura la
            // suya, el campo conserva la del paso anterior y comparar el foco
            // con el campo no distinguiría nada (medido: la mutación que quita
            // el @ref del paso 2 pasaba con esa comparación sola).
            var antes = Campo<ElementReference>(cut.Instance, "_tituloPaso").Id;

            await Pulsar(cut, boton);
            TituloDeLaSeccion(cut).Should().Be(tituloEsperado);
            PeticionesDeFoco().Should().HaveCount(peticiones, $"«{boton}» cambia de paso una vez");

            var titulo = Campo<ElementReference>(cut.Instance, "_tituloPaso");
            titulo.Id.Should().NotBeNullOrEmpty("sin @ref en el título la comparación no distinguiría nada")
                .And.NotBe(antes, $"el título «{tituloEsperado}» captura su propia referencia, no hereda la del paso anterior");
            PeticionesDeFoco().Last().Arguments[0].Should().BeOfType<ElementReference>()
                .Which.Id.Should().Be(titulo.Id, $"tras «{boton}» el foco va al título «{tituloEsperado}»");
            vistos.Add(titulo.Id);
        }

        await AvanzarYComprobar("Continuar con Plantilla de Clientes", "Sube el archivo", 1);
        await Subir(cut, "a.xlsx", "A");
        await AvanzarYComprobar("Ver plan de importación", "Revisar plan", 2);
        await AvanzarYComprobar("Continuar a confirmar", "Confirmar importación", 3);

        vistos.Should().OnlyHaveUniqueItems("cada paso pinta su propio título, no el del paso anterior");
    }

    [Fact]
    public async Task Volver_a_elegir_plantilla_tambien_lleva_el_foco_a_su_titulo()
    {
        var (cut, _) = Renderizar(new Escenario());
        await Pulsar(cut, "Continuar con Plantilla de Clientes");

        await Pulsar(cut, "← Cambiar plantilla");

        TituloDeLaSeccion(cut).Should().Be("Elige una plantilla");
        PeticionesDeFoco().Should().HaveCount(2);
        PeticionesDeFoco().Last().Arguments[0].Should().BeOfType<ElementReference>()
            .Which.Id.Should().Be(Campo<ElementReference>(cut.Instance, "_tituloPaso").Id);
    }

    [Fact]
    public async Task El_progreso_del_analisis_se_anuncia_desde_una_region_de_estado_que_ya_existia()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("A", [Fila("Alfa S.L.")]);
        var puerta = Puerta();
        escenario.Retener = p => p is AnalizarPlantillaClientesQuery ? puerta.Task : null;
        var (cut, _) = Renderizar(escenario);
        await Pulsar(cut, "Continuar con Plantilla de Clientes");

        // Antes de analizar: la región ya está, vacía, para que el lector de
        // pantalla la vigile cuando el progreso aparezca.
        var region = cut.Find(".region-progreso-analisis");
        region.GetAttribute("role").Should().Be("status");
        region.GetAttribute("aria-live").Should().Be("polite");
        region.QuerySelector(".progreso-carga").Should().BeNull();

        var subida = Subir(cut, "a.xlsx", "A");
        cut.WaitForAssertion(() =>
            cut.Find(".region-progreso-analisis").QuerySelector(".progreso-carga")
                .Should().NotBeNull("el progreso se pinta dentro de la región que anuncia"));

        puerta.SetResult();
        await subida;
    }
}
