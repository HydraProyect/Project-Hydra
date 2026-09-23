using Bunit;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="CampoBuscarSelect"/> traduce el texto escrito a un Id. Solo una coincidencia exacta y
/// única elige: con dos opciones del mismo texto, quedarse con la primera asignaría en silencio la
/// otra entidad (un Documento al Trabajador homónimo). Entonces no elige ninguna y la pantalla pide
/// elegir, igual que con un texto sin coincidencia.
/// </summary>
public class CampoBuscarSelectResolucionTests : BunitContext
{
    private readonly List<string> _valores = [];

    private IRenderedComponent<CampoBuscarSelect> Renderizar(params OpcionBuscable[] opciones) =>
        Render<CampoBuscarSelect>(p => p
            .Add(c => c.Etiqueta, "Trabajador")
            .Add(c => c.Opciones, opciones)
            .Add(c => c.ValorChanged, EventCallback.Factory.Create<string>(this, v => _valores.Add(v))));

    [Fact]
    public async Task Una_coincidencia_unica_elige_su_Id()
    {
        var cut = Renderizar(new OpcionBuscable("a", "Ana Ruiz"), new OpcionBuscable("b", "Luis Gil"));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "Luis Gil" });

        cut.WaitForAssertion(() => _valores.Should().Equal("b"));
    }

    [Fact]
    public async Task Dos_opciones_con_el_mismo_texto_no_eligen_ninguna()
    {
        var cut = Renderizar(new OpcionBuscable("a", "Ana Ruiz"), new OpcionBuscable("b", "Ana Ruiz"));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "Ana Ruiz" });

        cut.WaitForAssertion(() => _valores.Should().Equal(string.Empty));
    }

    /// <summary>
    /// Caso adversario de la revisión Codex (ronda 1): con las etiquetas de
    /// <see cref="EtiquetasSelectorTrabajador"/>, escribir cada una resuelve a su Trabajador, aunque
    /// un nombre literal imite la etiqueta con Id de otro.
    /// </summary>
    [Fact]
    public async Task Cada_etiqueta_de_Trabajador_resuelve_a_su_Id_aunque_un_nombre_imite_la_de_otro()
    {
        var idA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var trabajadores = new[]
        {
            new TrabajadorSelectorDto(idA, "Ana Ruiz", null, null),
            new TrabajadorSelectorDto(Guid.Parse("00000000-0000-0000-0000-00000000000b"), "Ana Ruiz", null, null),
            new TrabajadorSelectorDto(Guid.Parse("00000000-0000-0000-0000-00000000000c"), "Ana Ruiz [1]", null, null),
            new TrabajadorSelectorDto(Guid.Parse("00000000-0000-0000-0000-00000000000d"), $"Ana Ruiz [1] [{idA:N}]", null, null),
        };
        var etiquetas = EtiquetasSelectorTrabajador.Construir(trabajadores);
        var cut = Renderizar(etiquetas.Select(e => new OpcionBuscable(e.Id.ToString(), e.Texto)).ToArray());

        foreach (var etiqueta in etiquetas)
        {
            _valores.Clear();
            await cut.Find("input").InputAsync(new ChangeEventArgs { Value = etiqueta.Texto });
            cut.WaitForAssertion(() => _valores.Should().Equal(etiqueta.Id.ToString()));
        }
    }

    [Fact]
    public async Task Un_texto_sin_coincidencia_no_elige_ninguna()
    {
        var cut = Renderizar(new OpcionBuscable("a", "Ana Ruiz"));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "Ana" });

        cut.WaitForAssertion(() => _valores.Should().Equal(string.Empty));
    }
}
