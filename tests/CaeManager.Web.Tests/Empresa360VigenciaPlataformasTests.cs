using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Application.Empresas.Queries.ObtenerVigenciaEmpresa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Empresas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>Pestañas «Vigencia» y «Plataformas» de Empresa 360: lo que la maqueta pide y la regla de cada celda.</summary>
public class Empresa360VigenciaPlataformasTests : BunitContext
{
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid C1 = Guid.NewGuid();
    private static readonly Guid C2 = Guid.NewGuid();
    private static readonly Guid TipoReco = Guid.NewGuid();
    private static readonly Guid TipoArt19 = Guid.NewGuid();
    private static readonly Guid TipoAltura = Guid.NewGuid();
    private static readonly Guid TipoCarretilla = Guid.NewGuid();

    private sealed class MediadorFalso(Func<object, object?> responder) : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)responder(request)!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private MediadorFalso Registrar(Func<object, object?> responder)
    {
        var mediador = new MediadorFalso(responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddLocalization();
        return mediador;
    }

    // ── Vigencia ──────────────────────────────────────────────────────────

    private static VigenciaCeldaDto Celda(EstadoCeldaVigencia estado, int? diasHastaVencer, Guid[] exigidaPor, int mesesVigencia = 12) =>
        estado is EstadoCeldaVigencia.Falta or EstadoCeldaVigencia.NoAplica
            ? new VigenciaCeldaDto(estado, null, null, null, exigidaPor)
            : new VigenciaCeldaDto(estado, Guid.NewGuid(),
                DiaDeNegocio.Hoy().AddDays(diasHastaVencer ?? 400), DiaDeNegocio.Hoy().AddDays(diasHastaVencer ?? 400).AddMonths(-mesesVigencia), exigidaPor);

    private static VigenciaEmpresaDto DatosDeVigencia()
    {
        var columnas = new List<VigenciaColumnaDto>
        {
            new(TipoReco, "Reconocimiento médico", TipoDocumentoBase.AptitudMedica, GrupoColumnaVigencia.Basicos, 12,
                [new(C1, 12, 12), new(C2, 12, 12)]),
            new(TipoArt19, "Formación Art 19", TipoDocumentoBase.FormacionArt19, GrupoColumnaVigencia.Basicos, 48,
                [new(C1, 48, 48), new(C2, 36, 36)]),
            new(TipoAltura, "Trabajos en altura", null, GrupoColumnaVigencia.Formacion, 60, [new(C1, 60, 60)]),
            new(TipoCarretilla, "Carretilla elevadora", null, GrupoColumnaVigencia.Opcional, 60, [])
        };
        var filas = new List<VigenciaFilaDto>
        {
            new(Guid.NewGuid(), "Ana Goikoetxea", "Soldador", null, "Bilbao", [C1],
            [
                Celda(EstadoCeldaVigencia.Vigente, 300, [C1]),
                Celda(EstadoCeldaVigencia.Falta, null, [C1]),
                Celda(EstadoCeldaVigencia.Vencido, -20, [C1], 60),
                Celda(EstadoCeldaVigencia.Vigente, 500, [], 60)
            ]),
            new(Guid.NewGuid(), "Berta Lasa", "Peón", null, "Zaragoza", [C2],
            [
                Celda(EstadoCeldaVigencia.PorVencer, 10, [C2]),
                Celda(EstadoCeldaVigencia.Vigente, 700, [C2], 36),
                Celda(EstadoCeldaVigencia.NoAplica, null, []),
                Celda(EstadoCeldaVigencia.NoAplica, null, [])
            ]),
            new(Guid.NewGuid(), "Carla Pardo", "Soldador", null, "Bilbao", [C1],
            [
                Celda(EstadoCeldaVigencia.Vigente, 300, [C1]),
                Celda(EstadoCeldaVigencia.Vigente, 900, [C1], 48),
                Celda(EstadoCeldaVigencia.Vigente, 800, [C1], 60),
                Celda(EstadoCeldaVigencia.NoAplica, null, [])
            ])
        };
        return new VigenciaEmpresaDto(
            [new(C1, "Planta Zaragoza", "Refrielectric S.A."), new(C2, "Taller Berriz", "Talleres Berriz Coop.")],
            columnas, filas, 30);
    }

    private IRenderedComponent<PestanaVigenciaEmpresa> RenderizarVigencia(VigenciaEmpresaDto? datos = null)
    {
        Registrar(q => q is ObtenerVigenciaEmpresaQuery ? datos ?? DatosDeVigencia() : throw new NotSupportedException(q.GetType().Name));
        var cut = Render<PestanaVigenciaEmpresa>(p => p.Add(x => x.EmpresaId, EmpresaId));
        cut.WaitForAssertion(() => cut.FindAll("[aria-busy=true]").Should().BeEmpty());
        return cut;
    }

    private static List<string> Nombres(IRenderedComponent<PestanaVigenciaEmpresa> cut) =>
        cut.FindAll(".vigencia-trabajador-nombre").Select(a => a.TextContent.Trim()).ToList();

    [Fact]
    public void Vigencia_pide_la_matriz_de_la_Empresa_y_pinta_cabeceras_grupos_y_estados()
    {
        var cut = RenderizarVigencia();

        cut.FindAll(".vigencia-grupo").Select(g => g.TextContent.Trim()).Should().Equal("Básicos", "Formación exigida", "Opcional");
        cut.FindAll(".vigencia-columna-nombre").Select(c => c.TextContent.Trim())
            .Should().Equal("Reco. médico", "Art. 19", "Trabajos en altura", "Carretilla elevadora");
        // Todos los Centros: manda la vigencia más corta que pide alguno (Art. 19: 36 meses del Taller Berriz).
        cut.FindAll(".vigencia-columna-vigencia").Select(c => c.TextContent.Trim()).Should().Equal("1 año", "3 años", "5 años", "5 años");
        cut.FindAll(".vigencia-columna-vigencia")[1].GetAttribute("title").Should().Contain("Talleres Berriz Coop. › Taller Berriz");
        Nombres(cut).Should().Equal("Ana Goikoetxea", "Berta Lasa", "Carla Pardo");

        var filaAna = cut.FindAll("[data-fila]")[0];
        filaAna.QuerySelectorAll(".vigencia-celda").Select(c => c.GetAttribute("data-estado"))
            .Should().Equal("vigente", "falta", "vencido", "opcional");
        filaAna.QuerySelectorAll(".vigencia-celda")[1].TextContent.Trim().Should().Be("Falta");
        var filaBerta = cut.FindAll("[data-fila]")[1];
        filaBerta.QuerySelectorAll(".vigencia-celda").Select(c => c.GetAttribute("data-estado"))
            .Should().Equal("por-vencer", "vigente", "no-aplica", "no-aplica");
        filaBerta.QuerySelectorAll(".vigencia-celda")[2].TextContent.Trim().Should().Be("–");
    }

    [Fact]
    public void Vigencia_la_celda_es_un_enlace_a_la_documentacion_del_Trabajador_y_la_que_no_aplica_no_lo_es()
    {
        var cut = RenderizarVigencia();
        var filaBerta = cut.FindAll("[data-fila]")[1];

        filaBerta.QuerySelectorAll("a.vigencia-celda")[0].GetAttribute("href").Should().EndWith("?pestana=documentacion").And.Contain("/trabajadores/");
        filaBerta.QuerySelectorAll("a.vigencia-celda").Should().HaveCount(2, "las dos celdas «no aplica» no son enlaces");
    }

    [Fact]
    public async Task Vigencia_los_chips_cuentan_y_filtran_Trabajadores_con_problemas_y_por_vencer()
    {
        var cut = RenderizarVigencia();

        var chips = cut.FindAll(".vigencia-chip");
        chips[0].TextContent.Trim().Should().Be("Solo con problemas · 2");
        chips[1].TextContent.Trim().Should().Be("Vence en 30 días · 1");

        await chips[0].ClickAsync(new MouseEventArgs());
        Nombres(cut).Should().Equal("Ana Goikoetxea", "Berta Lasa");

        await cut.FindAll(".vigencia-chip")[1].ClickAsync(new MouseEventArgs());
        Nombres(cut).Should().Equal("Berta Lasa");
    }

    [Fact]
    public void Vigencia_con_un_Centro_elegido_solo_salen_sus_Trabajadores_y_las_columnas_que_exige_con_su_vigencia()
    {
        var cut = RenderizarVigencia();

        cut.Find("select[aria-label=Centro]").Change(C1.ToString());

        Nombres(cut).Should().Equal("Ana Goikoetxea", "Carla Pardo");
        cut.FindAll(".vigencia-columna-nombre").Select(c => c.TextContent.Trim()).Should().Equal("Reco. médico", "Art. 19", "Trabajos en altura");
        cut.FindAll(".vigencia-columna-vigencia").Select(c => c.TextContent.Trim()).Should().Equal("1 año", "4 años", "5 años");
    }

    [Fact]
    public void Vigencia_ordena_por_delegacion_y_pagina_con_el_tamano_elegido()
    {
        var cut = RenderizarVigencia();

        cut.Find("label.vigencia-ordenar select").Change("delegacion-empresa");
        Nombres(cut).Should().Equal("Ana Goikoetxea", "Carla Pardo", "Berta Lasa");

        cut.FindAll(".vigencia-tamanos button").Single(b => b.TextContent.Trim() == "10").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find(".vigencia-pie").TextContent.Should().Contain("Mostrando 1–3 de 3");
    }

    [Fact]
    public async Task Vigencia_el_selector_de_fecha_cambia_vencimiento_por_emision()
    {
        var cut = RenderizarVigencia();
        var vencimientoCarla = cut.FindAll("[data-fila]")[2].QuerySelectorAll(".vigencia-celda")[0].TextContent.Trim();

        await cut.FindAll(".vigencia-segmento button").Single(b => b.TextContent.Trim() == "Emisión").ClickAsync(new MouseEventArgs());

        var emisionCarla = cut.FindAll("[data-fila]")[2].QuerySelectorAll(".vigencia-celda")[0].TextContent.Trim();
        emisionCarla.Should().NotBe(vencimientoCarla);
        emisionCarla.Should().Be(DiaDeNegocio.Hoy().AddDays(300).AddMonths(-12).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Vigencia_sin_Trabajadores_dice_que_no_hay_nada_que_medir()
    {
        var cut = RenderizarVigencia(VigenciaEmpresaDto.Vacia());

        cut.Markup.Should().Contain("Sin Trabajadores");
        cut.FindAll(".vigencia-celda").Should().BeEmpty();
    }

    // ── Plataformas ───────────────────────────────────────────────────────

    private static AcreditacionDrillDownDto Documento(string tipo, EstadoAcreditacion estado, Guid centroId, string centro, string? motivo = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Obras y Estructuras del Ebro S.L.", tipo, estado, motivo, EmpresaId: EmpresaId, CentroId: centroId, CentroNombre: centro);

    private (MediadorFalso Mediador, IRenderedComponent<PestanaPlataformasEmpresa> Cut) RenderizarPlataformas(bool todoValidado = false) =>
        RenderizarPlataformas(DatosDePlataformas(todoValidado));

    private (MediadorFalso Mediador, IRenderedComponent<PestanaPlataformasEmpresa> Cut) RenderizarPlataformas(IReadOnlyList<ProveedorAcreditacionesDto> datos)
    {
        var mediador = Registrar(q => q is ObtenerAcreditacionesPorProveedorQuery ? datos : throw new NotSupportedException(q.GetType().Name));
        var cut = Render<PestanaPlataformasEmpresa>(p => p.Add(x => x.EmpresaId, EmpresaId));
        cut.WaitForAssertion(() => cut.FindAll("[aria-busy=true]").Should().BeEmpty());
        return (mediador, cut);
    }

    private static IReadOnlyList<ProveedorAcreditacionesDto> DatosDePlataformas(bool todoValidado) =>
            [
                new(Guid.NewGuid(), "Nalanda", "NALANDA",
                [
                    new ClienteAcreditacionesDto(Guid.NewGuid(), "Refrielectric S.A.",
                    [
                        Documento("Plan de prevención", todoValidado ? EstadoAcreditacion.Aceptada : EstadoAcreditacion.PendienteDeSubir, C1, "Planta Zaragoza"),
                        Documento("Relación nominal de trabajadores (RNT)", todoValidado ? EstadoAcreditacion.Aceptada : EstadoAcreditacion.Rechazada, C1, "Planta Zaragoza", "Falta la firma"),
                        Documento("Seguro de responsabilidad civil", EstadoAcreditacion.Aceptada, C1, "Planta Zaragoza")
                    ])
                ]),
                new(Guid.NewGuid(), "CTAIMA", "CTAIMA",
                [
                    new ClienteAcreditacionesDto(Guid.NewGuid(), "Talleres Berriz Coop.",
                    [
                        Documento("Certificado de estar al corriente con la TGSS", EstadoAcreditacion.Aceptada, C2, "Taller Berriz"),
                        Documento("Recibo del seguro", todoValidado ? EstadoAcreditacion.Aceptada : EstadoAcreditacion.Subida, C2, "Taller Berriz")
                    ])
                ])
            ];

    [Fact]
    public void Plataformas_pide_solo_los_documentos_de_esta_Empresa_con_aceptadas_y_subidas()
    {
        var (mediador, _) = RenderizarPlataformas();

        var consulta = mediador.Enviadas.OfType<ObtenerAcreditacionesPorProveedorQuery>().Should().ContainSingle().Subject;
        consulta.SoloDocumentosDeEmpresaId.Should().Be(EmpresaId);
        consulta.IncluirAceptadas.Should().BeTrue();
        consulta.IncluirSubidas.Should().BeTrue();
    }

    [Fact]
    public async Task Plataformas_por_defecto_solo_incidencias_agrupadas_por_Cliente_y_Centro_con_estado_y_motivo()
    {
        var (_, cut) = RenderizarPlataformas();

        var chips = cut.FindAll(".plataformas-chip");
        chips[0].TextContent.Trim().Should().Be("Solo incidencias · 3");
        chips[1].TextContent.Trim().Should().Be("Todo · 5");
        chips[0].GetAttribute("aria-pressed").Should().Be("true");

        cut.FindAll(".plataformas-grupo").Should().HaveCount(2);
        var grupoZaragoza = cut.FindAll(".plataformas-grupo")[0];
        grupoZaragoza.TextContent.Should().Contain("Refrielectric S.A.").And.Contain("Planta Zaragoza").And.Contain("Plataforma Nalanda · 2 incidencias");
        // Del peor al mejor: Rechazado, Pendiente de subir.
        grupoZaragoza.QuerySelectorAll(".plataformas-fila").Select(f => f.GetAttribute("data-estado")).Should().Equal("Rechazada", "PendienteDeSubir");
        grupoZaragoza.QuerySelectorAll(".plataformas-fila")[0].TextContent.Should().Contain("Rechazado").And.Contain("Falta la firma");
        grupoZaragoza.QuerySelector("a.boton")!.GetAttribute("href").Should().Be($"/centros/{C1}");

        var grupoBerriz = cut.FindAll(".plataformas-grupo")[1];
        grupoBerriz.QuerySelectorAll(".plataformas-fila").Should().ContainSingle().Which.TextContent.Should().Contain("Pendiente de validación");

        await chips[1].ClickAsync(new MouseEventArgs());

        cut.FindAll(".plataformas-fila").Should().HaveCount(5);
        cut.FindAll(".plataformas-fila").Count(f => f.GetAttribute("data-incidencia") == "false").Should().Be(2);
    }

    [Fact]
    public void Plataformas_sin_incidencias_dice_que_todo_esta_validado()
    {
        var (_, cut) = RenderizarPlataformas(todoValidado: true);

        cut.Markup.Should().Contain("Sin incidencias en plataformas");
        cut.FindAll(".plataformas-fila").Should().BeEmpty();
        cut.FindAll(".plataformas-chip")[1].TextContent.Trim().Should().Be("Todo · 5");
    }

    // ── Revisión de la PR: regla del dominio, accesibilidad y estados ─────

    private static AcreditacionDrillDownDto Vencida(string tipo, Guid centroId, string centro, DateOnly vencio) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Obras y Estructuras del Ebro S.L.", tipo, EstadoAcreditacion.Aceptada, null,
            EmpresaId: EmpresaId, CentroId: centroId, CentroNombre: centro,
            EstadoVigencia: EstadoVigenciaEnPlataforma.VenceEnFecha, FechaVencimientoEnPlataforma: vencio, VencidaEnPlataforma: true);

    private static IReadOnlyList<ProveedorAcreditacionesDto> UnProveedor(params AcreditacionDrillDownDto[] documentos) =>
        [new(Guid.NewGuid(), "Nalanda", "NALANDA", [new ClienteAcreditacionesDto(Guid.NewGuid(), "Refrielectric S.A.", documentos)])];

    [Fact]
    public void Plataformas_un_documento_aceptado_pero_vencido_en_la_plataforma_es_una_incidencia_no_un_validado()
    {
        var vencio = DiaDeNegocio.Hoy().AddDays(-3);
        var (_, cut) = RenderizarPlataformas(UnProveedor(
            Vencida("Seguro de responsabilidad civil", C1, "Planta Zaragoza", vencio),
            Documento("Plan de prevención", EstadoAcreditacion.Aceptada, C1, "Planta Zaragoza")));

        cut.FindAll(".plataformas-chip")[0].TextContent.Trim().Should().Be("Solo incidencias · 1");
        var fila = cut.FindAll(".plataformas-fila").Should().ContainSingle().Subject;
        fila.GetAttribute("data-estado").Should().Be("VencidaEnPlataforma");
        fila.TextContent.Should().Contain("Vencido en plataforma").And.Contain($"Vencido el {vencio:dd/MM/yyyy}").And.NotContain("Validado");
        cut.Markup.Should().NotContain("Sin incidencias en plataformas");
        cut.Find(".plataformas-grupo-titulo").TextContent.Should().Contain("1 incidencia");
    }

    [Fact]
    public void Plataformas_un_documento_repetido_bajo_la_misma_plataforma_y_Centro_sale_una_vez_con_el_peor_estado()
    {
        var (_, cut) = RenderizarPlataformas(UnProveedor(
            Documento("Plan de prevención", EstadoAcreditacion.Aceptada, C1, "Planta Zaragoza"),
            Documento("Plan de prevención", EstadoAcreditacion.Rechazada, C1, "Planta Zaragoza", "Sin firma")));

        cut.FindAll(".plataformas-chip")[1].TextContent.Trim().Should().Be("Todo · 1");
        cut.FindAll(".plataformas-fila").Should().ContainSingle().Which.GetAttribute("data-estado").Should().Be("Rechazada");
    }

    [Fact]
    public void Plataformas_sin_datos_no_afirma_que_no_haya_documentacion_porque_puede_ser_el_alcance()
    {
        var (_, cut) = RenderizarPlataformas([]);

        cut.Markup.Should().Contain("Sin documentación en plataformas");
        cut.Find("[data-nota=alcance]").TextContent.Should().Contain("puede deberse a tus permisos de acceso");
    }

    [Fact]
    public void Vigencia_el_selector_de_Centro_no_ofrece_un_Centro_que_no_exige_nada()
    {
        var datos = DatosDeVigencia();
        var sinGestion = Guid.NewGuid();
        datos = datos with { Centros = [.. datos.Centros, new VigenciaCentroDto(sinGestion, "Almacén sin gestión", "Refrielectric S.A.")] };

        var cut = RenderizarVigencia(datos);

        cut.FindAll("select[aria-label=Centro] option").Select(o => o.TextContent.Trim())
            .Should().Equal("Todos los Centros", "Planta Zaragoza", "Taller Berriz");
    }

    [Fact]
    public void Vigencia_sin_columnas_dice_que_no_hay_documentacion_exigida_en_vez_de_filas_vacias()
    {
        var datos = DatosDeVigencia();

        var cut = RenderizarVigencia(datos with { Columnas = [], Filas = [.. datos.Filas.Select(f => f with { Celdas = [] })] });

        cut.Markup.Should().Contain("Sin documentación exigida");
        cut.FindAll(".vigencia-fila").Should().BeEmpty();
    }

    [Fact]
    public void Vigencia_un_mes_se_escribe_en_singular()
    {
        var datos = DatosDeVigencia();
        var columna = datos.Columnas[2] with { VigenciaMesesDelTipo = 1, ExigidoPor = [new(C1, 1, 1)] };
        var cut = RenderizarVigencia(datos with { Columnas = [datos.Columnas[0], datos.Columnas[1], columna, datos.Columnas[3]] });

        cut.FindAll(".vigencia-columna-vigencia")[2].TextContent.Trim().Should().Be("1 mes");
    }

    [Fact]
    public void Vigencia_la_rejilla_es_una_tabla_accesible_y_el_estado_no_depende_solo_del_color()
    {
        var cut = RenderizarVigencia();

        cut.Find(".vigencia-rejilla").GetAttribute("role").Should().Be("table");
        cut.FindAll("[role=row]").Should().HaveCount(1 + 3, "la cabecera y una fila por Trabajador");
        cut.FindAll("[role=columnheader]").Should().HaveCount(1 + 4);
        cut.FindAll("[role=rowheader]").Should().HaveCount(3);
        cut.FindAll("[role=cell]").Should().HaveCount(3 * 4);
        // El nombre accesible de cada celda dice el estado con palabras.
        var filaAna = cut.FindAll("[data-fila]")[0];
        filaAna.QuerySelectorAll(".vigencia-celda")[2].GetAttribute("aria-label").Should().Contain("Vencido");
        filaAna.QuerySelectorAll(".vigencia-celda")[1].GetAttribute("aria-label").Should().Contain("sin registrar");
    }
}
