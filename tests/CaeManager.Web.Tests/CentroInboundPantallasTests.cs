using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Inbound.ContratosAcceso.Queries;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Web.Features.Inbound.CentroInbound;
using CaeManager.Web.Features.Inbound.CentroInbound.Pages;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;
using CaeManager.Web.Features.Inbound.InformacionDelCentro;
using CaeManager.Web.Features.Inbound.InformacionDelCentro.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las pantallas «Centro» (con Aptos hoy), «Centros» e «Información del Centro» de Inbound (tarjetas 14 y 15): rótulos, acciones,
/// estados y estados vacíos. Los datos ficticios (Logística Delta Sur S.A. y Montajes Moncayo S.L.) viven solo aquí: sin backend
/// (B-28 y B-29) la pantalla real no pinta ninguno.
/// </summary>
public class CentroInboundPantallasTests : BunitContext
{
    private static readonly Guid CentroId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid ClienteId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid ContratoId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private sealed class MediadorDePrueba : IMediator
    {
        public bool CentroNoVisible { get; init; }
        public IReadOnlyList<ContratoAccesoDto> Contratos { get; init; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return (TResponse)(request switch
            {
                ObtenerCentroPorIdQuery q => CentroNoVisible ? null! : (object)new CentroDetalleDto(
                    q.Id, ClienteId, "Logística Delta Sur S.A.", Guid.NewGuid(), "Logística Delta Sur S.A.", "Planta Zaragoza Norte", null,
                    "Polígono Malpica, calle E, 12 · 50016 Zaragoza", null, null, Guid.NewGuid()),
                ObtenerContratosAccesoQuery => (object)Contratos,
                ObtenerCentrosParaSelectorQuery => new[] { new CentroSelectorDto(CentroId, "Planta Zaragoza Norte", "Logística Delta Sur S.A.", "Logística Delta Sur S.A.", ClienteId) },
                ObtenerEmpresaPorIdQuery q => new EmpresaDetalleDto(q.Id, "Montajes Moncayo S.L.", "B12345674", DateTime.UtcNow, [], Guid.NewGuid()),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}."),
            });
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AntiforgeryFalso : Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider
    {
        public override Microsoft.AspNetCore.Components.Forms.AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }

    private sealed class AptosFalsos(IReadOnlyList<TrabajadorAptoHoy>? filas) : IAptosHoyDelCentro
    {
        public Task<IReadOnlyList<TrabajadorAptoHoy>?> ObtenerAsync(Guid centroId, CancellationToken cancellationToken = default) => Task.FromResult(filas);
    }

    private sealed class InformacionFalsa(IReadOnlyList<DocumentoInformacionCentro>? docs) : IInformacionDelCentro
    {
        public Task<IReadOnlyList<DocumentoInformacionCentro>?> ListarAsync(Guid? centroId, CancellationToken cancellationToken = default) => Task.FromResult(docs);
    }

    public CentroInboundPantallasTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private static ContratoAccesoDto Contrato(EstadoContratoAcceso estado = EstadoContratoAcceso.Activo) =>
        new(ContratoId, ClienteId, CentroId, EmpresaId, new DateOnly(2020, 1, 1), new DateOnly(2099, 12, 31), null, true, true, null,
            Guid.NewGuid(), estado, TipoContratoAcceso.Recurrente, PropiaActividadContrato.Si, 1, 10, null, null);

    private void Preparar(
        IReadOnlyList<TrabajadorAptoHoy>? aptos = null, IReadOnlyList<DocumentoInformacionCentro>? docs = null,
        bool centroNoVisible = false, params ContratoAccesoDto[] contratos)
    {
        Services.AddScoped<IMediator>(_ => new MediadorDePrueba { CentroNoVisible = centroNoVisible, Contratos = contratos });
        Services.AddScoped<ResolutorContratosAcceso>();
        Services.AddSingleton<Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider>(new AntiforgeryFalso());
        Services.AddScoped<CaeManager.Web.Components.DesignSystem.ToastService>();
        Services.AddScoped<CaeManager.Application.Common.ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddScoped<IAptosHoyDelCentro>(_ => new AptosFalsos(aptos));
        Services.AddScoped<IInformacionDelCentro>(_ => new InformacionFalsa(docs));
    }

    private static TrabajadorAptoHoy Apto(string nombre, EstadoAptitudHoy estado, string? bloquea = null, string empresa = "Montajes Moncayo S.L.") =>
        new(Guid.NewGuid(), nombre, "***4512**", empresa, "Recurrente", estado, bloquea, bloquea is null ? "Siguiente vencimiento: 02/03/27" : "Vencido el 30/10/26");

    private static DocumentoInformacionCentro Documento(string nombre, bool alDia, string centro = "Planta Zaragoza Norte") =>
        new(Guid.NewGuid(), CentroId, centro, nombre, "Información de riesgos", ModalidadInformacion.Firmado, "v5", new DateOnly(2026, 11, 14),
            "La Empresa contratista lo firma con nombre y cargo.",
            [new AcuseEmpresaContratista("Montajes Moncayo S.L.", alDia, alDia ? "Firmado" : "Acuse pendiente", "Jorge Royo Lacasta, gerente", "Trabajadores informados: 6 de 6")],
            [new VersionInformacion("v5", new DateOnly(2026, 11, 14), "Nuevo sentido de circulación")]);

    private IRenderedComponent<T> Cargada<T>(IRenderedComponent<T> cut) where T : IComponent
    {
        cut.WaitForState(() => !cut.Markup.Contains("esqueleto-lista"), TimeSpan.FromSeconds(5));
        return cut;
    }

    // ---------------------------------------------------------------- Centro (tarjeta 14)

    [Fact]
    public void El_Centro_sin_backend_enseña_la_cabecera_las_cuatro_pestañas_y_Aptos_hoy_no_disponible()
    {
        Preparar(contratos: Contrato());

        var cut = Cargada(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.Markup.Should().Contain("Planta Zaragoza Norte").And.Contain("Polígono Malpica");
        foreach (var pestana in new[] { "Aptos hoy", "Requisitos del Centro", "Empresas contratistas", "Información del Centro" })
            cut.FindAll("[role=tab]").Should().Contain(t => t.TextContent.Contains(pestana));
        cut.FindAll("[role=tab]").Should().HaveCount(4, "Concurrencia, Coordinación e Incidencias no entran en esta pantalla");
        cut.Markup.Should().Contain("nunca el motivo");
        cut.Markup.Should().Contain("Aptos hoy no está disponible");
        cut.FindAll("table").Should().BeEmpty("sin backend no se inventa ninguna fila");
        cut.Find("[data-kpi=aptos]").TextContent.Should().Contain("—");
    }

    [Fact]
    public void El_indicador_de_Empresas_contratistas_sale_de_los_contratos_reales()
    {
        Preparar(contratos: [Contrato(), Contrato(EstadoContratoAcceso.PendienteDeConfirmar), Contrato(EstadoContratoAcceso.Rechazado)]);

        var cut = Cargada(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        var kpi = cut.Find("[data-kpi=empresas]").TextContent;
        kpi.Should().Contain("1 con contrato activo").And.Contain("1 pendiente de confirmar").And.Contain("1 rechazado");
    }

    [Fact]
    public void La_pestaña_Empresas_contratistas_lista_los_contratos_del_Centro_y_enlaza_a_su_detalle()
    {
        Preparar(contratos: Contrato());
        var cut = Cargada(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.FindAll("[role=tab]").First(t => t.TextContent.Contains("Empresas contratistas")).Click();

        cut.Markup.Should().Contain("Montajes Moncayo S.L.").And.Contain("B12345674");
        cut.Find($"a[href='/inbound/contratos/{ContratoId}']").Should().NotBeNull();
        cut.Markup.Should().Contain("Activo");
    }

    [Fact]
    public void La_pestaña_Empresas_contratistas_sin_contratos_dice_que_no_hay()
    {
        Preparar();
        var cut = Cargada(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.FindAll("[role=tab]").First(t => t.TextContent.Contains("Empresas contratistas")).Click();

        cut.Markup.Should().Contain("Este Centro no tiene contratos de acceso");
    }

    [Fact]
    public void Aptos_hoy_ordena_lo_que_bloquea_primero_filtra_y_no_muestra_motivo_medico()
    {
        Preparar(aptos:
        [
            Apto("Javier Lasheras Pueyo", EstadoAptitudHoy.Apto),
            Apto("Pilar Sanjuán Aznar", EstadoAptitudHoy.NoApto, "Certificado de aptitud"),
            Apto("Iván Bescós Lacasa", EstadoAptitudHoy.EnTolerancia, "Formación en prevención del puesto (art. 19 LPRL)"),
        ], contratos: Contrato());

        var cut = Cargada(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        var filas = cut.FindAll("tbody tr").Select(f => f.TextContent).ToList();
        filas.Should().HaveCount(3);
        filas[0].Should().Contain("Pilar Sanjuán Aznar").And.Contain("No apto").And.Contain("No puede entrar hoy").And.Contain("Certificado de aptitud");
        filas[1].Should().Contain("Iván Bescós Lacasa").And.Contain("Vencido · en tolerancia").And.Contain("Puede entrar hoy");
        filas[2].Should().Contain("Javier Lasheras Pueyo").And.Contain("Apto").And.Contain("Todo validado");
        cut.Markup.Should().Contain("***4512**");
        cut.Find("[data-kpi=aptos]").TextContent.Should().Contain("2").And.Contain("de 3 Trabajadores").And.Contain("1 en tolerancia · 1 no aptos");
        cut.Markup.Should().NotContain("médico", "Aptos hoy nunca muestra el motivo médico (plan de pantallas, § 5 bis)");
    }

    [Fact]
    public void Aptos_hoy_filtra_por_estado_y_dice_cuando_ningun_Trabajador_coincide()
    {
        Preparar(aptos: [Apto("Javier Lasheras Pueyo", EstadoAptitudHoy.Apto)], contratos: Contrato());
        var cut = Cargada(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.FindAll("button").First(b => b.TextContent.Contains("No aptos")).Click();

        cut.Markup.Should().Contain("Ningún Trabajador coincide con los filtros");
    }

    [Fact]
    public void Aptos_hoy_sin_Trabajadores_asignados_lo_dice()
    {
        Preparar(aptos: [], contratos: Contrato());

        var cut = Cargada(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.Markup.Should().Contain("Ningún Trabajador asignado a este Centro");
    }

    [Fact]
    public void Requisitos_del_Centro_sin_backend_dice_que_no_esta_disponible()
    {
        Preparar(contratos: Contrato());
        var cut = Cargada(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.FindAll("[role=tab]").First(t => t.TextContent.Contains("Requisitos del Centro")).Click();

        cut.Markup.Should().Contain("Los requisitos del Centro no están disponibles");
    }

    [Fact]
    public void La_pestaña_Informacion_del_Centro_muestra_los_acuses_y_enlaza_a_la_pantalla_15()
    {
        Preparar(docs: [Documento("Normas de seguridad del Centro", alDia: false)], contratos: Contrato());
        var cut = Cargada(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.FindAll("[role=tab]").First(t => t.TextContent.Contains("Información del Centro")).Click();

        cut.Markup.Should().Contain("Normas de seguridad del Centro").And.Contain("0 de 1 firmaron").And.Contain("Faltan 1");
        cut.Find("a[href='/inbound/informacion-centro']").Should().NotBeNull();
    }

    [Fact]
    public void Un_Centro_que_no_es_visible_dice_que_no_se_encuentra()
    {
        Preparar(centroNoVisible: true);

        var cut = Cargada(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.FindAll("table").Should().BeEmpty();
        cut.FindAll("[role=tab]").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Centros (entrada de menú)

    [Fact]
    public void El_listado_de_Centros_agrupa_los_contratos_y_abre_la_ficha()
    {
        Preparar(contratos: [Contrato(), Contrato(EstadoContratoAcceso.Finalizado)]);

        var cut = Cargada(Render<CentrosInbound>());

        cut.Markup.Should().Contain("Planta Zaragoza Norte").And.Contain("Logística Delta Sur S.A.");
        cut.Find($"a[href='/inbound/centros/{CentroId}']").Should().NotBeNull();
        cut.Find("tbody tr").TextContent.Should().Contain("1 con contrato activo");
    }

    [Fact]
    public void El_listado_de_Centros_sin_contratos_lo_dice()
    {
        Preparar();

        var cut = Cargada(Render<CentrosInbound>());

        cut.Markup.Should().Contain("Todavía no hay Centros con contratos de acceso");
    }

    // ---------------------------------------------------------------- Información del Centro (tarjeta 15)

    [Fact]
    public void Informacion_del_Centro_sin_backend_dice_que_no_esta_disponible_y_no_pinta_datos()
    {
        Preparar();

        var cut = Cargada(Render<InformacionDelCentroPagina>());

        cut.Markup.Should().Contain("Información del Centro").And.Contain("La información del Centro no está disponible");
        cut.FindAll("table").Should().BeEmpty();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Añadir").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Informacion_del_Centro_lista_los_documentos_con_pendientes_primero_y_su_panel()
    {
        Preparar(docs: [Documento("Riesgos de la zona de muelles", alDia: true), Documento("Normas de seguridad del Centro", alDia: false)]);

        var cut = Cargada(Render<InformacionDelCentroPagina>());

        foreach (var rotulo in new[] { "Documento", "Centro", "Versión", "Empresas", "Con acuses pendientes", "Al día", "Modalidad" })
            cut.Markup.Should().Contain(rotulo);
        var filas = cut.FindAll("tbody tr").Select(f => f.TextContent).ToList();
        filas[0].Should().Contain("Normas de seguridad del Centro").And.Contain("0 de 1 firmaron");
        filas[1].Should().Contain("Riesgos de la zona de muelles").And.Contain("Todas al día");
        var panel = cut.Find("[data-pieza=detalle-documento]").TextContent;
        panel.Should().Contain("Normas de seguridad del Centro").And.Contain("Montajes Moncayo S.L.").And.Contain("Acuse pendiente")
            .And.Contain("Se les avisa con las Reglas de aviso").And.Contain("(vigente)");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Editar").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Informacion_del_Centro_elige_otro_documento_y_dice_cuando_ninguno_coincide()
    {
        Preparar(docs: [Documento("Riesgos de la zona de muelles", alDia: true), Documento("Normas de seguridad del Centro", alDia: false)]);
        var cut = Cargada(Render<InformacionDelCentroPagina>());

        cut.FindAll("button.icn-nombre").First(b => b.TextContent.Contains("Riesgos")).Click();
        cut.Find("[data-pieza=detalle-documento]").TextContent.Should().Contain("Riesgos de la zona de muelles");

        // El buscador tiene retardo propio (300 ms): se espera a que se aplique.
        cut.Find("input[data-filtro-pantalla]").Input("zzz");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ningún documento coincide con los filtros"), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Informacion_del_Centro_sin_documentos_dice_que_ningun_Centro_ha_publicado()
    {
        Preparar(docs: []);

        var cut = Cargada(Render<InformacionDelCentroPagina>());

        cut.Markup.Should().Contain("Ningún Centro ha publicado información");
    }
}
