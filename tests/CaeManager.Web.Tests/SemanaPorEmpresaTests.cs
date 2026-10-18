using Bunit;
using CaeManager.Application.Calendario.Queries;
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
        [new(Guid.NewGuid(), Lunes, true, "Aptitud médica", "Ana Gil"),
         new(Guid.NewGuid(), Lunes, false, "Formación PRL", "Ana Gil"),
         new(Guid.NewGuid(), Lunes.AddDays(4), false, "Seguro RC", "Montajes Ebro")], false);

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
        cut.Find(".semana-vencido").TextContent.Should().Be("2");
        cut.Find(".semana-proximo").TextContent.Should().Be("1");
        cut.Find(".semana-subtitulo").TextContent.Should().Contain("3 documentos esta semana").And.Contain("1 bloquea acceso");
        cut.Find(".semana-empresa a").GetAttribute("href").Should().Be($"/empresas/{_empresa.EmpresaId}");
        cut.Find(".semana-reclamar").TextContent.Should().Contain("Reclamar todos (3)");
    }

    [Fact]
    public void Cada_celda_abre_solo_los_documentos_de_ese_dia_y_empresa()
    {
        var cut = Semana();
        cut.Find("button.semana-celda").Click();
        var enlaces = cut.FindAll(".semana-documentos a");
        enlaces.Select(e => e.TextContent).Should().Equal("Aptitud médica", "Formación PRL");
        enlaces.Select(e => e.GetAttribute("href")).Should().Equal(
            _empresa.Documentos.Take(2).Select(d => $"/documentos?documentoId={d.DocumentoId}"));
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
