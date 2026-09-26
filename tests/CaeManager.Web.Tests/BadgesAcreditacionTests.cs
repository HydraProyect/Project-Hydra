using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (c) — badges de acreditación por plataforma.</summary>
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

    /// <summary>
    /// P0-9b (FS-02): la Rechazada tenía el mismo tono que la Pendiente de subir.
    /// Bloquea el cumplimiento (D-7), así que lleva el tono de peligro del
    /// semáforo (DDL-010: vigencia y cumplimiento) y nunca el de la Pendiente.
    /// </summary>
    [Fact]
    public void La_rechazada_tiene_tono_propio_de_peligro_distinto_del_de_la_pendiente()
    {
        var cut = Render<BadgesAcreditacion>(parametros => parametros
            .Add(p => p.Acreditaciones, [
                new AcreditacionResumenDto(Guid.NewGuid(), "Nalanda", EstadoAcreditacion.Rechazada),
                new AcreditacionResumenDto(Guid.NewGuid(), "Dokify", EstadoAcreditacion.PendienteDeSubir)
            ]));

        var badges = cut.FindAll(".badge");
        var rechazada = badges.Single(b => b.TextContent.Contains("Nalanda"));
        var pendiente = badges.Single(b => b.TextContent.Contains("Dokify"));
        rechazada.ClassList.Should().Contain("badge-peligro");
        pendiente.ClassList.Should().NotContain("badge-peligro");
        rechazada.ClassName.Should().NotBe(pendiente.ClassName);
    }

    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir)]
    [InlineData(EstadoAcreditacion.Aceptada)]
    [InlineData(EstadoAcreditacion.Subida)]
    [InlineData(EstadoAcreditacion.NoRequerida)]
    public void Fuera_de_la_rechazada_nunca_usa_los_tonos_del_semaforo(EstadoAcreditacion estado)
    {
        var cut = Render<BadgesAcreditacion>(parametros => parametros
            .Add(p => p.Acreditaciones, [new AcreditacionResumenDto(Guid.NewGuid(), "Plataforma", estado)]));

        var badge = cut.Find(".badge");
        badge.ClassList.Should().NotContain("badge-exito", "reservado a vigencia y cumplimiento (DDL-010)");
        badge.ClassList.Should().NotContain("badge-advertencia", "reservado a vigencia y cumplimiento (DDL-010)");
        badge.ClassList.Should().NotContain("badge-peligro", "reservado a vigencia y cumplimiento (DDL-010)");
    }
}
