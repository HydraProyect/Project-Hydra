using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class DelegacionesGen2Tests
{
    [Fact]
    public void El_listado_vacio_conserva_el_pie_separado()
    {
        var (cut, _, _) = Renderizar();
        cut.Find(".tarjeta-listado-pie-separado .paginador-texto").TextContent.Should().EndWith("Mostrando 0 de 0");
        cut.FindComponent<PaginadorSimple>().Instance.TotalElementos.Should().Be(0);
    }

    [Fact]
    public async Task La_primera_carga_no_ensena_un_total_que_aun_no_conoce()
    {
        _abrirDetalles = false;
        var respuesta = new TaskCompletionSource();
        _configurarMediador = m => m.EsperaLista = respuesta;
        var (cut, _, _) = Renderizar();
        try
        {
            cut.FindAll(".esqueleto-lista").Should().ContainSingle();
            cut.FindAll(".tarjeta-listado-pie").Should().BeEmpty();
        }
        finally
        {
            await cut.InvokeAsync(() => respuesta.TrySetResult());
        }
        cut.WaitForAssertion(() => cut.Find(".tarjeta-listado-pie-separado").Should().NotBeNull());
    }

    [Fact]
    public void El_listado_nace_con_detalles_contraidos_y_pie_separado()
    {
        _abrirDetalles = false;
        var (cut, _, _) = Renderizar(Delegacion(), Delegacion(soporte: true));
        cut.Find(".lista-design").Should().NotBeNull();
        cut.FindAll(".delegaciones-fila").Should().HaveCount(2);
        cut.FindAll(".delegaciones-abrir").Should().OnlyContain(b => b.GetAttribute("aria-expanded") == "false");
        cut.FindAll(".delegaciones-detalle").Should().BeEmpty();
        cut.Find(".tarjeta-listado-pie-separado").Closest(".tarjeta-listado").Should().BeNull();
        cut.FindComponent<FranjaEstado>().Instance.Recuentos.Should().Contain("Activa", 1).And.Contain("Abierto", 1);
    }

    [Fact]
    public async Task La_franja_filtra_sin_perder_los_recuentos()
    {
        _abrirDetalles = false;
        var (cut, _, _) = Renderizar(Delegacion(), Delegacion(soporte: true));
        await cut.InvokeAsync(() => cut.FindComponent<FranjaEstado>().Instance.ValorChanged.InvokeAsync("Abierto"));
        cut.FindAll(".delegaciones-fila").Should().ContainSingle().Which.TextContent.Should().Contain("Soporte");
        cut.FindComponent<FranjaEstado>().Instance.Recuentos.Should().Contain("Activa", 1).And.Contain("Abierto", 1);
        cut.FindComponent<FranjaEstado>().Instance.Valor.Should().Be("Abierto");
    }

    [Fact]
    public async Task Agrupar_no_corta_grupos_por_pagina_y_recuerda_los_abiertos()
    {
        _abrirDetalles = false;
        var filas = Enumerable.Range(0, 25).Select(_ => Delegacion()).Append(Delegacion(soporte: true)).ToArray();
        var (cut, _, _) = Renderizar(filas);
        cut.FindAll(".delegaciones-fila").Should().HaveCount(20);
        var agrupar = cut.FindComponents<PastillaFiltro>().Single(p => p.Instance.Etiqueta == "Agrupar");
        await cut.InvokeAsync(() => agrupar.Instance.ValorChanged.InvokeAsync("tipo"));
        cut.FindAll(".grupo-lista-cabecera").Should().HaveCount(2).And.OnlyContain(b => b.GetAttribute("aria-expanded") == "false");
        cut.FindAll(".delegaciones-fila").Should().BeEmpty();
        cut.FindAll(".grupo-lista-cabecera")[0].Click();
        cut.FindAll(".delegaciones-fila").Should().HaveCount(25);
        cut.FindAll(".grupo-lista-cabecera")[0].Click();
        cut.FindAll(".grupo-lista-cabecera")[0].Click();
        await cut.InvokeAsync(() => cut.FindComponent<FranjaEstado>().Instance.ValorChanged.InvokeAsync("Activa"));
        cut.FindAll(".delegaciones-fila").Should().HaveCount(25, "filtrar conserva los grupos abiertos durante esta visita");
    }

    [Fact]
    public void Las_personas_del_detalle_se_revelan_con_ver_los_restantes()
    {
        _abrirDetalles = false;
        var fila = Delegacion();
        fila = fila with { Operadores = Enumerable.Range(0, 5).Select(_ => fila.Operadores[0] with { AsignacionId = Guid.NewGuid() }).ToArray() };
        var (cut, _, _) = Renderizar(fila);
        cut.Find(".delegaciones-abrir").Click();
        cut.FindAll(".delegaciones-tabla tbody tr").Should().HaveCount(3);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Ver los 2 restantes")).Click();
        cut.FindAll(".delegaciones-tabla tbody tr").Should().HaveCount(5);
    }

    [Fact]
    public async Task Una_vista_guardada_aplica_busqueda_estado_y_agrupacion_sin_guardar_la_precarga_de_autorizacion()
    {
        _abrirDetalles = false;
        var (cut, _, _) = Renderizar(Delegacion(), Delegacion(soporte: true));
        var vista = cut.FindComponent<FiltrosGuardadosDeListado>();
        vista.Instance.ParametrosDeVista.Should().BeEquivalentTo("q", "estado", "tipo", "agrupar");
        await cut.InvokeAsync(() => vista.Instance.OnAplicar.InvokeAsync(new Dictionary<string, string?>
        { ["q"] = "Norte", ["estado"] = "Abierto,Inventado", ["agrupar"] = "tipo", ["tipo"] = "Inventado" }));
        cut.FindComponent<FranjaEstado>().Instance.Valor.Should().Be("Abierto");
        cut.FindAll(".grupo-lista-cabecera").Should().ContainSingle().Which.TextContent.Should().Contain("Soporte");
        cut.FindAll(".delegaciones-fila").Should().BeEmpty();
        var navegacion = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navegacion.Uri.Should().Contain("estado=Abierto").And.Contain("agrupar=tipo").And.NotContain("Inventado");
    }
}
