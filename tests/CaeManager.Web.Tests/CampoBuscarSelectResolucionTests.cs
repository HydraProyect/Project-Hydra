using Bunit;
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

    [Fact]
    public async Task Un_texto_sin_coincidencia_no_elige_ninguna()
    {
        var cut = Renderizar(new OpcionBuscable("a", "Ana Ruiz"));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "Ana" });

        cut.WaitForAssertion(() => _valores.Should().Equal(string.Empty));
    }
}
