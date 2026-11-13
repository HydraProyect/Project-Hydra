using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class Centro360Gen2Tests
{
    private static DocumentacionRequeridaCentroDto RequisitoDeFormacion(Guid tipoId, bool bloquea) =>
        new(tipoId, "Formación", AmbitoAplicacion.Trabajador, true, true, true, null, bloquea, null, null);

    /// <summary>
    /// El panel «Requisitos del Centro» vive en MainLayout y guarda sin pasar por la página: la pestaña «Requisitos» de Centro 360
    /// se enteraba solo al recargar. Ahora el panel avisa por <see cref="ContextWorkspaceService.OnEntidadGuardada"/>.
    /// </summary>
    [Fact]
    public void Requisitos_se_recarga_cuando_el_panel_guarda_un_requisito_de_este_Centro()
    {
        var id = Guid.NewGuid();
        var tipo = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Sur");
        mediador.Requisitos[id] = [RequisitoDeFormacion(tipo, bloquea: true)];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=requisitos");
        var cut = Renderizar(id);
        cut.Find(".centro-requisito [data-estado]").TextContent.Should().Contain("Bloquea el acceso", "control positivo: carga inicial");
        var antes = mediador.Enviadas.OfType<ObtenerDocumentacionRequeridaDeCentroQuery>().Count();
        var lecturasDeLaFicha = mediador.Enviadas.OfType<ObtenerCentroPorIdQuery>().Count();

        // El panel guarda (en la base ya no bloquea) y avisa.
        mediador.Requisitos[id] = [RequisitoDeFormacion(tipo, bloquea: false)];
        cut.InvokeAsync(() => Services.GetRequiredService<ContextWorkspaceService>()
            .NotificarEntidadGuardada(EntidadWorkspace.Centro, id));

        cut.WaitForAssertion(() => cut.Find(".centro-requisito [data-estado]").TextContent.Should().Contain("No bloquea el acceso"));
        mediador.Enviadas.OfType<ObtenerDocumentacionRequeridaDeCentroQuery>().Count().Should().Be(antes + 1);
        mediador.Enviadas.OfType<ObtenerCentroPorIdQuery>().Count().Should().Be(lecturasDeLaFicha,
            "la relectura es silenciosa: no rehace la ficha entera ni la sustituye por el esqueleto de carga");
    }

    [Fact]
    public void Requisitos_no_se_recarga_cuando_el_aviso_es_de_otro_Centro_ni_de_otra_entidad()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Sur");
        mediador.Requisitos[id] = [RequisitoDeFormacion(Guid.NewGuid(), bloquea: true)];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=requisitos");
        var cut = Renderizar(id);
        var consultas = mediador.Enviadas.Count;

        cut.InvokeAsync(() =>
        {
            var servicio = Services.GetRequiredService<ContextWorkspaceService>();
            servicio.NotificarEntidadGuardada(EntidadWorkspace.Centro, Guid.NewGuid());
            servicio.NotificarEntidadGuardada(EntidadWorkspace.Empresa, id);
        });

        mediador.Enviadas.Should().HaveCount(consultas, "ni otro Centro ni otra entidad con el mismo Id son este Centro");
    }

    [Fact]
    public async Task Tras_salir_de_la_pagina_un_aviso_ya_no_la_recarga()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Sur");
        mediador.Requisitos[id] = [RequisitoDeFormacion(Guid.NewGuid(), bloquea: true)];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=requisitos");
        var cut = Renderizar(id);
        var consultas = mediador.Enviadas.Count;

        await DisposeComponentsAsync();
        Services.GetRequiredService<ContextWorkspaceService>().NotificarEntidadGuardada(EntidadWorkspace.Centro, id);

        mediador.Enviadas.Should().HaveCount(consultas, "la página desuscribe el aviso al desecharse");
    }
}
