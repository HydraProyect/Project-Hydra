using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (c) — badges de acreditación por plataforma.</summary>
public class BadgesAcreditacionTests : BunitContext
{
    [Fact]
    public void Sin_acreditaciones_no_pinta_nada()
    {
        var cut = Render<BadgesAcreditacion>(parametros => parametros.Add(p => p.Acreditaciones, []));

        cut.Markup.Should().BeEmpty();
    }

    [Fact]
    public void Muestra_una_badge_por_plataforma_con_su_estado_en_el_texto()
    {
        var cut = Render<BadgesAcreditacion>(parametros => parametros
            .Add(p => p.Acreditaciones, [
                new AcreditacionResumenDto(Guid.NewGuid(), "Nalanda", EstadoAcreditacion.Rechazada),
                new AcreditacionResumenDto(Guid.NewGuid(), "Dokify", EstadoAcreditacion.Aceptada)
            ]));

        // Femenino: concuerda con «acreditación», que es lo que el badge
        // nombra, y con el propio enum (Rechazada/Aceptada). Decisión del
        // propietario, 2026-08-29 — antes decía «rechazado»/«aceptado» y
        // código e interfaz discrepaban sobre el mismo valor.
        cut.Markup.Should().Contain("Nalanda").And.Contain("rechazada");
        cut.Markup.Should().Contain("Dokify").And.Contain("aceptada");
    }

    [Theory]
    [InlineData(EstadoAcreditacion.Rechazada)]
    [InlineData(EstadoAcreditacion.PendienteDeSubir)]
    [InlineData(EstadoAcreditacion.Aceptada)]
    [InlineData(EstadoAcreditacion.Subida)]
    [InlineData(EstadoAcreditacion.NoRequerida)]
    public void Nunca_usa_los_tonos_reservados_al_semaforo_de_vigencia_documental(EstadoAcreditacion estado)
    {
        var cut = Render<BadgesAcreditacion>(parametros => parametros
            .Add(p => p.Acreditaciones, [new AcreditacionResumenDto(Guid.NewGuid(), "Plataforma", estado)]));

        var badge = cut.Find(".badge");
        badge.ClassList.Should().NotContain("badge-exito", "reservado para vigencia documental (DESIGN_SYSTEM.md, no negociable)");
        badge.ClassList.Should().NotContain("badge-advertencia", "reservado para vigencia documental (DESIGN_SYSTEM.md, no negociable)");
        badge.ClassList.Should().NotContain("badge-peligro", "reservado para vigencia documental (DESIGN_SYSTEM.md, no negociable)");
    }
}
