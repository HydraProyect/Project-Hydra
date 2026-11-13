using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

using PaginaDocumentos = CaeManager.Web.Features.Documentos.Pages.Documentos;

/// <summary>
/// D-18: el menú «Acreditación ▾» en cada fila del listado agrupado por Plataforma CAE. La pantalla solo dibuja el menú, lo
/// despacha a los diálogos y a los Commands de siempre y decide a quién se enseña (D-3); aquí la consulta es un doble.
/// </summary>
public partial class DocumentosGen2Tests
{
    private static IElement FilaDe(IRenderedComponent<PaginaDocumentos> cut, string propietario) =>
        cut.FindAll(".documentos-tabla-visible tbody tr:has(button.enlace-nombre-fila)")
            .First(f => f.TextContent.Contains(propietario));

    private static EscenarioVinculos ConPropietarios(EscenarioVinculos e)
    {
        var original = e.Mediador.Documentos;
        e.Mediador.Documentos = q => original(q).Select(d => d with { PropietarioId = Guid.NewGuid() }).ToList();
        return e;
    }

    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("GestorCae", true)]
    [InlineData("Consulta", false)]
    [InlineData("Cliente", false)]
    public async Task Por_Plataforma_CAE_cada_fila_acreditada_lleva_el_menu_Acreditacion_solo_con_escritura(string rol, bool escritura)
    {
        var e = ConPropietarios(ConVinculos());
        var (cut, _) = Renderizar(e.Mediador, rol: rol);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, "Por Plataforma CAE");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        var filas = cut.FindAll(".documentos-tabla-visible tbody tr:has(button.enlace-nombre-fila)");
        var conMenu = filas.Where(f => f.QuerySelector(".menu-acreditacion") is not null).ToList();
        // Cuatro filas acreditadas (dos de Nalanda en el mismo Documento, la rechazada y la pendiente de Ctaima) y una sin vínculo.
        conMenu.Should().HaveCount(escritura ? 4 : 0);
        // Lo que la fila ya tenía sigue ahí, también «Gestionar».
        foreach (var fila in filas)
            fila.QuerySelectorAll(".documentos-accion-rapida").Should().HaveCount(escritura ? 1 : 0);
    }

    [Theory]
    [InlineData("Por Cliente")]
    [InlineData("Sin agrupar")]
    public async Task Fuera_del_agrupado_por_Plataforma_CAE_las_filas_no_ganan_el_menu(string agrupacion)
    {
        var e = ConPropietarios(ConVinculos());
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, agrupacion);

        cut.WaitForAssertion(() => cut.FindAll(".documentos-tabla-visible tbody button.enlace-nombre-fila").Should().NotBeEmpty());
        cut.FindAll(".menu-acreditacion").Should().BeEmpty();
    }

    [Fact]
    public async Task Cada_fila_ofrece_las_opciones_de_su_estado_con_el_nombre_de_su_plataforma()
    {
        var e = ConPropietarios(ConVinculos());
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        await Agrupar(cut, "Por Plataforma CAE");
        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());

        var rechazada = FilaDe(cut, "Ana Ruiz Pardo");
        await rechazada.QuerySelector<IElement>("button.menu-acciones-disparador")!.ClickAsync(new MouseEventArgs());
        cut.Find("[role=menu] .menu-acciones-titulo").TextContent.Trim().Should().Be("Acreditación en Nalanda");
        cut.FindAll("[role=menu] [role=menuitem]").Select(o => o.TextContent.Trim()).Should().Equal(
            "Subir versión corregida", "Marcar validado", "Marcar rechazado…", "Gestionar acreditación…", "Abrir portal ↗");
        cut.Find("[role=menu] a[role=menuitem]").GetAttribute("href").Should().StartWith("https://nalanda.example.com");
    }

    [Fact]
    public async Task Marcar_subido_despacha_el_comando_de_siempre_con_la_acreditacion_de_la_fila()
    {
        var e = ConPropietarios(ConVinculos());
        var enviados = new List<MarcarAcreditacionSubidaCommand>();
        e.Mediador.Interceptar = p => p is MarcarAcreditacionSubidaCommand c
            ? Task.FromResult<object?>(Registrar(enviados, c))
            : null;
        var (cut, mediador) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        await Agrupar(cut, "Por Plataforma CAE");
        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        var pendiente = e.Mediador.Documentos(UltimaConsultaDeDocumentos(mediador))
            .Single(d => d.Grupo?.Acreditacion?.Estado == EstadoAcreditacion.PendienteDeSubir).Grupo!.Acreditacion!.Id;

        await FilaDe(cut, "Luis Mora Gil").QuerySelector<IElement>("button.menu-acciones-disparador")!.ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menu] [role=menuitem]").Single(o => o.TextContent.Trim() == "Marcar subido").ClickAsync(new MouseEventArgs());

        enviados.Should().ContainSingle().Which.AcreditacionId.Should().Be(pendiente);
    }

    private static Result Registrar(List<MarcarAcreditacionSubidaCommand> enviados, MarcarAcreditacionSubidaCommand comando)
    {
        enviados.Add(comando);
        return Result.Exito();
    }
}
