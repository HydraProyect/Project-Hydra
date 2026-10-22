using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Blindaje42.Commands.SolicitarCertificacionTgss;
using CaeManager.Application.Blindaje42.Queries.ObtenerBlindajeEmpresasDeCliente;
using CaeManager.Application.Blindaje42.Queries.ObtenerHistorialCertificacionesTgss;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionExigidaDeCliente;
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
using CaeManager.Web.Features.Clientes;
using CaeManager.Web.Features.Clientes.Components;
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
        public List<EmpresaDeClienteDto> EmpresasDelCliente { get; } = [];
        public List<SolicitudCertificacionTgssDto> Historial { get; } = [];
        public DocumentacionExigidaClienteDto? Documentacion { get; set; } = new(0, []);
        public int FallosDeDocumentacion { get; set; }
        public bool Colgar { get; set; }
        public List<CancellationToken> Tokens { get; } = [];
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add(cancellationToken);
            if (Colgar)
                return new TaskCompletionSource<TResponse>().Task;
            return Task.FromResult((TResponse)Responder(request)!);
        }

        private object? Responder(object request) => request switch
        {
            ObtenerClientePorIdQuery => Detalle,
            ObtenerResumenClienteQuery => Resumen,
            ObtenerCentrosQuery => new ResultadoPaginado<CentroListaDto>(Centros, Centros.Count, 1, MaximoCentros),
            ObtenerEmpresasDeClienteQuery => EmpresasDelCliente.ToList(),
            ObtenerSubcontratasDeClienteQuery => new List<SubcontrataDeClienteDto>(),
            ObtenerSubcontratasConCentrosDeClienteQuery => new List<SubcontrataConCentrosDeClienteDto>(),
            ObtenerBlindajeEmpresasDeClienteQuery => Certificaciones,
            ObtenerHistorialCertificacionesTgssQuery => Historial,
            SolicitarCertificacionTgssCommand => Result.Exito(Guid.NewGuid()),
            ObtenerDocumentacionExigidaDeClienteQuery => FallosDeDocumentacion-- > 0
                ? throw new InvalidOperationException("Fallo de lectura de los documentos exigidos")
                : Documentacion,
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

    private static RequisitoExigidoClienteDto Requisito(
        string nombre, AmbitoAplicacion ambito, int centros, bool bloquea = false) =>
        new(Guid.NewGuid(), nombre, ambito, centros, bloquea);

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

        cut.Find("[role=tab][aria-selected=true]").TextContent.Trim().Should().StartWith("Documentación", "con su cifra detrás, como en la maqueta");
        cut.FindAll("[role=tab]").Should().HaveCount(7);
        var texto = cut.Find(".cliente360-documentacion").TextContent;
        texto.Should().NotContain("42.1").And.NotContain("42.2").And.NotContain("certificación negativa")
            .And.NotContain("Certificaciones de la TGSS").And.NotContain("Blindaje");
        cut.FindAll(".cliente360-documentacion .tarjeta-titulo").Should().BeEmpty("el certificado es una fila, no un bloque propio");
    }

    private static void Preparar_centro_con_requisitos(MediatorFalso mediador)
    {
        mediador.Centros.Add(Centro("Planta Barakaldo"));
        mediador.Documentacion = new(1, [Requisito("Evaluación de riesgos", AmbitoAplicacion.Empresa, 1)]);
    }

    private const string GrupoEmpresa = "Lo que exige a la Empresa empleadora";
    private const string GrupoTrabajador = "Lo que exige a los Trabajadores";
    private const string TituloTgss = "Certificado de estar al corriente con la Seguridad Social";

    /// <summary>Los grupos nacen plegados: despliega todos los que sigan cerrados.</summary>
    private static async Task AbrirGruposAsync(IRenderedComponent<ClienteDetalle> cut)
    {
        foreach (var _ in cut.FindAll(".cliente360-documentacion .fila-relacion-desplegar[aria-expanded=false]").ToList())
            await cut.Find(".cliente360-documentacion .fila-relacion-desplegar[aria-expanded=false]").ClickAsync(new MouseEventArgs());
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> FilasDeDocumento(IRenderedComponent<ClienteDetalle> cut) =>
        cut.FindAll(".cliente360-documentacion .fila-relacion-desplegado .fila-relacion").ToList();

    [Fact]
    public async Task Dos_grupos_plegados_y_al_abrirlos_un_solo_formato_de_fila_con_estado_y_accion()
    {
        var (id, mediador) = Preparar();
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Ibertec GmbH", EstadoBlindaje42.ExoneradaPorCertificacion, 2));
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Grúas Aldapa S.L.", null, 0));
        mediador.Centros.AddRange([Centro("Planta Barakaldo"), Centro("Almacén Getafe"), Centro("Nave sin gestión", EstadoCentro.SinGestionCae)]);
        // La agregación por tipo (qué aplica, en cuántos Centros, si alguno bloquea) es del servidor:
        // ObtenerDocumentacionExigidaDeClienteQueryTests. Aquí, cómo se pinta lo que devuelve.
        mediador.Documentacion = new(2, [
            Requisito("Seguro RC", AmbitoAplicacion.Empresa, 2, bloquea: true),
            Requisito(TituloTgss, AmbitoAplicacion.Empresa, 2, bloquea: true) with { EsCertificadoTgss = true },
            Requisito("Formación PRL", AmbitoAplicacion.Trabajador, 1)]);

        var cut = Renderizar(id);

        // Plegados por defecto: solo los dos grupos, con su recuento y su pastilla.
        Nombres(cut).Should().Equal(GrupoEmpresa, GrupoTrabajador);
        var grupos = cut.FindAll(".cliente360-documentacion .fila-relacion");
        grupos.Should().HaveCount(2);
        // Documentos, no filas: el certificado es uno aunque tenga una fila por Empresa.
        grupos[0].TextContent.Should().Contain("2 documentos").And.Contain("2 bloquean el acceso");
        grupos[1].TextContent.Should().Contain("1 documento").And.Contain("Sin bloqueos");

        await AbrirGruposAsync(cut);

        // El certificado va primero, una fila por Empresa con su estado real, y no sale además como requisito genérico.
        Nombres(cut).Should().Equal(GrupoEmpresa, TituloTgss, TituloTgss, "Seguro RC", GrupoTrabajador, "Formación PRL");
        var filas = FilasDeDocumento(cut);
        filas.Should().HaveCount(4);
        filas[0].TextContent.Should().Contain("Ibertec GmbH").And.Contain("2 solicitudes").And.Contain("En 2 de 2 Centros")
            .And.Contain("Exonerada (certificación)");
        filas[1].TextContent.Should().Contain("Grúas Aldapa S.L.").And.Contain("Sin solicitar");
        filas[2].TextContent.Should().Contain("En 2 de 2 Centros").And.Contain("Bloquea el acceso");
        filas[3].TextContent.Should().Contain("En 1 de 2 Centros").And.Contain("Se exige").And.NotContain("Bloquea");

        // Mismo formato en todas: una pastilla de estado y una acción, sin celdas vacías.
        foreach (var fila in filas)
        {
            fila.QuerySelector(".fila-relacion-estado")!.TextContent.Trim().Should().NotBeEmpty();
            fila.QuerySelector(".fila-relacion-acciones button")!.TextContent.Trim().Should().NotBeEmpty();
        }

        mediador.Enviadas.OfType<ObtenerDocumentacionExigidaDeClienteQuery>().Should()
            .ContainSingle(q => q.ClienteEmpresarialId == id, "una sola consulta de servidor, no una por Centro");
        mediador.Enviadas.OfType<ObtenerDocumentacionRequeridaDeCentroQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Con_un_grupo_plegado_los_descubiertos_de_una_Empresa_se_ven_en_su_cabecera()
    {
        var (id, mediador) = Preparar();
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Ibertec GmbH", EstadoBlindaje42.NoExonerada, 1));

        var cut = Renderizar(id);

        var grupo = cut.Find(".cliente360-documentacion .fila-relacion");
        grupo.GetAttribute("data-tono").Should().Be("peligro");
        grupo.QuerySelector(".fila-relacion-estado")!.TextContent.Trim().Should().Be("Con descubiertos");
    }

    [Fact]
    public async Task Sin_escritura_el_historial_del_certificado_se_abre_con_Ver_historial()
    {
        var (id, mediador) = Preparar(Roles.Consulta);
        var empresaId = Guid.NewGuid();
        mediador.Certificaciones.Add(Empresa(empresaId, "Ibertec GmbH", EstadoBlindaje42.PendienteRespuesta, 1));
        var cut = Renderizar(id);
        await AbrirGruposAsync(cut);

        await cut.FindAll(".cliente360-documentacion button").Single(b => b.TextContent.Trim() == "Ver historial")
            .ClickAsync(new MouseEventArgs());

        cut.FindComponent<DrawerBlindaje42>().Instance.EmpresaId.Should().Be(empresaId);
        cut.FindAll(".cliente360-documentacion button").Select(b => b.TextContent.Trim()).Should().NotContain("Gestionar");
    }

    [Fact]
    public void La_cifra_de_la_pestana_cuenta_el_certificado_una_sola_vez()
    {
        var (id, mediador) = Preparar();
        mediador.EmpresasDelCliente.Add(new EmpresaDeClienteDto(Guid.NewGuid(), "Ibertec GmbH", null));
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Ibertec GmbH", null, 0));
        mediador.Centros.Add(Centro("Planta Barakaldo"));
        mediador.Documentacion = new(1, [
            Requisito(TituloTgss, AmbitoAplicacion.Empresa, 1) with { EsCertificadoTgss = true },
            Requisito("Seguro RC", AmbitoAplicacion.Empresa, 1)]);

        var cut = Renderizar(id);

        cut.Find("[role=tab][aria-selected=true]").TextContent.Should().Contain("2").And.NotContain("3");
    }

    [Fact]
    public async Task Sin_Empresas_el_certificado_sale_como_requisito_normal_y_una_sola_vez()
    {
        var (id, mediador) = Preparar();
        mediador.Centros.Add(Centro("Planta Barakaldo"));
        mediador.Documentacion = new(1, [Requisito(TituloTgss, AmbitoAplicacion.Empresa, 1) with { EsCertificadoTgss = true }]);

        var cut = Renderizar(id);
        await AbrirGruposAsync(cut);

        Nombres(cut).Should().Equal(GrupoEmpresa, TituloTgss);
    }

    [Fact]
    public async Task Con_mas_de_diez_filas_el_grupo_enseña_diez_y_ver_los_restantes_despliega_el_resto()
    {
        var (id, mediador) = Preparar();
        mediador.Centros.Add(Centro("Planta Barakaldo"));
        mediador.Documentacion = new(1, Enumerable.Range(1, 13)
            .Select(n => Requisito($"Formación {n:00}", AmbitoAplicacion.Trabajador, 1)).ToList());

        var cut = Renderizar(id);
        await AbrirGruposAsync(cut);

        FilasDeDocumento(cut).Should().HaveCount(10);
        var boton = cut.Find(".cliente360-documentacion [data-accion=ver-restantes]");
        boton.TextContent.Trim().Should().Be("Ver los 3 restantes");

        await boton.ClickAsync(new MouseEventArgs());

        FilasDeDocumento(cut).Should().HaveCount(13);
        cut.FindAll(".cliente360-documentacion [data-accion=ver-restantes]").Should().BeEmpty();
    }

    [Fact]
    public async Task Con_un_unico_Centro_la_cifra_dice_en_su_Centro_y_la_fila_trae_su_pastilla()
    {
        var (id, mediador) = Preparar();
        Preparar_centro_con_requisitos(mediador);

        var cut = Renderizar(id);
        await AbrirGruposAsync(cut);

        var fila = FilasDeDocumento(cut).Single();
        fila.QuerySelector(".fila-relacion-cifra")!.TextContent.Should().Contain("En su Centro");
        fila.QuerySelector(".fila-relacion-estado")!.TextContent.Trim().Should().Be("Se exige");
    }

    [Fact]
    public async Task Ver_Centros_lleva_a_la_pestana_Centros_del_Cliente()
    {
        var (id, mediador) = Preparar(Roles.Consulta);
        Preparar_centro_con_requisitos(mediador);
        var cut = Renderizar(id);
        await AbrirGruposAsync(cut);

        await cut.FindAll(".cliente360-documentacion button").Single(b => b.TextContent.Trim() == "Ver Centros")
            .ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"clientes/{id}?pestana=centros");
    }

    [Fact]
    public void La_pestana_no_consulta_hasta_que_la_pagina_termina_su_cadena()
    {
        var (id, mediador) = Preparar();
        Preparar_centro_con_requisitos(mediador);

        var cut = Renderizar(id);

        // La lectura de la pestaña llega después de todo lo de la página (cabecera, Centros, Empresas, Gestor CAE).
        var orden = mediador.Enviadas.Select(e => e.GetType()).ToList();
        orden.IndexOf(typeof(ObtenerBlindajeEmpresasDeClienteQuery)).Should()
            .BeGreaterThan(orden.IndexOf(typeof(ObtenerCentrosQuery)))
            .And.BeGreaterThan(orden.IndexOf(typeof(ObtenerPersonasConCarteraQuery)));
        cut.FindComponent<DocumentosExigidosCliente>().Instance.Cargar.Should().BeTrue();
    }

    [Fact]
    public void Sin_Cargar_el_componente_no_consulta_nada()
    {
        var (id, mediador) = Preparar();

        var cut = Render<DocumentosExigidosCliente>(p => p.Add(x => x.ClienteId, id).Add(x => x.Cargar, false));

        mediador.Enviadas.Should().BeEmpty();
        cut.FindAll(".estado-cargando, [aria-busy=true]").Should().NotBeEmpty("sin lectura se queda en «cargando»");

        cut.Render(p => p.Add(x => x.ClienteId, id).Add(x => x.Cargar, true));

        mediador.Enviadas.Select(e => e.GetType()).Should()
            .Equal(typeof(ObtenerBlindajeEmpresasDeClienteQuery), typeof(ObtenerDocumentacionExigidaDeClienteQuery));
    }

    [Fact]
    public async Task Al_desmontarse_cancela_la_lectura_en_vuelo()
    {
        var (id, mediador) = Preparar();
        mediador.Colgar = true;

        Render<DocumentosExigidosCliente>(p => p.Add(x => x.ClienteId, id).Add(x => x.Cargar, true));
        var token = mediador.Tokens.Should().ContainSingle().Subject;
        token.IsCancellationRequested.Should().BeFalse();

        await DisposeComponentsAsync();

        token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Al_cambiar_de_Cliente_empresarial_cancela_la_lectura_del_anterior()
    {
        var (id, mediador) = Preparar();
        mediador.Colgar = true;

        var cut = Render<DocumentosExigidosCliente>(p => p.Add(x => x.ClienteId, id).Add(x => x.Cargar, true));
        var token = mediador.Tokens.Should().ContainSingle().Subject;

        cut.Render(p => p.Add(x => x.ClienteId, Guid.NewGuid()).Add(x => x.Cargar, false));

        token.IsCancellationRequested.Should().BeTrue("la página va a releer sobre el mismo DbContext");
    }

    [Fact]
    public async Task Espera_el_turno_de_la_pagina_antes_de_consultar()
    {
        var (id, mediador) = Preparar();
        var serie = new SerieDeLecturas();
        var turno = await serie.TomarAsync(CancellationToken.None);

        var cut = Render<DocumentosExigidosCliente>(p => p.Add(x => x.ClienteId, id).Add(x => x.Cargar, true).Add(x => x.Serie, serie));
        mediador.Enviadas.Should().BeEmpty("otra lectura de la página tiene el DbContext");

        turno.Dispose();

        cut.WaitForAssertion(() => mediador.Enviadas.Should().HaveCount(2));
    }

    [Fact]
    public async Task Cada_Empresa_lleva_su_propio_estado_del_certificado_y_solo_los_descubiertos_llevan_tono_de_peligro()
    {
        var (id, mediador) = Preparar();
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Ibertec GmbH", EstadoBlindaje42.NoExonerada, 1));
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Grúas Aldapa S.L.", EstadoBlindaje42.ExoneradaPorCertificacion, 1));
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Obras del Ebro S.L.", null, 0));

        var cut = Renderizar(id);
        await AbrirGruposAsync(cut);

        var filas = FilasDeDocumento(cut);
        filas.Select(f => f.QuerySelector(".fila-relacion-estado")!.TextContent.Trim()).Should()
            .Equal("Con descubiertos", "Exonerada (certificación)", "Sin solicitar");
        filas.Select(f => f.GetAttribute("data-tono")).Should().Equal("peligro", null, null);
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
    public async Task Con_Empresas_y_sin_requisitos_solo_queda_la_fila_del_certificado_y_no_el_vacio()
    {
        var (id, mediador) = Preparar();
        mediador.Certificaciones.Add(Empresa(Guid.NewGuid(), "Ibertec GmbH", null, 0));

        var cut = Renderizar(id);
        await AbrirGruposAsync(cut);

        Nombres(cut).Should().Equal(GrupoEmpresa, TituloTgss);
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
    public async Task Si_falla_la_lectura_de_requisitos_Reintentar_la_repite_y_pinta_las_filas()
    {
        var (id, mediador) = Preparar();
        Preparar_centro_con_requisitos(mediador);
        // Dos fallos: la lectura de la cifra de la pestaña (la página) y la de la propia lista, que sin precarga pregunta de nuevo.
        mediador.FallosDeDocumentacion = 2;

        var cut = Renderizar(id);

        cut.Find(".cliente360-documentacion .estado-vacio").TextContent.Should().Contain("No pudimos cargar los documentos exigidos");
        await cut.FindAll(".cliente360-documentacion button").Single(b => b.TextContent.Trim() == "Reintentar")
            .ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal(GrupoEmpresa));
        await AbrirGruposAsync(cut);
        Nombres(cut).Should().Equal(GrupoEmpresa, "Evaluación de riesgos");
        mediador.Enviadas.OfType<ObtenerDocumentacionExigidaDeClienteQuery>().Should().HaveCount(3);
    }

    [Fact]
    public async Task Consulta_abre_el_historial_de_una_Empresa_en_lectura_al_pulsar_su_fila()
    {
        var (id, mediador) = Preparar(Roles.Consulta);
        var empresaId = Guid.NewGuid();
        mediador.Certificaciones.Add(Empresa(empresaId, "Ibertec GmbH", EstadoBlindaje42.PendienteRespuesta, 1));
        mediador.Historial.Add(new SolicitudCertificacionTgssDto(Guid.NewGuid(),
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null, null,
            EstadoBlindaje42.PendienteRespuesta, null, false, null));
        var cut = Renderizar(id);

        await AbrirGruposAsync(cut);
        await cut.Find(".cliente360-documentacion .fila-relacion-desplegado .fila-relacion-nombre-boton").ClickAsync(new MouseEventArgs());

        var drawer = cut.FindComponent<DrawerBlindaje42>();
        drawer.Instance.Visible.Should().BeTrue("como en la antigua pestaña Blindaje 42.1, cualquier rol ve el historial");
        drawer.Instance.EmpresaId.Should().Be(empresaId);
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerHistorialCertificacionesTgssQuery>().Should().NotBeEmpty());
        drawer.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Registrar solicitud", "sin escritura no hay acciones");
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

        // El grupo de la Empresa se abre y deja ver el certificado de cada Empresa con su estado (lectura).
        await AbrirGruposAsync(cut);
        cut.Find(".cliente360-documentacion .fila-relacion-desplegado").TextContent
            .Should().Contain("Ibertec GmbH").And.Contain("Pendiente TGSS");

        // Sin contar el chevron y el nombre del certificado (abre el historial, ver la prueba de Consulta): «Gestionar»
        // solo con escritura y «Ver Centros» (navegación) para cualquier rol.
        var botones = cut.FindAll(".cliente360-documentacion button")
            .Where(b => !b.ClassList.Contains("fila-relacion-desplegar") && !b.ClassList.Contains("fila-relacion-nombre-boton"))
            .Select(b => b.TextContent.Trim()).ToList();
        botones.Should().Equal(puedeEscribir ? ["Gestionar", "Ver Centros"] : ["Ver historial", "Ver Centros"],
            "sin escritura la fila del certificado abre el mismo drawer en lectura");

        if (!puedeEscribir)
            return;

        await cut.FindAll(".cliente360-documentacion button").Single(b => b.TextContent.Trim() == "Gestionar")
            .ClickAsync(new MouseEventArgs());

        var drawer = cut.FindComponent<DrawerBlindaje42>();
        drawer.Instance.Visible.Should().BeTrue();
        drawer.Instance.EmpresaId.Should().Be(empresaId);
        await drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Registrar solicitud")
            .ClickAsync(new MouseEventArgs());
        mediador.Enviadas.OfType<SolicitarCertificacionTgssCommand>().Should()
            .ContainSingle(c => c.ClienteId == id && c.EmpresaId == empresaId);

        // Tras la solicitud solo se relee el certificado; los requisitos no cambian.
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerBlindajeEmpresasDeClienteQuery>().Should().HaveCount(2));
        mediador.Enviadas.OfType<ObtenerDocumentacionExigidaDeClienteQuery>().Should().ContainSingle();
    }
}

