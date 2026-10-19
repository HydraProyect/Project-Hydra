using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Web.Features.Bandeja;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class MiTrabajoGen2Tests
{
    [Fact]
    public void PropagacionesConservaDestinosDelMismoDocumentoSinEnviarNiCompletar()
    {
        var documento = Guid.NewGuid();
        var pendientes = new[] { "Norte", "Sur" }.Select(centro =>
            Item(centro, TipoItemBandeja.PlataformaPendiente, "RNT", "Empresa destinataria", documentoId: documento,
                proveedor: "Nalanda") with
            {
                CentroNombre = centro,
                AcreditacionId = Guid.NewGuid(),
                EsPropagacionPendiente = true
            }).ToArray();
        var datos = new MiTrabajoAgregadoDto([Tenant(TenantRefri, "Refrielectric", false, pendientes)]);
        var cut = Renderizar(() => datos);

        cut.Find(".propagaciones h2").TextContent.Should().Contain("Propagaciones pendientes · 2");
        foreach (var pendiente in pendientes)
        {
            var fila = cut.FindAll(".propagaciones-fila").Single(e => e.TextContent.Contains(pendiente.CentroNombre!));
            fila.TextContent.Should().Contain("Refrielectric").And.Contain("Nalanda");
            // Dos puertas cross-Tenant por fila, la misma que revalida el endpoint: el título (enlace al documento) y «Gestionar».
            var formularios = fila.QuerySelectorAll("form").ToList();
            formularios.Should().HaveCount(2);
            var titulo = formularios[0];
            titulo.QuerySelector("button.enlace-documento")!.TextContent.Should().Contain("RNT");
            titulo.QuerySelector("input[name=returnUrl]")!.GetAttribute("value").Should().StartWith($"/documentos?documentoId={documento}");
            var gestionar = formularios[1];
            gestionar.QuerySelector("button")!.TextContent.Trim().Should().Be("Gestionar");
            foreach (var formulario in formularios)
            {
                formulario.GetAttribute("action").Should().Be("/cuenta/cliente-activo");
                formulario.QuerySelector("input[name=tenantId]")!.GetAttribute("value").Should().Be(TenantRefri.ToString());
                formulario.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value").Should().Be("token-de-prueba");
            }
            gestionar.QuerySelector("input[name=returnUrl]")!.GetAttribute("value").Should().Contain(pendiente.AcreditacionId.ToString());
        }
        cut.Find(".propagaciones").OuterHtml.Should().NotContain("mailto:");
        cut.Render();
        cut.FindAll(".propagaciones-fila").Should().HaveCount(2, "renderizar no completa ni envía una propagación");
    }

    [Fact]
    public void PropagacionesExcluyeOrigenYEstadosQueNoSonPendientes()
    {
        var pendiente = Item("p", TipoItemBandeja.PlataformaPendiente, "RNT", documentoId: Guid.NewGuid())
            with
        { EsPropagacionPendiente = true };
        var datos = new MiTrabajoAgregadoDto([
            Tenant(TenantOrigen, "Origen", true, [pendiente]),
            Tenant(TenantRefri, "Refrielectric", false,
                [pendiente with { Tipo = TipoItemBandeja.PlataformaRechazada, EsPropagacionPendiente = false }],
                seguimiento: [pendiente with { Tipo = TipoItemBandeja.EnPlataformaSeguimiento, EsPropagacionPendiente = false }])
        ]);
        new MiTrabajoVista(datos).Propagaciones(new()).Should().BeEmpty();
        var cut = Renderizar(() => datos);
        cut.Find(".propagaciones-vacio").TextContent.Should().Be("Todo actualizado en los Centros y plataformas.");
    }

    [Fact]
    public void PropagacionesVaciaConConsultaFallidaNoAfirmaQueTodoEsteActualizado()
    {
        var datos = new MiTrabajoAgregadoDto([], [new TenantNoConsultadoDto(TenantRefri, "Refrielectric", false)]);
        var cut = RenderizarPlegado(() => datos);
        cut.Find(".propagaciones-vacio").TextContent.Should().Contain("La consulta está incompleta.");
    }

    [Fact]
    public void PropagacionesFiltradasNoAfirmaQueTodoEsteActualizado()
    {
        var cut = Renderizar(url: "/mi-trabajo?q=inexistente");
        cut.Find(".propagaciones-vacio").TextContent.Should().Be("No hay propagaciones con estos filtros.");
    }

    [Fact]
    public void PropagacionesSinCarteraNoAfirmaQueTodoEsteActualizado()
    {
        var cut = RenderizarPlegado(() => new MiTrabajoAgregadoDto([]));
        cut.Find(".propagaciones-vacio").TextContent.Should().NotContain("Todo actualizado");
    }
}
