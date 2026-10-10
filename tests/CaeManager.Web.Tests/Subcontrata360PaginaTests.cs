using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Commands.CambiarNivelServicioSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarVerificacionExterna;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCumplimientoSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using CaeManager.Application.Subcontratas.Queries.ObtenerSupervisionSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerTrabajadoresDocumentacionPorSubcontrata;
using CaeManager.Application.Usuarios.Queries.ObtenerDobleFactorPropio;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Features.Subcontratas.Components;
using CaeManager.Web.Features.Subcontratas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Subcontrata 360, la página (<see cref="SubcontrataDetalle"/>, <c>/subcontratas/{id}</c>) contra su mockup
/// «Subcontrata 360 página TALVEG», primer incremento. Prueban efectos: qué se ve, qué consultas y órdenes salen
/// y a dónde se navega. bUnit no evalúa CSS, así que ninguno afirma un estilo.
///
/// <para>
/// Lo que aquí se prueba de los roles es lo que la página OFRECE. Quién puede registrar, cambiar de nivel,
/// eliminar o leer credenciales lo deciden los behaviors de Application, que tienen sus propios tests: el
/// mediador falso no los ejecuta.
/// </para>
/// </summary>
public class Subcontrata360PaginaTests : BunitContext
{
    public Subcontrata360PaginaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        ComponentFactories.AddStub<PestanaAgendaContactos>();
        ComponentFactories.AddStub<PestanaHistorial>();
        // Formulario POST con antiforgery: tiene sus propios tests.
        ComponentFactories.AddStub<CaeManager.Web.Components.Layout.EnlaceProfundoOtraEmpresa>();
    }

    private sealed class MediatorFalso : IMediator
    {
        public SubcontrataDetalleDto? Detalle { get; set; }
        public FraccionCumplimiento? Cumplimiento { get; set; }
        public List<TrabajadorDocumentacionSubcontrataDto> Trabajadores { get; set; } = [];
        public SupervisionSubcontrataDto Supervision { get; set; } = new([], []);
        public List<CentroConActividadDto> Centros { get; set; } = [];
        public CredencialAccesoSubcontrataDto? Credencial { get; set; }
        public bool DobleFactor { get; set; } = true;
        public bool FallaElCumplimiento { get; set; }

        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (FallaElCumplimiento && request is ObtenerCumplimientoSubcontrataQuery)
                return Task.FromException<TResponse>(new InvalidOperationException("fallo transitorio"));
            return Task.FromResult((TResponse)Responder(request)!);
        }

        private object? Responder(object request) => request switch
        {
            ObtenerSubcontrataPorIdQuery => Detalle,
            ObtenerCumplimientoSubcontrataQuery => Cumplimiento,
            ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[],
            ObtenerEmpresasParaSelectorQuery => (IReadOnlyList<EmpresaSelectorDto>)[],
            ObtenerTrabajadoresDocumentacionPorSubcontrataQuery => (IReadOnlyList<TrabajadorDocumentacionSubcontrataDto>)Trabajadores,
            ObtenerSupervisionSubcontrataQuery => Supervision,
            ObtenerCentrosConActividadDeSubcontrataQuery => (IReadOnlyList<CentroConActividadDto>)Centros,
            ObtenerDobleFactorPropioQuery => DobleFactor,
            ObtenerCredencialAccesoSubcontrataQuery => Credencial,
            CambiarNivelServicioSubcontrataCommand or EliminarSubcontrataCommand or EliminarVerificacionExternaSubcontrataCommand => Result.Exito(),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
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

    /// <summary>La página monta DrawerGestionDocumento, que los inyecta; cerrado, nadie los toca.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Con el drawer cerrado no se abre ningún archivo.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Con el drawer cerrado no se convierte nada.");
    }

    // ── Montaje ───────────────────────────────────────────────────────────

    private static readonly Guid SubcontrataId = Guid.NewGuid();
    private static readonly Guid Version = Guid.NewGuid();
    private static readonly Guid CentroId = Guid.NewGuid();

    private static SubcontrataDetalleDto Detalle(NivelServicioSubcontrata nivel = NivelServicioSubcontrata.Supervisada, Guid? id = null) =>
        new(id ?? SubcontrataId, "Transportes Terminator S.L.", "B70005204", new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), [], [], Version, nivel);

    private static TrabajadorDocumentacionSubcontrataDto Trabajador(string nombre, EstadoDocumento peor, params DocumentoRequeridoDto[] documentos) =>
        new(Guid.NewGuid(), nombre, "12345678Z", peor, documentos,
            new FraccionCumplimiento(documentos.Count(d => d.Estado is EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad), documentos.Length));

    private static DocumentoRequeridoDto Documento(string tipo, EstadoDocumento estado, DateOnly? vence = null) =>
        new(estado == EstadoDocumento.Faltante ? null : Guid.NewGuid(), Guid.NewGuid(), tipo, estado, vence);

    /// <summary>En desorden a propósito: la página tiene que ordenar del peor estado al mejor.</summary>
    private static List<TrabajadorDocumentacionSubcontrataDto> Plantilla() =>
    [
        Trabajador("Vera Vigente", EstadoDocumento.Vigente, Documento("Contrato de trabajo", EstadoDocumento.SinCaducidad)),
        Trabajador("Pablo Próximo", EstadoDocumento.Proximo, Documento("Formación Art. 19", EstadoDocumento.Proximo, new DateOnly(2026, 12, 1))),
        Trabajador("Fabio Faltante", EstadoDocumento.Faltante, Documento("Aptitud médica", EstadoDocumento.Faltante)),
        Trabajador("Úrsula Urgente", EstadoDocumento.Urgente, Documento("Aptitud médica", EstadoDocumento.Urgente, new DateOnly(2026, 10, 12))),
        Trabajador("Víctor Vencido", EstadoDocumento.Vencido, Documento("Aptitud médica", EstadoDocumento.Vencido, new DateOnly(2026, 9, 1))),
    ];

    private static SupervisionSubcontrataDto Supervision() => new(
        [
            new SupervisionCentroDto(CentroId, "Almacén Vigo", "Cyberdyne Ibérica S.A.",
            [
                new SupervisionTipoDto(Guid.NewGuid(), "Formación Art. 19", Exigido: true, EstadoSupervision.SinVerificar, null),
                new SupervisionTipoDto(Guid.NewGuid(), "Aptitud médica", Exigido: true, EstadoSupervision.Vigente,
                    new UltimaVerificacionDto(Guid.NewGuid(), new DateOnly(2026, 9, 20), ResultadoVerificacionExterna.Valido, new DateOnly(2027, 9, 20), null, true, "captura.png")),
                new SupervisionTipoDto(Guid.NewGuid(), "Seguro de vida", Exigido: false, EstadoSupervision.SinVerificar, null),
            ])
        ],
        [new CentroSeleccionableDto(CentroId, "Almacén Vigo", "Cyberdyne Ibérica S.A.")]);

    private MediatorFalso Montar(string rol = Roles.GestorCae, Action<MediatorFalso>? ajustar = null)
    {
        var mediador = new MediatorFalso
        {
            Detalle = Detalle(),
            Cumplimiento = new FraccionCumplimiento(29, 38),
            Trabajadores = Plantilla(),
            Supervision = Supervision(),
            Centros = [new CentroConActividadDto(CentroId, "Almacén Vigo", "Cyberdyne Ibérica S.A.", 3)],
            Credencial = new CredencialAccesoSubcontrataDto("https://portal.example", null, "terminator", "secreta", null),
        };
        ajustar?.Invoke(mediador);
        this.ConRolDeEscritura(rol);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddLocalization();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return mediador;
    }

    private IRenderedComponent<SubcontrataDetalle> Renderizar(string consulta = "")
    {
        Navegacion.NavigateTo($"subcontratas/{SubcontrataId}{consulta}");
        var cut = Render<SubcontrataDetalle>(p => p.Add(x => x.SubcontrataId, SubcontrataId));
        cut.WaitForAssertion(() => cut.FindAll("[aria-busy=true]").Should().BeEmpty());
        return cut;
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private static IElement Boton(IRenderedComponent<SubcontrataDetalle> cut, string texto) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto);

    private static bool HayBoton(IRenderedComponent<SubcontrataDetalle> cut, string texto) =>
        cut.FindAll("button").Any(b => b.TextContent.Trim() == texto);

    private static void AbrirMenuDeCabeceraYPulsar(IRenderedComponent<SubcontrataDetalle> cut, string item)
    {
        // MenuAcciones no pinta sus ítems hasta que se abre.
        cut.Find(".cabecera-identidad .menu-acciones-disparador").Click();
        cut.FindAll(".cabecera-identidad .menu-acciones-item").Single(b => b.TextContent.Trim() == item).Click();
    }

    private static List<string> NombresDeFila(IRenderedComponent<SubcontrataDetalle> cut) =>
        cut.FindAll(".fila-relacion .fila-relacion-nombre").Select(n => n.TextContent.Trim()).ToList();

    // ── Cabecera ──────────────────────────────────────────────────────────

    [Fact]
    public void La_cabecera_identifica_la_subcontrata_con_su_anillo_y_su_nivel_de_servicio()
    {
        Montar();

        var cut = Renderizar();

        var cabecera = cut.Find("[data-pieza=cabecera-identidad]");
        cabecera.QuerySelector("h1")!.TextContent.Should().Be("Transportes Terminator S.L.");
        cabecera.QuerySelector("[data-pieza=anillo]")!.GetAttribute("aria-label").Should().Be("76 % · 29 de 38 al día");
        cabecera.TextContent.Should().Contain("B70005204").And.Contain("Supervisada").And.Contain("5 trabajadores");
    }

    [Fact]
    public void Sin_requisitos_el_anillo_no_inventa_un_porcentaje()
    {
        Montar(ajustar: m => m.Cumplimiento = FraccionCumplimiento.SinRequisitos);

        var cut = Renderizar();

        cut.Find("[data-pieza=anillo]").GetAttribute("aria-label").Should().Be("Sin requisitos documentales todavía");
    }

    [Fact]
    public void Una_subcontrata_que_no_existe_o_esta_fuera_del_alcance_se_ve_igual_y_no_pide_nada_mas()
    {
        var mediador = Montar(ajustar: m => m.Detalle = null);

        var cut = Renderizar();

        cut.Markup.Should().Contain("No encontramos esta subcontrata");
        cut.FindAll("[data-pieza=cabecera-identidad]").Should().BeEmpty();
        mediador.Enviadas.Should().NotContain(p => p is ObtenerTrabajadoresDocumentacionPorSubcontrataQuery
            || p is ObtenerSupervisionSubcontrataQuery || p is ObtenerCredencialAccesoSubcontrataQuery
            || p is ObtenerCumplimientoSubcontrataQuery);
    }

    /// <summary>Revisión puente: un fallo del dato secundario no puede presentarse como «no encontrada».</summary>
    [Fact]
    public void Si_falla_la_consulta_del_anillo_la_ficha_se_abre_sin_porcentaje()
    {
        Montar(ajustar: m => m.FallaElCumplimiento = true);

        var cut = Renderizar();

        cut.Find("[data-pieza=cabecera-identidad] h1").TextContent.Should().Be("Transportes Terminator S.L.");
        cut.Markup.Should().NotContain("No encontramos esta subcontrata");
        cut.Find("[data-pieza=anillo]").GetAttribute("aria-label").Should().Be("Sin requisitos documentales todavía");
    }

    // ── Trabajadores ──────────────────────────────────────────────────────

    [Fact]
    public void Los_trabajadores_salen_del_peor_estado_al_mejor_con_el_vocabulario_de_la_pagina()
    {
        Montar();

        var cut = Renderizar();

        // Vencido antes que Faltante: orden de severidad fijado el 2026-10-03 (SeveridadEstadoDocumento).
        NombresDeFila(cut).Should().Equal("Víctor Vencido", "Fabio Faltante", "Úrsula Urgente", "Pablo Próximo", "Vera Vigente");
        var filas = cut.FindAll(".fila-relacion");
        filas.Select(f => f.QuerySelector(".fila-relacion-estado")!.TextContent.Trim())
            .Should().Equal("Vencido", "Pendiente", "Por vencer", "Por vencer", "Vigente");
        // Rojo lo que ya impide, ámbar lo urgente; «Por vencer» sin urgencia y «Vigente» no tiñen.
        filas.Select(f => f.GetAttribute("data-tono")).Should().Equal("peligro", "peligro", "advertencia", null, null);
        cut.Markup.Should().Contain("Mostrando 5 de 5 · del peor estado al mejor");
    }

    [Fact]
    public void El_filtro_de_estado_es_de_seleccion_multiple_y_Todos_la_borra()
    {
        Montar();
        var cut = Renderizar();

        // Próximo y Urgente comparten rótulo: un solo contador «Por vencer», con los dos.
        cut.FindAll(".filtro-estados-opcion").Select(b => b.TextContent.Trim())
            .Should().Equal("Todos · 5", "Vencido · 1", "Pendiente · 1", "Por vencer · 2", "Vigente · 1");

        cut.FindAll(".filtro-estados-opcion").First(b => b.TextContent.StartsWith("Vencido")).Click();
        cut.FindAll(".filtro-estados-opcion").First(b => b.TextContent.StartsWith("Por vencer")).Click();

        NombresDeFila(cut).Should().Equal("Víctor Vencido", "Úrsula Urgente", "Pablo Próximo");
        cut.Markup.Should().Contain("Mostrando 3 de 5");

        cut.FindAll(".filtro-estados-opcion").First(b => b.TextContent.StartsWith("Todos")).Click();

        NombresDeFila(cut).Should().HaveCount(5);
    }

    [Fact]
    public void Desplegar_un_trabajador_ensena_sus_documentos_con_la_accion_que_pide_cada_estado()
    {
        Montar();
        var cut = Renderizar();

        cut.FindAll(".fila-relacion-desplegar")[0].Click(); // Víctor Vencido
        cut.FindAll(".fila-relacion-desplegar")[1].Click(); // Fabio Faltante

        var subfilas = cut.FindAll(".subcontrata360-subfila");
        subfilas.Should().HaveCount(2);
        subfilas[0].QuerySelector(".subcontrata360-subfila-accion button")!.TextContent.Trim().Should().Be("Renovar");
        subfilas[1].TextContent.Should().Contain("Pendiente").And.Contain("Se exige y no hay documento");
        subfilas[1].QuerySelector(".subcontrata360-subfila-accion button")!.TextContent.Trim().Should().Be("Subir");
    }

    /// <summary>
    /// Revisión puente: la consulta entrega una fila por documento operativo, y dos copias del mismo tipo pueden
    /// coexistir. Con la clave por tipo eran dos hermanos con la misma @key, que mata el circuito.
    /// </summary>
    [Fact]
    public void Dos_copias_operativas_del_mismo_tipo_son_dos_filas_y_no_rompen_el_desplegable()
    {
        var tipo = Guid.NewGuid();
        Montar(ajustar: m => m.Trabajadores =
        [
            new(Guid.NewGuid(), "Sonia Cano Prieto", "12345678Z", EstadoDocumento.Vencido,
            [
                new DocumentoRequeridoDto(Guid.NewGuid(), tipo, "Aptitud médica", EstadoDocumento.Vencido, new DateOnly(2026, 3, 12)),
                new DocumentoRequeridoDto(Guid.NewGuid(), tipo, "Aptitud médica", EstadoDocumento.Vigente, new DateOnly(2027, 3, 12)),
            ], new FraccionCumplimiento(1, 1))
        ]);
        var cut = Renderizar();

        cut.Find(".fila-relacion-desplegar").Click();

        cut.FindAll(".subcontrata360-subfila").Should().HaveCount(2);
    }

    // ── Sin banda de incidencias ──────────────────────────────────────────

    /// <summary>
    /// Decisión de producto del 2026-10-09: la cabecera no repite en una banda lo que las listas ya dicen. Quién
    /// tiene documentación vencida o pendiente se ve en su fila (y se corrige desde ella); que hay algo por
    /// verificar lo avisa el contador de la pestaña Supervisión.
    /// </summary>
    [Fact]
    public void Con_trabajadores_y_centros_con_problema_no_hay_banda_y_lo_avisan_las_pestanas()
    {
        Montar();

        var cut = Renderizar();

        cut.FindAll("[data-pieza=banda]").Should().BeEmpty();
        cut.FindAll(".pestanas-contador-alerta").Should().HaveCount(2, "Trabajadores y Supervisión siguen en alerta");
    }

    // ── Rol Consulta ──────────────────────────────────────────────────────

    [Fact]
    public void En_Consulta_la_pagina_no_ofrece_ordenes_ni_credenciales_y_no_las_pide()
    {
        var mediador = Montar(Roles.Consulta);

        var cut = Renderizar();

        cut.FindAll(".cabecera-identidad .menu-acciones-disparador").Should().BeEmpty();
        HayBoton(cut, "Registrar verificación").Should().BeFalse();
        cut.Markup.Should().NotContain("Acceso al portal");
        mediador.Enviadas.Should().NotContain(p => p is ObtenerCredencialAccesoSubcontrataQuery);
    }

    // ── Credenciales ──────────────────────────────────────────────────────

    [Fact]
    public void Las_credenciales_no_se_piden_al_abrir_la_pagina_sino_al_pulsar_Ver_credenciales()
    {
        var mediador = Montar();
        var cut = Renderizar();

        // Cada lectura queda auditada en Application: abrir la ficha no debe generar ninguna.
        mediador.Enviadas.Should().NotContain(p => p is ObtenerCredencialAccesoSubcontrataQuery);
        cut.Markup.Should().NotContain("terminator");

        Boton(cut, "Ver credenciales").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("terminator"));
        mediador.Enviadas.OfType<ObtenerCredencialAccesoSubcontrataQuery>().Should().ContainSingle()
            .Which.SubcontrataId.Should().Be(SubcontrataId);
        cut.Markup.Should().NotContain("secreta", "la contraseña llega enmascarada hasta que se revela");
    }

    [Fact]
    public void Sin_doble_factor_la_tarjeta_avisa_y_no_ofrece_pedir_las_credenciales()
    {
        var mediador = Montar(ajustar: m => m.DobleFactor = false);

        var cut = Renderizar();

        cut.Markup.Should().Contain("Activa la autenticación en dos pasos para verlas.");
        HayBoton(cut, "Ver credenciales").Should().BeFalse();
        mediador.Enviadas.Should().NotContain(p => p is ObtenerCredencialAccesoSubcontrataQuery);
    }

    [Fact]
    public void Revelar_vuelve_a_pedir_la_credencial_y_ocultar_la_suelta()
    {
        var mediador = Montar();
        var cut = Renderizar();
        Boton(cut, "Ver credenciales").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("terminator"));

        cut.Find(".subcontrata360-botones button[aria-pressed=false]").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("secreta"));
        mediador.Enviadas.OfType<ObtenerCredencialAccesoSubcontrataQuery>().Should().HaveCount(2,
            "revelar comprueba el doble factor en el momento, no enseña lo que ya tenía");

        cut.Find(".subcontrata360-botones button[aria-pressed=true]").Click();

        cut.Markup.Should().NotContain("secreta");
    }

    /// <summary>La página reutiliza el componente al navegar de una subcontrata a otra: la credencial de la anterior no viaja.</summary>
    [Fact]
    public void Al_pasar_a_otra_subcontrata_no_queda_la_credencial_de_la_anterior()
    {
        var mediador = Montar();
        var cut = Renderizar();
        Boton(cut, "Ver credenciales").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("terminator"));

        var otra = Guid.NewGuid();
        mediador.Detalle = Detalle(id: otra) with { RazonSocial = "Laboratorios Nemesis S.L." };
        cut.Render(p => p.Add(x => x.SubcontrataId, otra));

        cut.WaitForAssertion(() => cut.Find("[data-pieza=cabecera-identidad] h1").TextContent.Should().Be("Laboratorios Nemesis S.L."));
        cut.Markup.Should().NotContain("terminator");
        HayBoton(cut, "Ver credenciales").Should().BeTrue("la credencial de la nueva se pide de nuevo, no se hereda");
    }

    // ── Menú de cabecera ──────────────────────────────────────────────────

    [Fact]
    public void Cambiar_de_nivel_envia_la_orden_con_la_version_que_se_esta_viendo()
    {
        var mediador = Montar();
        var cut = Renderizar();

        AbrirMenuDeCabeceraYPulsar(cut, "Cambiar a Gestionada");

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<CambiarNivelServicioSubcontrataCommand>().Should().ContainSingle()
            .Which.Should().Be(new CambiarNivelServicioSubcontrataCommand(SubcontrataId, NivelServicioSubcontrata.Gestionada, Version)));
    }

    [Fact]
    public void Eliminar_pregunta_antes_y_al_confirmar_vuelve_al_listado()
    {
        var mediador = Montar();
        var cut = Renderizar();

        AbrirMenuDeCabeceraYPulsar(cut, "Eliminar");

        mediador.Enviadas.Should().NotContain(p => p is EliminarSubcontrataCommand, "eliminar pregunta antes");
        cut.FindAll("button").Last(b => b.ClassList.Any(c => c.Contains("destructivo"))).Click();

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<EliminarSubcontrataCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(SubcontrataId));
        new Uri(Navegacion.Uri).AbsolutePath.Should().Be("/subcontratas");
    }

    // ── Supervisión ───────────────────────────────────────────────────────

    [Fact]
    public void La_supervision_ensena_solo_lo_exigido_y_ofrece_verificar_lo_que_no_esta_comprobado()
    {
        Montar();

        var cut = Renderizar("?pestana=supervision");

        NombresDeFila(cut).Should().Equal("Formación Art. 19", "Aptitud médica");
        var filas = cut.FindAll(".fila-relacion");
        filas[0].TextContent.Should().Contain("Sin verificar").And.Contain("Nunca comprobada");
        filas[0].GetAttribute("data-tono").Should().Be("peligro");
        filas[0].QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Verificar");
        filas[1].QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Verificar");
        filas[1].QuerySelector("a.subcontrata360-enlace")!.GetAttribute("href").Should().StartWith("/subcontratas/verificaciones/");
    }

    [Fact]
    public void La_pestana_de_la_URL_que_no_existe_cae_en_Trabajadores()
    {
        Montar();

        var cut = Renderizar("?pestana=inventada");

        NombresDeFila(cut).Should().HaveCount(5);
    }

    [Fact]
    public void Eliminar_una_verificacion_pregunta_antes_y_envia_la_orden_de_esa_verificacion()
    {
        var mediador = Montar();
        var verificacion = mediador.Supervision.Centros[0].Tipos[1].UltimaVerificacion!.Id;
        var cut = Renderizar("?pestana=supervision");

        cut.Find(".fila-relacion .menu-acciones-disparador").Click();
        cut.Find(".fila-relacion .menu-acciones-item").Click();

        mediador.Enviadas.Should().NotContain(p => p is EliminarVerificacionExternaSubcontrataCommand, "eliminar pregunta antes");
        cut.FindAll("button").Last(b => b.ClassList.Any(c => c.Contains("destructivo"))).Click();

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<EliminarVerificacionExternaSubcontrataCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(verificacion));
    }

    [Fact]
    public void En_Consulta_la_supervision_no_ofrece_verificar_ni_eliminar()
    {
        Montar(Roles.Consulta);

        var cut = Renderizar("?pestana=supervision");

        NombresDeFila(cut).Should().Equal("Formación Art. 19", "Aptitud médica");
        cut.FindAll(".fila-relacion button").Should().BeEmpty();
        cut.FindAll("a.subcontrata360-enlace").Should().ContainSingle("ver la evidencia es lectura");
    }

    // ── Vocabulario de estado ─────────────────────────────────────────────

    [Theory]
    [InlineData(EstadoDocumento.Faltante, "Pendiente", TonoBadge.Peligro, TonoFila.Peligro, AccionDocumentoFicha360.Subir)]
    [InlineData(EstadoDocumento.Vencido, "Vencido", TonoBadge.Peligro, TonoFila.Peligro, AccionDocumentoFicha360.Renovar)]
    [InlineData(EstadoDocumento.EnTolerancia, "En tolerancia", TonoBadge.Tolerancia, TonoFila.Advertencia, AccionDocumentoFicha360.Renovar)]
    [InlineData(EstadoDocumento.Urgente, "Por vencer", TonoBadge.Advertencia, TonoFila.Advertencia, AccionDocumentoFicha360.Renovar)]
    [InlineData(EstadoDocumento.Proximo, "Por vencer", TonoBadge.Advertencia, null, null)]
    [InlineData(EstadoDocumento.SinConfirmar, "Sin confirmar", TonoBadge.Advertencia, null, AccionDocumentoFicha360.Confirmar)]
    [InlineData(EstadoDocumento.Vigente, "Vigente", TonoBadge.Exito, null, null)]
    [InlineData(EstadoDocumento.SinCaducidad, "Vigente", TonoBadge.Exito, null, null)]
    // Un estado que nadie ha declarado no degrada a favorable.
    [InlineData((EstadoDocumento)999, "Estado desconocido", TonoBadge.Peligro, null, null)]
    public void El_vocabulario_de_estado_de_la_ficha_declara_cada_estado(
        EstadoDocumento estado, string texto, TonoBadge tono, TonoFila? tonoFila, AccionDocumentoFicha360? accion)
    {
        EstadoDocumentoFicha360.Texto(estado).Should().Be(texto);
        EstadoDocumentoFicha360.Tono(estado).Should().Be(tono);
        EstadoDocumentoFicha360.TonoFila(estado).Should().Be(tonoFila);
        EstadoDocumentoFicha360.Accion(estado).Should().Be(accion);
    }

    // ── Contadores de estado en ?estado= ──────────────────────────────────

    private static List<string> ContadoresMarcados(IRenderedComponent<SubcontrataDetalle> cut) =>
        cut.FindAll(".filtro-estados-opcion[aria-pressed=true]").Select(b => b.TextContent.Trim()).ToList();

    [Fact]
    public void Marcar_contadores_los_deja_en_la_URL_separados_por_coma_y_Todos_quita_el_parametro()
    {
        Montar();
        var cut = Renderizar();

        // Se marcan en desorden: la URL los lleva en el orden de gravedad, para que el mismo filtro dé el mismo enlace.
        cut.FindAll(".filtro-estados-opcion").First(b => b.TextContent.StartsWith("Por vencer")).Click();
        cut.FindAll(".filtro-estados-opcion").First(b => b.TextContent.StartsWith("Vencido")).Click();

        new Uri(Navegacion.Uri).Query.Should().Be("?estado=Vencido%2CPorVencer");
        NombresDeFila(cut).Should().Equal("Víctor Vencido", "Úrsula Urgente", "Pablo Próximo");

        cut.FindAll(".filtro-estados-opcion").First(b => b.TextContent.StartsWith("Todos")).Click();

        new Uri(Navegacion.Uri).Query.Should().BeEmpty("si la URL conservara el estado, recargar lo repondría");
        NombresDeFila(cut).Should().HaveCount(5);
    }

    [Fact]
    public void Un_enlace_con_estado_abre_la_lista_filtrada_y_lo_desconocido_no_filtra()
    {
        Montar();

        var cut = Renderizar("?estado=Vencido,PorVencer,Inventado,7");

        NombresDeFila(cut).Should().Equal("Víctor Vencido", "Úrsula Urgente", "Pablo Próximo");
        ContadoresMarcados(cut).Should().Equal("Vencido · 1", "Por vencer · 2");
    }

    [Fact]
    public void Un_estado_que_no_existe_deja_la_lista_entera()
    {
        Montar();

        var cut = Renderizar("?estado=Inventado");

        NombresDeFila(cut).Should().HaveCount(5);
        ContadoresMarcados(cut).Should().Equal("Todos · 5");
    }

    [Fact]
    public void Cambiar_el_estado_desde_fuera_se_adopta_y_convive_con_la_pestana()
    {
        Montar();
        var cut = Renderizar("?estado=Vencido");
        NombresDeFila(cut).Should().Equal("Víctor Vencido");

        Navegacion.NavigateTo($"subcontratas/{SubcontrataId}?estado=Pendiente");
        cut.WaitForAssertion(() => ContadoresMarcados(cut).Should().Equal("Pendiente · 1"));

        cut.FindAll("[role=tab]").First(t => t.TextContent.StartsWith("Centros")).Click();
        new Uri(Navegacion.Uri).Query.Should().Contain("pestana=centros").And.Contain("estado=Pendiente");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("Vigente,Vencido", "Ficha360Vencido,Ficha360Vigente")]
    [InlineData("EnTolerancia,SinConfirmar,Pendiente,PorVencer", "EnTolerancia,Ficha360Pendiente,Ficha360PorVencer,SinConfirmar")]
    [InlineData("Ficha360Vencido,Desconocido,vencido,3", "")]
    public void Las_claves_de_contador_se_leen_de_la_URL_por_su_nombre_y_lo_demas_se_descarta(string? valor, string esperadas)
    {
        var claves = EstadoDocumentoFicha360.ClavesDesdeUrl(valor);

        claves.Order(StringComparer.Ordinal).Should().Equal(esperadas.Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Las_claves_de_contador_van_y_vuelven_de_la_URL_sin_perderse()
    {
        var todas = Enum.GetValues<EstadoDocumento>().Select(EstadoDocumentoFicha360.Clave)
            .Where(c => c != "Ficha360Desconocido").ToHashSet();

        var enUrl = EstadoDocumentoFicha360.ClavesEnUrl(todas);

        enUrl.Should().Be("Vencido,Pendiente,EnTolerancia,PorVencer,SinConfirmar,Vigente");
        EstadoDocumentoFicha360.ClavesDesdeUrl(enUrl).Should().BeEquivalentTo(todas);
        EstadoDocumentoFicha360.ClavesEnUrl(new HashSet<string>()).Should().BeNull("sin contadores el parámetro desaparece de la URL");
    }
}
