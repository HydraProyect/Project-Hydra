using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Blindaje42.Commands.SolicitarCertificacionTgss;
using CaeManager.Application.Blindaje42.Queries.ObtenerBlindajeEmpresasDeCliente;
using CaeManager.Application.Blindaje42.Queries.ObtenerHistorialCertificacionesTgss;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerResumenCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasConCentrosDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento.Queries.ObtenerToleranciasClienteEmpresarial;
using CaeManager.Application.Usuarios.Queries.ObtenerOperadoresCaeDeMiTenant;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using CaeManager.Domain.Blindaje42;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Blindaje42.Components;
using CaeManager.Web.Features.Clientes.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cliente 360, pestaña «Documentación» (<c>?pestana=documentacion</c>): solo el listado de documentos que el Cliente
/// empresarial exige para acceder a sus Centros (sus requisitos documentales, la misma consulta que la pestaña
/// «Requisitos» de Centro 360), con el certificado de la Seguridad Social (TGSS) como primera fila. Sin el texto
/// explicativo del art. 42.1/42.2 ET, sin la lista de documentos registrados y con «Gestionar» como única acción.
/// </summary>
public class Cliente360DocumentacionTests : BunitContext
{
    private const int MaximoCentros = 200;

    private static readonly DateTime Alta = new(2019, 3, 4, 12, 0, 0, DateTimeKind.Utc);

    public Cliente360DocumentacionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class MediatorFalso : IMediator
    {
        public ClienteDetalleDto? Detalle { get; set; }
        public ResumenClienteDto? Resumen { get; set; }
        public List<CentroListaDto> Centros { get; } = [];
        public List<BlindajeEmpresaDto> Certificaciones { get; } = [];
        public List<SolicitudCertificacionTgssDto> Historial { get; } = [];
        public Dictionary<Guid, IReadOnlyList<DocumentacionRequeridaCentroDto>> RequisitosPorCentro { get; } = [];
        public bool FallaRequisitos { get; set; }
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)Responder(request)!);
        }

        private object? Responder(object request) => request switch
        {
            ObtenerClientePorIdQuery => Detalle,
            ObtenerResumenClienteQuery => Resumen,
            ObtenerCentrosQuery => new ResultadoPaginado<CentroListaDto>(Centros, Centros.Count, 1, MaximoCentros),
            ObtenerEmpresasDeClienteQuery => new List<EmpresaDeClienteDto>(),
            ObtenerSubcontratasDeClienteQuery => new List<SubcontrataDeClienteDto>(),
            ObtenerSubcontratasConCentrosDeClienteQuery => new List<SubcontrataConCentrosDeClienteDto>(),
            ObtenerBlindajeEmpresasDeClienteQuery => Certificaciones,
            ObtenerHistorialCertificacionesTgssQuery => Historial,
            SolicitarCertificacionTgssCommand => Result.Exito(Guid.NewGuid()),
            ObtenerDocumentacionRequeridaDeCentroQuery q => FallaRequisitos
                ? throw new InvalidOperationException("Fallo de lectura de requisitos")
                : RequisitosPorCentro.GetValueOrDefault(q.CentroId) ?? (IReadOnlyList<DocumentacionRequeridaCentroDto>)[],
            ObtenerDocumentacionBloqueantePendienteQuery => (IReadOnlyList<DocumentacionBloqueantePendienteDto>)[],
            ObtenerToleranciasClienteEmpresarialQuery => (IReadOnlyList<ToleranciaTipoDocumentoDto>)[],
            ObtenerPersonasConCarteraQuery => (IReadOnlyList<CarterasDeOperacion>)[],
            ObtenerOperadoresCaeDeMiTenantQuery => (IReadOnlyList<CarterasDeOperacion>)[],
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        };

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(Roles.GestorCae);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(Roles.GestorCae);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class TenantActivo : ITenantActual
    {
        public Guid? TenantId { get; } = Guid.NewGuid();
    }

    private (Guid Id, MediatorFalso Mediador) Preparar(string rol = Roles.GestorCae)
    {
        var id = Guid.NewGuid();
        var mediador = new MediatorFalso
        {
            Detalle = new ClienteDetalleDto(id, "Refrielectric S.A.", "A-48.220.917", false, null, Alta, null, Guid.NewGuid(), NotaInternaVisible: true),
            Resumen = new ResumenClienteDto(id, "Refrielectric S.A.", "A-48.220.917", false, Alta, null, 3, 42)
        };
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ITenantActual>(_ => new TenantActivo());
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioActualFalso());
        Services.AddLocalization();
        this.ConRolDeEscritura(rol);
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return (id, mediador);
    }

    private IRenderedComponent<ClienteDetalle> Renderizar(Guid id, string pestana = "documentacion")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"clientes/{id}?pestana={pestana}");
        var cut = Render<ClienteDetalle>(p => p.Add(x => x.ClienteId, id));
        cut.WaitForAssertion(() => cut.FindAll("h1, .estado-vacio").Should().NotBeEmpty());
        cut.WaitForAssertion(() => cut.FindAll(".cliente360-documentacion .fila-relacion, .cliente360-documentacion .estado-vacio").Should().NotBeEmpty());
        return cut;
    }

    private static CentroListaDto Centro(string nombre, EstadoCentro estado = EstadoCentro.Vigente) =>
        new(Guid.NewGuid(), nombre, null, Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), "Ibertec GmbH",
            estado, 80, new RecuentosCentroDto([], []));

    private static DocumentacionRequeridaCentroDto Requisito(
        Guid tipoId, string nombre, AmbitoAplicacion ambito, bool aplica = true, bool bloquea = false) =>
        new(tipoId, nombre, ambito, EsObligatorioGlobal: aplica, Aplica: aplica, Incluido: null,
            PeriodicidadEspecialMeses: null, BloqueaAcceso: bloquea, ArchivoUrl: null, NombreArchivoOriginal: null);

    private static BlindajeEmpresaDto Empresa(Guid id, string nombre, EstadoBlindaje42? estado, int solicitudes) =>
        new(id, nombre, null, solicitudes, estado is null ? null : new UltimaSolicitudCertificacionTgssDto(
            Guid.NewGuid(), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null, null, estado.Value));

    private static List<string> Nombres(IRenderedComponent<ClienteDetalle> cut) =>
        cut.FindAll(".cliente360-documentacion .fila-relacion-nombre").Select(n => n.TextContent.Trim()).ToList();

    [Theory]
    [InlineData("documentacion")]
    [InlineData("blindaje42")]
    public void Documentacion_y_el_enlace_antiguo_abren_los_documentos_exigidos_sin_texto_explicativo(string pestana)
    {
        var (id, mediador) = Preparar();
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Ibertec GmbH", null, 0));
        Preparar_centro_con_requisitos(mediador);

        var cut = Renderizar(id, pestana);

        cut.Find("[role=tab][aria-selected=true]").TextContent.Trim().Should().Be("Documentación");
        cut.FindAll("[role=tab]").Should().HaveCount(7);
        var texto = cut.Find(".cliente360-documentacion").TextContent;
        texto.Should().NotContain("42.1").And.NotContain("42.2").And.NotContain("certificación negativa")
            .And.NotContain("Certificaciones de la TGSS").And.NotContain("Blindaje");
        cut.FindAll(".cliente360-documentacion .tarjeta-titulo").Should().BeEmpty("el certificado es una fila, no un bloque propio");
    }

    private static void Preparar_centro_con_requisitos(MediatorFalso mediador)
    {
        var centro = Centro("Planta Barakaldo");
        mediador.Centros.Add(centro);
        mediador.RequisitosPorCentro[centro.Id] = [Requisito(Guid.NewGuid(), "Evaluación de riesgos", AmbitoAplicacion.Empresa)];
    }

    [Fact]
    public void El_certificado_TGSS_es_la_primera_fila_con_su_estado_y_le_siguen_los_requisitos_de_los_Centros()
    {
        var (id, mediador) = Preparar();
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Ibertec GmbH", EstadoBlindaje42.ExoneradaPorCertificacion, 2));
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Grúas Aldapa S.L.", null, 0));
        var seguro = Guid.NewGuid();
        var formacion = Guid.NewGuid();
        var epi = Guid.NewGuid();
        var riesgos = Guid.NewGuid();
        var barakaldo = Centro("Planta Barakaldo");
        var getafe = Centro("Almacén Getafe");
        var cerrado = Centro("Nave sin gestión", EstadoCentro.SinGestionCae);
        mediador.Centros.AddRange([barakaldo, getafe, cerrado]);
        mediador.RequisitosPorCentro[barakaldo.Id] = [
            Requisito(formacion, "Formación PRL", AmbitoAplicacion.Trabajador),
            Requisito(seguro, "Seguro RC", AmbitoAplicacion.Empresa, bloquea: true),
            Requisito(epi, "EPI entregado", AmbitoAplicacion.Trabajador, aplica: false)];
        mediador.RequisitosPorCentro[getafe.Id] = [Requisito(seguro, "Seguro RC", AmbitoAplicacion.Empresa)];
        mediador.RequisitosPorCentro[cerrado.Id] = [Requisito(riesgos, "Evaluación de riesgos", AmbitoAplicacion.Empresa)];

        var cut = Renderizar(id);

        // TGSS primero; después lo que se exige a la Empresa y, a continuación, lo de sus Trabajadores. Lo que el Centro
        // no aplica y lo de un Centro sin gestión CAE no entran.
        Nombres(cut).Should().Equal("Certificado de estar al corriente con la Seguridad Social", "Seguro RC", "Formación PRL");
        var filas = cut.FindAll(".cliente360-documentacion .fila-relacion");
        var tgss = filas[0];
        tgss.TextContent.Should().Contain("2 Empresas").And.Contain("2 solicitudes").And.Contain("Sin solicitar");
        var seguroFila = filas[1];
        seguroFila.TextContent.Should().Contain("Empresa empleadora").And.Contain("En 2 de 2 Centros").And.Contain("Bloquea el acceso");
        filas[2].TextContent.Should().Contain("Trabajador").And.Contain("En 1 de 2 Centros").And.NotContain("Bloquea");
        mediador.Enviadas.OfType<ObtenerDocumentacionRequeridaDeCentroQuery>().Select(q => q.CentroId).Should()
            .BeEquivalentTo([barakaldo.Id, getafe.Id], "un Centro sin gestión CAE no exige nada");
    }

    [Fact]
    public void El_estado_del_certificado_es_el_peor_de_las_Empresas()
    {
        var (id, mediador) = Preparar();
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Ibertec GmbH", EstadoBlindaje42.NoExonerada, 1));
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Grúas Aldapa S.L.", EstadoBlindaje42.ExoneradaPorCertificacion, 1));

        var cut = Renderizar(id);

        var tgss = cut.Find(".cliente360-documentacion .fila-relacion");
        tgss.QuerySelector(".fila-relacion-estado")!.TextContent.Trim().Should().Be("Con descubiertos");
        tgss.GetAttribute("data-tono").Should().Be("peligro");
    }

    [Fact]
    public void Sin_requisitos_ni_Empresas_el_vacio_dice_que_el_Cliente_no_tiene_documentos_exigidos()
    {
        var (id, _) = Preparar();

        var cut = Renderizar(id);

        var vacio = cut.Find(".cliente360-documentacion .estado-vacio");
        vacio.TextContent.Should().Contain("Este Cliente todavía no tiene documentos exigidos");
        cut.Find(".cliente360-documentacion").TextContent.Should().NotContain("Sin documentos")
            .And.NotContain("Todavía no hay documentos registrados");
    }

    [Fact]
    public void Con_Empresas_y_sin_requisitos_solo_queda_la_fila_del_certificado_y_no_el_vacio()
    {
        var (id, mediador) = Preparar();
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Ibertec GmbH", null, 0));

        var cut = Renderizar(id);

        Nombres(cut).Should().Equal("Certificado de estar al corriente con la Seguridad Social");
        cut.FindAll(".cliente360-documentacion .estado-vacio").Should().BeEmpty();
    }

    [Fact]
    public void No_lista_los_documentos_registrados_del_Cliente()
    {
        var (id, mediador) = Preparar();
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Ibertec GmbH", null, 0));
        Preparar_centro_con_requisitos(mediador);

        Renderizar(id);

        // PestanaDocumentacion pide ObtenerDocumentosQuery: el doble no la conoce y la página fallaría si la enviara.
        mediador.Enviadas.Select(e => e.GetType().Name).Should().NotContain("ObtenerDocumentosQuery");
        mediador.Enviadas.OfType<ObtenerBlindajeEmpresasDeClienteQuery>().Should().ContainSingle(q => q.ClienteId == id);
    }

    [Fact]
    public void Si_falla_la_lectura_de_requisitos_la_pestana_ofrece_reintentar()
    {
        var (id, mediador) = Preparar();
        Preparar_centro_con_requisitos(mediador);
        mediador.FallaRequisitos = true;

        var cut = Renderizar(id);

        cut.Find(".cliente360-documentacion .estado-vacio").TextContent.Should().Contain("No pudimos cargar los documentos exigidos");
        cut.FindAll(".cliente360-documentacion button").Should().ContainSingle(b => b.TextContent.Trim() == "Reintentar");
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task Gestionar_es_la_unica_accion_y_solo_con_escritura(string rol, bool puedeEscribir)
    {
        var (id, mediador) = Preparar(rol);
        var empresaId = Guid.NewGuid();
        mediador.Certificaciones.Add(Empresa(empresaId, "Ibertec GmbH", EstadoBlindaje42.PendienteRespuesta, 1));
        mediador.Historial.Add(new SolicitudCertificacionTgssDto(Guid.NewGuid(),
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null, null,
            EstadoBlindaje42.PendienteRespuesta, null, false, null));
        Preparar_centro_con_requisitos(mediador);
        var cut = Renderizar(id);

        // La fila del certificado se despliega y deja ver una por Empresa con su estado (lectura).
        await cut.Find(".cliente360-documentacion .fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
        cut.Find(".cliente360-documentacion .fila-relacion-desplegado").TextContent
            .Should().Contain("Ibertec GmbH").And.Contain("Pendiente TGSS");

        var botones = cut.FindAll(".cliente360-documentacion button")
            .Where(b => !b.ClassList.Contains("fila-relacion-desplegar"))
            .Select(b => b.TextContent.Trim()).ToList();
        botones.Should().Equal(puedeEscribir ? ["Gestionar"] : []);

        if (!puedeEscribir)
        {
            cut.FindComponent<DrawerBlindaje42>().Instance.Visible.Should().BeFalse();
            mediador.Enviadas.OfType<ObtenerHistorialCertificacionesTgssQuery>().Should().BeEmpty();
            return;
        }

        await cut.FindAll(".cliente360-documentacion button").Single(b => b.TextContent.Trim() == "Gestionar")
            .ClickAsync(new MouseEventArgs());

        var drawer = cut.FindComponent<DrawerBlindaje42>();
        drawer.Instance.Visible.Should().BeTrue();
        drawer.Instance.EmpresaId.Should().Be(empresaId);
        await drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Registrar solicitud")
            .ClickAsync(new MouseEventArgs());
        mediador.Enviadas.OfType<SolicitarCertificacionTgssCommand>().Should()
            .ContainSingle(c => c.ClienteId == id && c.EmpresaId == empresaId);
    }
}
