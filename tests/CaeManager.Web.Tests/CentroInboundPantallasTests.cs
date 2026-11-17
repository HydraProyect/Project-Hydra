using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Inbound.ContratosAcceso.AptosHoy;
using CaeManager.Application.Inbound.ContratosAcceso.Queries;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Web.Features.Inbound.CentroInbound;
using CaeManager.Web.Features.Inbound.CentroInbound.Pages;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;
using CaeManager.Web.Features.Inbound.InformacionDelCentro;
using CaeManager.Web.Features.Inbound.InformacionDelCentro.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
        public IReadOnlyList<AptoHoyDto> AptosHoy { get; init; } = [];
        public List<ObtenerContratosAccesoQuery> PeticionesContratos { get; } = [];

        private IReadOnlyList<ContratoAccesoDto> Registrar(ObtenerContratosAccesoQuery q)
        {
            PeticionesContratos.Add(q);
            return Contratos.Where(c => q.CentroId is null || c.CentroId == q.CentroId).ToList();
        }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return (TResponse)(request switch
            {
                // Solo el Centro de prueba es visible: cualquier otro Id (p. ej. Guid.Empty) devuelve null, como la Query real.
                ObtenerCentroPorIdQuery q => CentroNoVisible || q.Id != CentroId ? null! : (object)new CentroDetalleDto(
                    q.Id, ClienteId, "Logística Delta Sur S.A.", Guid.NewGuid(), "Logística Delta Sur S.A.", "Planta Zaragoza Norte", null,
                    "Polígono Malpica, calle E, 12 · 50016 Zaragoza", null, null, Guid.NewGuid()),
                // Registra la petición: sin CentroId (listado) devuelve todos los contratos, así que que la ficha acota por su Centro
                // se comprueba sobre PeticionesContratos, no sobre los datos devueltos.
                ObtenerContratosAccesoQuery q => (object)Registrar(q),
                ObtenerAptosHoyDelCentroQuery q => q.CentroId == CentroId ? (object)AptosHoy : Array.Empty<AptoHoyDto>(),
                ObtenerCentrosParaSelectorQuery => new[] { new CentroSelectorDto(CentroId, "Planta Zaragoza Norte", "Logística Delta Sur S.A.", "Logística Delta Sur S.A.", ClienteId) },
                ObtenerEmpresaPorIdQuery q => new EmpresaDetalleDto(q.Id, "Montajes Moncayo S.L.", "B12345674", DateTime.UtcNow, [], Guid.NewGuid()),
                // FichaNoEncontrada lee el conjunto autorizado del usuario: sin ninguna empresa no hay selector de Tenant y pinta el
                // estado vacío con «Ir a la lista». Antes la respuesta era la excepción de «petición no prevista», que la ficha
                // también convierte en el estado vacío, de modo que estos tests pasaban por el camino de error.
                ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)[],
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
        // Datos solo para el Centro de prueba; con otro Id responde vacío.
        public Task<IReadOnlyList<TrabajadorAptoHoy>?> ObtenerAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            Task.FromResult(filas is null ? null : centroId == CentroId ? filas : (IReadOnlyList<TrabajadorAptoHoy>)[]);
    }

    private sealed class InformacionFalsa(IReadOnlyList<DocumentoInformacionCentro>? docs) : IInformacionDelCentro
    {
        public List<Guid?> Consultas { get; } = [];

        public Task<IReadOnlyList<DocumentoInformacionCentro>?> ListarAsync(Guid? centroId, CancellationToken cancellationToken = default)
        {
            Consultas.Add(centroId);
            return Task.FromResult(docs is null || centroId is null ? docs : (IReadOnlyList<DocumentoInformacionCentro>?)docs.Where(d => d.CentroId == centroId).ToList());
        }
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

    private InformacionFalsa _informacion = new(null);
    private MediadorDePrueba _mediador = new();

    private void Preparar(
        IReadOnlyList<TrabajadorAptoHoy>? aptos = null, IReadOnlyList<DocumentoInformacionCentro>? docs = null,
        bool centroNoVisible = false, IReadOnlyList<AptoHoyDto>? aptosDelBackend = null, params ContratoAccesoDto[] contratos)
    {
        _mediador = new MediadorDePrueba { CentroNoVisible = centroNoVisible, Contratos = contratos, AptosHoy = aptosDelBackend ?? [] };
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ResolutorContratosAcceso>();
        Services.AddSingleton<Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider>(new AntiforgeryFalso());
        Services.AddScoped<CaeManager.Web.Components.DesignSystem.ToastService>();
        Services.AddScoped<CaeManager.Application.Common.ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        // Con filas del backend la pantalla usa el adaptador real, que es quien da formato a las fechas.
        if (aptosDelBackend is null)
            Services.AddScoped<IAptosHoyDelCentro>(_ => new AptosFalsos(aptos));
        else
            Services.AddScoped<IAptosHoyDelCentro, AptosHoyDelCentroConBackend>();
        _informacion = new InformacionFalsa(docs);
        Services.AddScoped<IInformacionDelCentro>(_ => _informacion);
    }

    private static TrabajadorAptoHoy Apto(string nombre, EstadoAptitudHoy estado, string? bloquea = null, string empresa = "Montajes Moncayo S.L.") =>
        new(Guid.NewGuid(), nombre, "***4512**", empresa, "Recurrente", estado, bloquea, bloquea is null ? "Siguiente vencimiento: 02/03/27" : "Vencido el 30/10/26");

    private static DocumentoInformacionCentro Documento(string nombre, bool alDia, string centro = "Planta Zaragoza Norte") =>
        new(Guid.NewGuid(), CentroId, centro, nombre, "Información de riesgos", ModalidadInformacion.Firmado, "v5", new DateOnly(2026, 11, 14),
            "La Empresa contratista lo firma con nombre y cargo.",
            [new AcuseEmpresaContratista("Montajes Moncayo S.L.", alDia, alDia ? "Firmado" : "Acuse pendiente", "Jorge Royo Lacasta, gerente", "Trabajadores informados: 6 de 6")],
            [new VersionInformacion("v5", new DateOnly(2026, 11, 14), "Nuevo sentido de circulación")]);

    // Todas las esperas de la clase son asíncronas y usan este plazo (#2330). Las pantallas cargan con awaits reales (el doble
    // cede el hilo con Task.Yield) y cada continuación necesita un hilo libre del pool. Un WaitFor* síncrono bloquea con
    // GetResult() el hilo del pool en el que xUnit corre el test (mecanismo de #2171, descrito en el csproj): con el pool
    // ocupado por el resto de la suite, la continuación no encuentra hilo y la espera vence sin que la pantalla haya vuelto a
    // pintarse («Check count: 1»). La espera asíncrona devuelve el hilo al pool mientras espera. El plazo es el que ya usaba
    // Cargada; antes, dos esperas de esta clase se quedaban en el segundo por defecto de bUnit.
    private static readonly TimeSpan Plazo = TimeSpan.FromSeconds(5);

    // Cargada solo cubre la carga de la pantalla. Cuando la ficha no encuentra el Centro, FichaNoEncontrada hace todavía su
    // propia lectura asíncrona: ese estado se espera aparte, después.
    private static async Task<IRenderedComponent<T>> CargadaAsync<T>(IRenderedComponent<T> cut) where T : IComponent
    {
        await cut.WaitForStateAsync(() => !cut.Markup.Contains("esqueleto-lista"), Plazo);
        return cut;
    }

    // ---------------------------------------------------------------- Centro (tarjeta 14)

    [Fact]
    public async Task El_Centro_sin_backend_enseña_la_cabecera_las_cuatro_pestañas_y_Aptos_hoy_no_disponible()
    {
        Preparar(contratos: Contrato());

        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.Markup.Should().Contain("Planta Zaragoza Norte").And.Contain("Polígono Malpica");
        foreach (var pestana in new[] { "Aptos hoy", "Requisitos del Centro", "Empresas contratistas", "Información del Centro" })
            cut.FindAll("[role=tab]").Should().Contain(t => t.TextContent.Contains(pestana));
        cut.FindAll("[role=tab]").Should().HaveCount(4, "Concurrencia, Coordinación e Incidencias no entran en esta pantalla");
        cut.Markup.Should().Contain("nunca su motivo");
        cut.Markup.Should().Contain("Aptos hoy no está disponible");
        cut.FindAll("table").Should().BeEmpty("sin backend no se inventa ninguna fila");
        cut.Find("[data-kpi=aptos]").TextContent.Should().Contain("—");
    }

    [Fact]
    public async Task El_indicador_de_Empresas_contratistas_sale_de_los_contratos_reales()
    {
        Preparar(contratos: [Contrato(), Contrato(EstadoContratoAcceso.PendienteDeConfirmar), Contrato(EstadoContratoAcceso.Rechazado)]);

        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        var kpi = cut.Find("[data-kpi=empresas]").TextContent;
        kpi.Should().Contain("1 con contrato activo").And.Contain("1 pendiente de confirmar").And.Contain("1 rechazado");
    }

    [Fact]
    public async Task La_pestaña_Empresas_contratistas_lista_los_contratos_del_Centro_y_enlaza_a_su_detalle()
    {
        Preparar(contratos: Contrato());
        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        await cut.FindAll("[role=tab]").First(t => t.TextContent.Contains("Empresas contratistas")).ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Montajes Moncayo S.L.").And.Contain("B12345674");
        cut.Find($"a[href='/inbound/contratos/{ContratoId}']").Should().NotBeNull();
        cut.Markup.Should().Contain("Activo");
    }

    [Fact]
    public async Task La_pestaña_Empresas_contratistas_sin_contratos_dice_que_no_hay()
    {
        Preparar();
        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        await cut.FindAll("[role=tab]").First(t => t.TextContent.Contains("Empresas contratistas")).ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Este Centro no tiene contratos de acceso");
    }

    [Fact]
    public async Task Aptos_hoy_ordena_lo_que_bloquea_primero_filtra_y_no_muestra_motivo_medico()
    {
        Preparar(aptos:
        [
            Apto("Javier Lasheras Pueyo", EstadoAptitudHoy.Apto),
            Apto("Pilar Sanjuán Aznar", EstadoAptitudHoy.NoApto, "Certificado de aptitud"),
            Apto("Iván Bescós Lacasa", EstadoAptitudHoy.EnTolerancia, "Formación en prevención del puesto (art. 19 LPRL)"),
        ], contratos: Contrato());

        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

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
    public async Task Aptos_hoy_filtra_por_estado_y_dice_cuando_ningun_Trabajador_coincide()
    {
        Preparar(aptos: [Apto("Javier Lasheras Pueyo", EstadoAptitudHoy.Apto)], contratos: Contrato());
        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        await cut.WaitForElementAsync("button.franja-estado-boton", Plazo);
        await cut.FindAll("button.franja-estado-boton").First(b => b.TextContent.Contains("No aptos")).ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Ningún Trabajador coincide con los filtros"), Plazo);
    }

    [Fact]
    public async Task Aptos_hoy_el_filtro_por_Empresa_contratista_encuentra_al_Trabajador_por_cualquiera_de_sus_Empresas()
    {
        var deUna = Apto("Pilar Sanjuán Aznar", EstadoAptitudHoy.NoApto, "Seguro de responsabilidad civil", empresa: "Instalaciones Cinca S.L.");
        var enDos = deUna with { EmpresasContratistas = ["Instalaciones Cinca S.L.", "Montajes Moncayo S.L."] };
        Preparar(aptos: [enDos, Apto("Javier Lasheras Pueyo", EstadoAptitudHoy.Apto, empresa: "Instalaciones Cinca S.L.")], contratos: Contrato());
        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        await cut.FindAll("button").First(b => b.TextContent.Contains("Empresa contratista")).ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitemradio]").First(i => i.TextContent.Contains("Montajes Moncayo S.L.")).ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Select(f => f.TextContent).Should()
            .ContainSingle("solo está en Montajes Moncayo el Trabajador con dos Empresas").Which.Should().Contain("Pilar Sanjuán Aznar"),
            Plazo);
    }

    [Fact]
    public async Task Aptos_hoy_sin_Trabajadores_asignados_lo_dice()
    {
        Preparar(aptos: [], contratos: Contrato());

        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.Markup.Should().Contain("Ningún Trabajador asignado a este Centro");
    }

    [Fact]
    public async Task Aptos_hoy_con_datos_enseña_sus_cuatro_columnas_la_nota_los_filtros_y_el_detalle_de_fechas()
    {
        // Las fechas llegan como DateOnly y las formatea el adaptador real, no el fixture.
        Preparar(aptosDelBackend:
        [
            new AptoHoyDto(Guid.NewGuid(), "Pilar Sanjuán Aznar", "12345678Z", ContratoId, EmpresaId, TipoContratoAcceso.Recurrente,
                EstadoAptitudInbound.NoApto, "Carné de operador de plataforma elevadora", SujetoDelRequisitoInbound.Trabajador,
                CausaNoAptitudInbound.Vencido, new DateOnly(2026, 10, 30)),
            new AptoHoyDto(Guid.NewGuid(), "Javier Lasheras Pueyo", "87654321X", ContratoId, EmpresaId, TipoContratoAcceso.Recurrente,
                EstadoAptitudInbound.Apto, null, null, null, new DateOnly(2027, 3, 2)),
        ], contratos: Contrato());

        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        var panel = cut.Find("[data-pieza=aptos-hoy]");
        panel.QuerySelectorAll("thead th").Select(th => th.TextContent.Trim())
            .Should().Equal("Trabajador", "Empresa contratista", "Hoy", "Lo que bloquea o vence");
        panel.QuerySelector("[role=note]")!.TextContent.Should().Contain("Consulta informativa del día; no registra entradas");
        panel.QuerySelector("input[placeholder='Filtrar: Trabajador o DNI']").Should().NotBeNull();
        panel.TextContent.Should().Contain("Todos").And.Contain("En tolerancia").And.Contain("No aptos");
        var filas = cut.FindAll("[data-pieza=aptos-hoy] tbody tr").Select(f => f.TextContent).ToList();
        filas[0].Should().Contain("Montajes Moncayo S.L.").And.Contain("****678Z").And.Contain("Vencido el 30/10/2026");
        filas[1].Should().Contain("Montajes Moncayo S.L.").And.Contain("****321X").And.Contain("Siguiente vencimiento: 02/03/2027");
    }

    [Fact]
    public async Task Aptos_hoy_con_la_proyeccion_restringida_no_pinta_requisito_ni_Todo_validado_ni_tipo_de_contrato()
    {
        // Sin ValidarInbound la Query devuelve las filas ya restringidas (duda 6): nombre, DNI parcial, Empresa y apto o no apto.
        Preparar(aptosDelBackend:
        [
            new AptoHoyDto(Guid.NewGuid(), "Pilar Sanjuán Aznar", "****678Z", ContratoId, EmpresaId, null,
                EstadoAptitudInbound.NoApto, null, null, null, null, ProyeccionCompleta: false),
            new AptoHoyDto(Guid.NewGuid(), "Javier Lasheras Pueyo", "****321X", ContratoId, EmpresaId, null,
                EstadoAptitudInbound.Apto, null, null, null, null, ProyeccionCompleta: false),
        ], contratos: Contrato());

        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        var panel = cut.Find("[data-pieza=aptos-hoy]");
        panel.QuerySelectorAll("thead th").Select(th => th.TextContent.Trim()).Should().Equal("Trabajador", "Empresa contratista", "Hoy");
        panel.QuerySelector("[role=note]")!.TextContent.Should().Contain("Consulta informativa del día").And.NotContain("requisito");
        panel.TextContent.Should().NotContain("En tolerancia", "la restringida no tiene ese estado: la franja no lo ofrece");
        cut.Markup.Should().NotContain("en tolerancia", "el indicador tampoco lo cuenta");
        var filas = cut.FindAll("[data-pieza=aptos-hoy] tbody tr").Select(f => f.TextContent).ToList();
        filas.Should().HaveCount(2);
        filas.Should().OnlyContain(f => f.Contains("Montajes Moncayo S.L.") && !f.Contains("Todo validado") && !f.Contains("—"));
        filas.Should().ContainSingle(f => f.Contains("No puede entrar hoy"));
    }

    [Fact]
    public void El_modelo_de_Aptos_hoy_no_tiene_ninguna_propiedad_de_motivo_medico()
    {
        var propiedades = typeof(TrabajadorAptoHoy).GetProperties().Select(p => p.Name).ToList();

        propiedades.Should().NotContain(n => n.Contains("Motivo", StringComparison.OrdinalIgnoreCase) || n.Contains("Medic", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Salud", StringComparison.OrdinalIgnoreCase) || n.Contains("Clinic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Un_CentroId_vacio_no_deja_la_ficha_cargando()
    {
        Preparar(contratos: Contrato()); // el Centro de prueba es visible; Guid.Empty no es ninguno

        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, Guid.Empty)));

        cut.FindAll("[role=tab]").Should().BeEmpty();
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-ficha-no-encontrada=vacio]").Should().ContainSingle(), Plazo);
        cut.Markup.Should().NotContain("esqueleto-lista");
    }

    [Fact]
    public async Task Un_contrato_repetido_de_la_misma_Empresa_cuenta_una_vez_en_el_indicador()
    {
        Preparar(contratos: [Contrato(), Contrato()]);

        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.Find("[data-kpi=empresas]").TextContent.Should().Contain("1 con contrato activo");
        _mediador.PeticionesContratos.Should().ContainSingle().Which.CentroId.Should().Be(CentroId, "la ficha pide solo los contratos de su Centro");
        _informacion.Consultas.Should().Equal([(Guid?)CentroId], "la ficha pide la información de su Centro, no la de todos");
    }

    [Fact]
    public async Task Requisitos_del_Centro_sin_backend_dice_que_no_esta_disponible()
    {
        Preparar(contratos: Contrato());
        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        await cut.FindAll("[role=tab]").First(t => t.TextContent.Contains("Requisitos del Centro")).ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Los requisitos del Centro no están disponibles");
    }

    [Fact]
    public async Task La_pestaña_Informacion_del_Centro_muestra_los_acuses_y_enlaza_a_la_pantalla_15()
    {
        Preparar(docs: [Documento("Normas de seguridad del Centro", alDia: false)], contratos: Contrato());
        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        await cut.FindAll("[role=tab]").First(t => t.TextContent.Contains("Información del Centro")).ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Normas de seguridad del Centro").And.Contain("0 de 1 firmaron").And.Contain("Faltan 1");
        cut.Find("a[href='/inbound/informacion-centro']").Should().NotBeNull();
    }

    [Fact]
    public async Task Un_Centro_que_no_es_visible_dice_que_no_se_encuentra()
    {
        Preparar(centroNoVisible: true);

        var cut = await CargadaAsync(Render<FichaCentroInbound>(p => p.Add(c => c.CentroId, CentroId)));

        cut.FindAll("table").Should().BeEmpty();
        cut.FindAll("[role=tab]").Should().BeEmpty();
        // La ficha cae en FichaNoEncontrada (no en el error de carga con «Reintentar»).
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-ficha-no-encontrada=vacio]").Should().ContainSingle(), Plazo);
        cut.Markup.Should().NotContain("Reintentar").And.NotContain("esqueleto-lista");
    }

    // ---------------------------------------------------------------- Centros (entrada de menú)

    [Fact]
    public async Task El_listado_de_Centros_agrupa_los_contratos_y_abre_la_ficha()
    {
        Preparar(contratos: [Contrato(), Contrato(EstadoContratoAcceso.Finalizado)]);

        var cut = await CargadaAsync(Render<CentrosInbound>());

        cut.Markup.Should().Contain("Planta Zaragoza Norte").And.Contain("Logística Delta Sur S.A.");
        cut.Find($"a[href='/inbound/centros/{CentroId}']").Should().NotBeNull();
        cut.Find("tbody tr").TextContent.Should().Contain("1 con contrato activo");
    }

    [Fact]
    public async Task El_listado_de_Centros_sin_contratos_lo_dice()
    {
        Preparar();

        var cut = await CargadaAsync(Render<CentrosInbound>());

        cut.Markup.Should().Contain("Todavía no hay Centros con contratos de acceso");
    }

    // ---------------------------------------------------------------- Información del Centro (tarjeta 15)

    [Fact]
    public async Task Informacion_del_Centro_sin_backend_dice_que_no_esta_disponible_y_no_pinta_datos()
    {
        Preparar();

        var cut = await CargadaAsync(Render<InformacionDelCentroPagina>());

        cut.Markup.Should().Contain("Información del Centro").And.Contain("La información del Centro no está disponible");
        cut.FindAll("table").Should().BeEmpty();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Añadir").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Informacion_del_Centro_lista_los_documentos_con_pendientes_primero_y_su_panel()
    {
        Preparar(docs: [Documento("Riesgos de la zona de muelles", alDia: true), Documento("Normas de seguridad del Centro", alDia: false)]);

        var cut = await CargadaAsync(Render<InformacionDelCentroPagina>());

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
    public async Task Informacion_del_Centro_elige_otro_documento_y_dice_cuando_ninguno_coincide()
    {
        Preparar(docs: [Documento("Riesgos de la zona de muelles", alDia: true), Documento("Normas de seguridad del Centro", alDia: false)]);
        var cut = await CargadaAsync(Render<InformacionDelCentroPagina>());

        await cut.FindAll("button.icn-nombre").First(b => b.TextContent.Contains("Riesgos")).ClickAsync(new MouseEventArgs());
        cut.Find("[data-pieza=detalle-documento]").TextContent.Should().Contain("Riesgos de la zona de muelles");

        // El buscador tiene retardo propio (300 ms): se espera a que se aplique.
        await cut.Find("input[data-filtro-pantalla]").InputAsync(new ChangeEventArgs { Value = "zzz" });

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Ningún documento coincide con los filtros"), Plazo);
    }

    [Fact]
    public async Task Informacion_del_Centro_sin_documentos_dice_que_ningun_Centro_ha_publicado()
    {
        Preparar(docs: []);

        var cut = await CargadaAsync(Render<InformacionDelCentroPagina>());

        cut.Markup.Should().Contain("Ningún Centro ha publicado información");
    }
}
