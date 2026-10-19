using Bunit;
using CaeManager.Application.Calendario.Queries;
using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Visitas.Queries.ObtenerVisitasParaCalendario;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Bandeja.Components;
using CaeManager.Web.Features.Calendario.Components;
using CaeManager.Web.Features.Calendario.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class SemanaPorEmpresaTests : BunitContext
{
    private static readonly DateOnly Lunes = new(2026, 10, 19);
    private readonly Mediador _mediador = new();
    private readonly SemanaEmpresaDto _empresa = new(Guid.NewGuid(), "Montajes Ebro",
        [new(Guid.NewGuid(), Lunes, true, "Aptitud médica", "Ana Gil", DeTrabajador: true, Reclamable: false),
         new(Guid.NewGuid(), Lunes, false, "Formación PRL", "Montajes Ebro", DeTrabajador: false, Reclamable: true),
         new(Guid.NewGuid(), Lunes.AddDays(4), false, "Seguro RC", "Montajes Ebro", DeTrabajador: false, Reclamable: true)], false);

    public SemanaPorEmpresaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        AddAuthorization().SetAuthorized("marta").SetRoles(Roles.GestorCae);
    }

    private IRenderedComponent<SemanaPorEmpresa> Semana(SemanaEmpresaDto? empresa = null) =>
        Render<SemanaPorEmpresa>(p => p.Add(c => c.Lunes, Lunes).Add(c => c.Hoy, Lunes)
            .Add(c => c.Empresas, [empresa ?? _empresa]));

    [Fact]
    public void Cinco_dias_recuentos_bloqueos_y_enlace_a_la_empresa()
    {
        var cut = Semana();
        cut.FindAll("[role=columnheader]").Should().HaveCount(7);
        cut.FindAll("button.semana-celda").Select(e => e.TextContent).Should().Equal("2", "1");
        // Hoy es el lunes: vence hoy, así que es ámbar (como la pastilla «Vence hoy»); rojo solo lo anterior a hoy.
        cut.FindAll(".semana-vencido").Should().BeEmpty();
        cut.FindAll(".semana-proximo").Select(e => e.TextContent).Should().Equal("2", "1");
        cut.Find(".semana-subtitulo").TextContent.Should().Contain("3 documentos esta semana").And.Contain("1 bloquea acceso");
        cut.Find(".semana-empresa a").GetAttribute("href").Should().Be($"/empresas/{_empresa.EmpresaId}");
        cut.Find(".semana-reclamar").TextContent.Should().Contain("Reclamar todos (2)", "cuenta solo lo reclamable a los contactos de la Empresa: el documento del Trabajador no");
    }

    [Fact]
    public void Cada_celda_abre_solo_los_documentos_de_ese_dia_y_empresa()
    {
        var cut = Semana();
        cut.Find("button.semana-celda").Click();
        cut.FindAll(".detalle-dia-fila .detalle-dia-secundaria").Select(e => e.TextContent)
            .Should().Equal("Aptitud médica", "Formación PRL");
        var enlaces = cut.FindAll(".detalle-dia-renovar");
        enlaces.Select(e => e.GetAttribute("href")).Should().Equal(
            _empresa.Documentos.Take(2).Select(d => $"/documentos?documentoId={d.DocumentoId}"));
    }

    /// <summary>«Detalle del día» del mockup: antetítulo, fecha larga, resumen, filas con sujeto en negrita y documento debajo, estado, Renovar y Reclamar, y pie con «Reclamar los N».</summary>
    [Fact]
    public void El_detalle_de_la_celda_sigue_el_dialogo_Detalle_del_dia_del_mockup()
    {
        var cut = Render<SemanaPorEmpresa>(p => p.Add(c => c.Lunes, Lunes).Add(c => c.Hoy, Lunes)
            .Add(c => c.Empresas, [_empresa]).Add(c => c.OnReclamar, (SemanaEmpresaDto _) => { }));
        cut.Find("button.semana-celda").Click();

        var dialogo = cut.Find("[role=dialog]");
        dialogo.QuerySelector(".modal-sobretitulo")!.TextContent.Should().Be("Lunes · Montajes Ebro");
        dialogo.QuerySelector("h2")!.TextContent.Should().Be("19 de octubre de 2026");
        dialogo.QuerySelector(".modal-subtitulo")!.TextContent.Should().Be("2 vencimientos");
        dialogo.QuerySelector(".detalle-dia-etiqueta")!.TextContent.Should().Be("Vencimientos");

        var filas = dialogo.QuerySelectorAll(".detalle-dia-fila").ToArray();
        filas.Should().HaveCount(2);
        filas[0].QuerySelector(".detalle-dia-principal")!.TextContent.Should().Be("Ana Gil");
        filas[0].QuerySelector(".detalle-dia-secundaria")!.TextContent.Should().Be("Aptitud médica");
        filas[0].QuerySelector(".badge")!.TextContent.Should().Be("Vence hoy");
        filas[0].QuerySelector(".detalle-dia-renovar")!.TextContent.Should().Be("Renovar");
        filas[0].QuerySelector(".detalle-dia-reclamar").Should().BeNull("el documento de un Trabajador no se reclama a la Empresa");
        filas[1].QuerySelector(".detalle-dia-reclamar")!.TextContent.Should().Be("Reclamar");
        dialogo.QuerySelector(".modal-pie")!.TextContent.Should().Contain("Las acciones se ejecutan sin salir del calendario.").And.Contain("Reclamar el documento");
    }

    [Fact]
    public void Un_documento_de_un_dia_anterior_a_hoy_aparece_vencido_y_uno_posterior_con_su_fecha()
    {
        var cut = Render<SemanaPorEmpresa>(p => p.Add(c => c.Lunes, Lunes).Add(c => c.Hoy, Lunes.AddDays(1))
            .Add(c => c.Empresas, [_empresa]));
        cut.Find(".semana-vencido").TextContent.Should().Be("2", "el lunes ya es pasado: rojo, igual que la pastilla");
        cut.Find("button.semana-celda").Click();
        cut.Find(".detalle-dia-fila .badge").TextContent.Should().Be("Vencido");

        cut.FindAll("button.semana-celda")[1].Click();
        cut.Find(".detalle-dia-fila .badge").TextContent.Should().Be("Vence el 23 de octubre");
    }

    [Fact]
    public void Reclamar_en_una_fila_del_dia_pide_solo_ese_documento_de_esa_empresa()
    {
        var recibido = new List<SemanaEmpresaDto>();
        var cut = Render<SemanaPorEmpresa>(p => p.Add(c => c.Lunes, Lunes).Add(c => c.Hoy, Lunes)
            .Add(c => c.Empresas, [_empresa])
            .Add(c => c.OnReclamar, (SemanaEmpresaDto e) => recibido.Add(e)));
        cut.Find("button.semana-celda").Click();
        cut.Find(".detalle-dia-reclamar").Click();

        recibido.Should().ContainSingle().Which.Should().BeEquivalentTo(
            _empresa with { Documentos = [_empresa.Documentos[1]] });
        cut.FindAll("[role=dialog]").Should().BeEmpty("el cajón de reclamación sustituye al detalle");
    }

    [Fact]
    public void El_pie_Reclamar_los_N_pide_todos_los_reclamables_del_dia_y_no_los_de_trabajador()
    {
        var recibido = new List<SemanaEmpresaDto>();
        var empresa = _empresa with { Documentos = [.. _empresa.Documentos, new(Guid.NewGuid(), Lunes, false, "TC2", "Montajes Ebro", false, true)] };
        var cut = Render<SemanaPorEmpresa>(p => p.Add(c => c.Lunes, Lunes).Add(c => c.Hoy, Lunes)
            .Add(c => c.Empresas, [empresa])
            .Add(c => c.OnReclamar, (SemanaEmpresaDto e) => recibido.Add(e)));
        cut.Find("button.semana-celda").Click();
        cut.Find(".modal-pie").TextContent.Should().Contain("Reclamar los 2");
        cut.Find(".modal-pie button").Click();

        recibido.Single().Documentos.Select(d => d.DocumentoId).Should().Equal(empresa.Documentos[1].DocumentoId, empresa.Documentos[3].DocumentoId);
    }

    [Fact]
    public void Consulta_ve_el_detalle_del_dia_sin_Reclamar_ni_pie()
    {
        AddAuthorization().SetAuthorized("consulta").SetRoles(Roles.Consulta);
        var cut = Render<SemanaPorEmpresa>(p => p.Add(c => c.Lunes, Lunes).Add(c => c.Hoy, Lunes)
            .Add(c => c.Empresas, [_empresa])
            .Add(c => c.OnReclamar, (SemanaEmpresaDto _) => { }));
        cut.Find("button.semana-celda").Click();

        cut.FindAll(".detalle-dia-renovar").Should().HaveCount(2);
        cut.FindAll(".detalle-dia-reclamar").Should().BeEmpty();
        cut.FindAll(".modal-pie").Should().BeEmpty();
    }

    [Fact]
    public void Las_pestanas_exponen_aria_selected_como_true_o_false()
    {
        _mediador.Responder = r => r switch
        {
            ObtenerVencimientosMesQuery => Array.Empty<VencimientoCalendarioDto>(),
            ObtenerVisitasParaCalendarioQuery => Array.Empty<VisitaCalendarioDto>(),
            ObtenerSemanaPorEmpresaQuery => Array.Empty<SemanaEmpresaDto>(),
            _ => throw new NotSupportedException()
        };
        var cut = Render<Calendario>(p => p.Add(c => c.Hoy, Lunes));
        cut.FindAll("[role=tab]").Select(t => t.GetAttribute("aria-selected")).Should().Equal("true", "false");
        cut.FindAll("[role=tab][aria-selected=true]").Should().ContainSingle().Which.TextContent.Should().Be("Mes");

        cut.FindAll("[role=tab]")[1].Click();
        cut.FindAll("[role=tab]").Select(t => t.GetAttribute("aria-selected")).Should().Equal("false", "true");
        cut.FindAll("[role=tab][aria-selected=true]").Should().ContainSingle().Which.TextContent.Should().Be("Semana por Empresa");
    }

    [Fact]
    public void La_leyenda_va_bajo_la_tabla_de_la_semana_igual_que_bajo_la_rejilla_del_mes()
    {
        _mediador.Responder = r => r switch
        {
            ObtenerVencimientosMesQuery => Array.Empty<VencimientoCalendarioDto>(),
            ObtenerVisitasParaCalendarioQuery => Array.Empty<VisitaCalendarioDto>(),
            ObtenerSemanaPorEmpresaQuery => new[] { _empresa },
            _ => throw new NotSupportedException()
        };
        var cut = Render<Calendario>(p => p.Add(c => c.Hoy, Lunes));
        cut.FindAll(".calendario-leyenda").Should().ContainSingle();
        cut.FindAll("[role=tab]")[1].Click();
        cut.FindAll(".semana-tabla").Should().ContainSingle();
        cut.FindAll(".calendario-leyenda").Should().ContainSingle();
        cut.Markup.IndexOf("calendario-leyenda", StringComparison.Ordinal)
            .Should().BeGreaterThan(cut.Markup.IndexOf("semana-tabla", StringComparison.Ordinal), "la leyenda está bajo la tabla");
    }

    [Fact]
    public void Reclamado_hoy_cambia_el_texto_sin_ocultar_la_accion()
    {
        var cut = Semana(_empresa with { ReclamadoHoy = true });
        cut.Find(".semana-subtitulo").TextContent.Should().Contain("reclamado hoy");
        cut.Find(".semana-reclamado").TextContent.Should().Contain("Reclamar de nuevo");
    }

    [Fact]
    public void Consulta_ve_los_datos_sin_la_accion_de_envio()
    {
        AddAuthorization().SetAuthorized("consulta").SetRoles(Roles.Consulta);
        var cut = Semana();
        cut.FindAll("button.semana-celda").Should().HaveCount(2);
        cut.FindAll(".semana-reclamar").Should().BeEmpty();
    }

    [Fact]
    public void Semana_vacia_no_inventa_empresas()
    {
        var cut = Render<SemanaPorEmpresa>();
        cut.Markup.Should().Contain("Nada vence esta semana");
        cut.FindAll("[role=row]").Should().BeEmpty();
    }

    [Fact]
    public void Navega_por_semanas_cruzando_el_ano_y_hoy_vuelve_al_reloj_fijado()
    {
        _mediador.Responder = r => r switch
        {
            ObtenerVencimientosMesQuery => Array.Empty<VencimientoCalendarioDto>(),
            ObtenerVisitasParaCalendarioQuery => Array.Empty<VisitaCalendarioDto>(),
            ObtenerSemanaPorEmpresaQuery => Array.Empty<SemanaEmpresaDto>(),
            _ => throw new NotSupportedException()
        };
        var cut = Render<Calendario>(p => p.Add(c => c.Hoy, new DateOnly(2026, 12, 31)));
        cut.FindAll("[role=tab]")[1].Click();
        cut.Find("button[aria-label='Semana siguiente']").Click();
        _mediador.Recibidas.OfType<ObtenerSemanaPorEmpresaQuery>().Select(q => q.Fecha)
            .Should().Equal(new DateOnly(2026, 12, 28), new DateOnly(2027, 1, 4));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Hoy").Click();
        _mediador.Recibidas.OfType<ObtenerSemanaPorEmpresaQuery>().Last().Fecha.Should().Be(new DateOnly(2026, 12, 28));
        cut.FindAll("[role=tab]")[0].Click();
        cut.FindAll(".calendario-rejilla").Should().ContainSingle();
    }

    [Fact]
    public void Error_semanal_permite_reintentar_la_misma_semana()
    {
        _mediador.Responder = r => r switch
        {
            ObtenerVencimientosMesQuery => Array.Empty<VencimientoCalendarioDto>(),
            ObtenerVisitasParaCalendarioQuery => Array.Empty<VisitaCalendarioDto>(),
            _ => throw new InvalidOperationException("Fallo simulado")
        };
        var cut = Render<Calendario>(p => p.Add(c => c.Hoy, Lunes));
        cut.FindAll("[role=tab]")[1].Click();
        cut.Markup.Should().Contain("No pudimos cargar el calendario");
        _mediador.Responder = _ => new[] { _empresa };
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").Click();
        cut.FindAll(".semana-empresa").Should().ContainSingle();
    }

    [Fact]
    public async Task Una_respuesta_semanal_tardia_no_sustituye_la_vista_mensual()
    {
        var respuesta = new TaskCompletionSource<IReadOnlyList<SemanaEmpresaDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediador.Responder = r => r switch
        {
            ObtenerVencimientosMesQuery => Array.Empty<VencimientoCalendarioDto>(),
            ObtenerVisitasParaCalendarioQuery => Array.Empty<VisitaCalendarioDto>(),
            ObtenerSemanaPorEmpresaQuery => respuesta.Task,
            _ => throw new NotSupportedException()
        };
        var cut = Render<Calendario>(p => p.Add(c => c.Hoy, Lunes));
        var semana = cut.FindAll("[role=tab]")[1].ClickAsync(new());
        cut.WaitForAssertion(() => cut.FindAll(".esqueleto-lista").Should().NotBeEmpty());
        await cut.FindAll("[role=tab]")[0].ClickAsync(new());
        respuesta.SetResult([_empresa]);
        await semana.WaitAsync(TimeSpan.FromSeconds(5));
        cut.FindAll(".calendario-rejilla").Should().ContainSingle();
        cut.FindAll(".semana-empresa").Should().BeEmpty();
    }

    [Fact]
    public void Reclamar_abre_revision_acotada_incluso_si_el_mismo_titular_tiene_dos_ambitos()
    {
        var ajeno = Guid.NewGuid();
        _mediador.Responder = r => r is ObtenerLoteReclamacionPorFiltroQuery q
            ? new[] { Lote(q.Filtro.Ambito, q.Filtro.Ambito == AmbitoAplicacion.Empresa ? _empresa.Documentos[2].DocumentoId : _empresa.Documentos[0].DocumentoId, ajeno) }
            : throw new NotSupportedException();
        var cut = Render<DrawerReclamacionLote>(p => p.Add(c => c.Visible, true)
            .Add(c => c.AmbitosDisponibles, [AmbitoAplicacion.Empresa, AmbitoAplicacion.Trabajador])
            .Add(c => c.TitularIdInicial, _empresa.EmpresaId)
            .Add(c => c.DocumentoIdsIniciales, _empresa.Documentos.Select(d => d.DocumentoId).ToArray()));

        cut.FindAll(".tabla-lote-reclamacion tbody tr").Should().HaveCount(2);
        cut.Markup.Should().NotContain("Documento ajeno");
        cut.FindAll(".selector-lote-documental").Should().BeEmpty();
        _mediador.Recibidas.Should().OnlyContain(r => r is ObtenerLoteReclamacionPorFiltroQuery);
        // La selección de un ámbito no desmarca el Documento del otro ámbito del mismo titular.
        cut.FindAll(".tabla-lote-reclamacion input[type=checkbox]")[0].Change(false);
        cut.FindAll("button").Where(b => b.TextContent.Trim().StartsWith("Enviar ("))
            .Select(b => b.TextContent.Trim()).Should().Equal("Enviar (0)", "Enviar (1)");
    }

    /// <summary>
    /// Hallazgo de revisión visual: desde la fila «Construcciones Gargamel 08 S.A.» el cajón abría la tarjeta «LexCorp Iberia S.A.»
    /// con un contacto de otra compañía. Causa: la revisión pedía los lotes de cada ámbito sin acotar y solo intersectaba los
    /// Documentos, y el lote de ámbito Trabajador tiene por titular al Cliente del Centro (LexCorp) con los contactos de ese
    /// Cliente. Una reclamación sale a los contactos de la Empresa de la fila, no a los de otro titular. La fila, además, solo
    /// ofrece (y cuenta) lo reclamable a esa Empresa: el documento del Trabajador no.
    /// </summary>
    [Fact]
    public void Reclamar_desde_la_fila_solo_ofrece_la_Empresa_de_la_fila_y_nunca_el_Cliente_del_Centro()
    {
        var gargamel = new SemanaEmpresaDto(Guid.NewGuid(), "Construcciones Gargamel 08 S.A.",
            [new(Guid.NewGuid(), Lunes, false, "Seguro RC", "Construcciones Gargamel 08 S.A.", DeTrabajador: false, Reclamable: true),
             new(Guid.NewGuid(), Lunes, false, "Aptitud médica", "Ana Gil", DeTrabajador: true, Reclamable: false)], false);
        var lexcorp = Guid.NewGuid();
        var contactoGargamel = new DestinatarioAgendaDto(Guid.NewGuid(), "Gerencia Gargamel", "gerencia@gargamel.example", ["RLC/TC1"]);
        var contactoLexcorp = new DestinatarioAgendaDto(Guid.NewGuid(), "Compras LexCorp", "compras@lexcorp.example", ["Contacto predeterminado"]);
        _mediador.Responder = r => r switch
        {
            ObtenerVencimientosMesQuery => Array.Empty<VencimientoCalendarioDto>(),
            ObtenerVisitasParaCalendarioQuery => Array.Empty<VisitaCalendarioDto>(),
            ObtenerSemanaPorEmpresaQuery => new[] { gargamel },
            ObtenerLoteReclamacionPorFiltroQuery { Filtro.Ambito: AmbitoAplicacion.Empresa } => new[]
            {
                new LoteReclamacionAgrupadoDto(gargamel.EmpresaId, gargamel.EmpresaNombre, AmbitoAplicacion.Empresa, null,
                    [new(gargamel.Documentos[0].DocumentoId, null, null, Guid.NewGuid(), "Seguro RC", Lunes, EstadoDocumento.Proximo)],
                    null, [contactoGargamel])
            },
            ObtenerLoteReclamacionPorFiltroQuery => new[]
            {
                new LoteReclamacionAgrupadoDto(lexcorp, "LexCorp Iberia S.A.", AmbitoAplicacion.Trabajador, null,
                    [new(gargamel.Documentos[0].DocumentoId, Guid.NewGuid(), "Ana Gil", Guid.NewGuid(), "Aptitud médica", Lunes, EstadoDocumento.Proximo),
                     new(gargamel.Documentos[1].DocumentoId, Guid.NewGuid(), "Ana Gil", Guid.NewGuid(), "Aptitud médica", Lunes, EstadoDocumento.Proximo)],
                    null, [contactoLexcorp])
            },
            _ => throw new NotSupportedException()
        };
        var cut = Render<Calendario>(p => p.Add(c => c.Hoy, Lunes));
        cut.FindAll("[role=tab]")[1].Click();
        cut.Find(".semana-reclamar").TextContent.Should().Contain("Reclamar todos (1)");
        cut.Find(".semana-reclamar").Click();

        cut.WaitForAssertion(() => cut.FindAll(".tarjeta-reclamacion").Should().ContainSingle());
        cut.Markup.Should().Contain("Construcciones Gargamel 08 S.A.").And.Contain("gerencia@gargamel.example");
        cut.Markup.Should().NotContain("LexCorp Iberia S.A.").And.NotContain("compras@lexcorp.example");
        // La Empresa se pide acotada a sí misma: el cajón no carga las demás Empresas del Tenant para filtrar después.
        _mediador.Recibidas.OfType<ObtenerLoteReclamacionPorFiltroQuery>()
            .Single(q => q.Filtro.Ambito == AmbitoAplicacion.Empresa).Filtro.EntidadId.Should().Be(gargamel.EmpresaId);
    }

    [Fact]
    public void Una_fila_sin_nada_reclamable_no_ofrece_Reclamar()
    {
        var soloTrabajador = _empresa with { Documentos = [_empresa.Documentos[0]] };
        var cut = Semana(soloTrabajador);
        cut.FindAll("button.semana-celda").Should().ContainSingle();
        cut.FindAll(".semana-reclamar").Should().BeEmpty();
        cut.Find(".semana-subtitulo").TextContent.Should().Contain("1 documento esta semana");
    }

    [Fact]
    public void Un_solo_documento_se_dice_en_singular_en_la_fila_y_en_la_celda()
    {
        var cut = Semana(_empresa with { Documentos = [_empresa.Documentos[2]] });
        cut.Find(".semana-subtitulo").TextContent.Should().Contain("1 documento esta semana").And.NotContain("documentos esta semana");
        cut.Find("button.semana-celda").GetAttribute("aria-label").Should().StartWith("1 documento vence ");
    }

    /// <summary>El cajón dice POR QUÉ deja fuera cada documento, y el callejón sin salida no manda a «probar otro ámbito».</summary>
    [Fact]
    public void El_cajon_distingue_los_de_trabajadores_de_los_fuera_de_ventana()
    {
        var empresa = Guid.NewGuid();
        var propio = Guid.NewGuid();
        var deTrabajador = Guid.NewGuid();
        var fueraDeVentana = Guid.NewGuid();
        _mediador.Responder = r => r is ObtenerLoteReclamacionPorFiltroQuery { Filtro.Ambito: AmbitoAplicacion.Empresa }
            ? new[] { new LoteReclamacionAgrupadoDto(empresa, "Montajes Ebro", AmbitoAplicacion.Empresa, null,
                [new(propio, null, null, Guid.NewGuid(), "Seguro RC", Lunes, EstadoDocumento.Proximo)], null, []) }
            : new[] { new LoteReclamacionAgrupadoDto(Guid.NewGuid(), "LexCorp Iberia S.A.", AmbitoAplicacion.Trabajador, null,
                [new(deTrabajador, Guid.NewGuid(), "Ana", Guid.NewGuid(), "Aptitud médica", Lunes, EstadoDocumento.Proximo)], null, []) };
        var cut = Render<DrawerReclamacionLote>(p => p.Add(c => c.Visible, true)
            .Add(c => c.AmbitosDisponibles, [AmbitoAplicacion.Empresa, AmbitoAplicacion.Trabajador])
            .Add(c => c.TitularIdInicial, empresa)
            .Add(c => c.DocumentoIdsIniciales, new[] { propio, deTrabajador, fueraDeVentana }));

        cut.FindAll(".tarjeta-reclamacion").Should().ContainSingle();
        var notas = cut.FindAll("[role=note]").Select(n => n.TextContent).ToArray();
        notas.Should().Contain("1 de 3 documentos son de Trabajadores y no entran en este lote de documentos de la Empresa: se reclaman desde «Gestionar» en la ficha de cada Trabajador.");
        notas.Should().Contain("1 de 3 documentos están fuera de la ventana de reclamación o ya no son reclamables.");
        cut.Markup.Should().NotContain("LexCorp");
    }

    [Fact]
    public void Si_ningun_lote_es_de_la_Empresa_de_la_fila_no_se_ofrece_ningun_destinatario_ajeno_ni_se_manda_a_probar_otro_ambito()
    {
        var empresa = Guid.NewGuid();
        var otro = Guid.NewGuid();
        _mediador.Responder = r => r is ObtenerLoteReclamacionPorFiltroQuery
            ? new[] { new LoteReclamacionAgrupadoDto(otro, "LexCorp Iberia S.A.", AmbitoAplicacion.Trabajador, null,
                [new(_empresa.Documentos[0].DocumentoId, Guid.NewGuid(), "Ana", Guid.NewGuid(), "Aptitud médica", Lunes, EstadoDocumento.Proximo)],
                null, [new DestinatarioAgendaDto(Guid.NewGuid(), "Compras", "compras@lexcorp.example", [])]) }
            : throw new NotSupportedException();
        var cut = Render<DrawerReclamacionLote>(p => p.Add(c => c.Visible, true)
            .Add(c => c.AmbitosDisponibles, [AmbitoAplicacion.Empresa, AmbitoAplicacion.Trabajador])
            .Add(c => c.TitularIdInicial, empresa)
            .Add(c => c.DocumentoIdsIniciales, new[] { _empresa.Documentos[0].DocumentoId }));

        cut.FindAll(".tarjeta-reclamacion").Should().BeEmpty();
        cut.Markup.Should().NotContain("compras@lexcorp.example").And.NotContain("LexCorp");
        cut.Find("[role=note]").TextContent.Should().Contain("1 de 1 documentos son de Trabajadores");
        cut.Markup.Should().Contain("Nada que reclamar a esta Empresa").And.NotContain("Prueba con otro tipo de documento o ámbito");
    }

    [Fact]
    public void Abrir_el_cajon_con_documentos_exige_titular()
    {
        var abrir = () => Render<DrawerReclamacionLote>(p => p.Add(c => c.Visible, true)
            .Add(c => c.AmbitosDisponibles, [AmbitoAplicacion.Empresa])
            .Add(c => c.DocumentoIdsIniciales, new[] { Guid.NewGuid() }));

        abrir.Should().Throw<Exception>().Where(e => e.ToString().Contains("TitularIdInicial"));
    }
    private LoteReclamacionAgrupadoDto Lote(AmbitoAplicacion ambito, Guid documento, Guid ajeno) =>
        new(_empresa.EmpresaId, _empresa.EmpresaNombre, ambito, null,
            [new(documento, null, "Ana", Guid.NewGuid(), "Documento de la semana", Lunes, EstadoDocumento.Proximo),
             new(ajeno, null, "Ana", Guid.NewGuid(), "Documento ajeno", Lunes, EstadoDocumento.Proximo)], null, []);

    private sealed class Mediador : IMediator
    {
        public Func<object, object> Responder { get; set; } = _ => throw new NotSupportedException();
        public List<object> Recibidas { get; } = [];
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add(request);
            var respuesta = Responder(request);
            return respuesta is Task<TResponse> tarea ? tarea : Task.FromResult((TResponse)respuesta);
        }
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
