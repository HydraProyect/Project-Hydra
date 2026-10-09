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

    /// <summary>
    /// Defecto L1 del piloto Outbound (2026-10-08): quien preselecciona una entidad antes de haber
    /// cargado la lista (el lote de reclamación abierto desde «Pedir») dejaba el campo vacío para
    /// siempre, porque el texto solo se resolvía cuando cambiaba el Id y no cuando llegaban las opciones.
    /// </summary>
    [Fact]
    public void Un_Id_recibido_antes_que_sus_opciones_se_pinta_cuando_llegan()
    {
        var cut = Render<CampoBuscarSelect>(p => p
            .Add(c => c.Etiqueta, "Trabajador")
            .Add(c => c.Valor, "b")
            .Add(c => c.Opciones, []));
        (cut.Find("input").GetAttribute("value") ?? string.Empty).Should().BeEmpty(
            "control: sin opciones todavía no hay texto que pintar");

        cut.Render(p => p.Add(c => c.Opciones, [new OpcionBuscable("a", "Ana Ruiz"), new OpcionBuscable("b", "Luis Gil")]));

        cut.Find("input").GetAttribute("value").Should().Be("Luis Gil");
    }

    /// <summary>
    /// La otra cara: en cuanto la persona escribe, manda lo que escribe. Si las opciones llegan
    /// mientras su pulsación espera el rebote (el padre todavía no sabe nada y sigue pasando el Id
    /// de partida), no se le pisa el texto con el de ese Id.
    /// </summary>
    [Fact]
    public async Task Si_las_opciones_llegan_mientras_se_escribe_no_se_pisa_lo_escrito()
    {
        var cut = Render<CampoBuscarSelect>(p => p
            .Add(c => c.Etiqueta, "Trabajador")
            .Add(c => c.Valor, "b")
            .Add(c => c.Opciones, [])
            .Add(c => c.ValorChanged, EventCallback.Factory.Create<string>(this, v => _valores.Add(v))));

        var escritura = cut.Find("input").InputAsync(new ChangeEventArgs { Value = "Ana" });
        _valores.Should().BeEmpty("control: la pulsación sigue en el rebote y el padre no ha recibido nada");

        cut.Render(p => p.Add(c => c.Opciones, [new OpcionBuscable("a", "Ana Ruiz"), new OpcionBuscable("b", "Luis Gil")]));

        (cut.Find("input").GetAttribute("value") ?? string.Empty).Should().BeEmpty(
            "el campo no refleja lo tecleado (ver ManejarEscrituraAsync), pero tampoco debe imponer «Luis Gil»");
        await escritura;
    }
}
