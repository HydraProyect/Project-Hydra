using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Features.Inbound.TenantTec.Components;
using CaeManager.Web.Features.Inbound.TenantTec.Pages;
using CaeManager.Web.Features.Inbound.TenantTec.Servicios;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantallas del Tenant TEC (Inbound): Requerimientos con su detalle, Subir documento, Quién lee mis documentos y el aviso de
/// contrato nuevo. Los datos son de prueba: en ejecución los servicios son los «sin backend» (B-09, B-11, B-20), que no
/// inventan nada y avisan de que la función todavía no está disponible.
/// </summary>
public sealed class InboundTenantTecTests : BunitContext
{
    private static readonly Guid PendienteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RechazadoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ValidadoId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtroClienteEmpresarialId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public InboundTenantTecTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
    }

    private static RequerimientoTec Requerimiento(
        Guid id, string cliente, string nombre, EstadoRequerimientoTec estado, AccionRequerimientoTec accion,
        string? motivo = null, bool conDocumento = false, bool aptitud = false,
        IReadOnlyList<NivelRequerimientoTec>? niveles = null, IReadOnlyList<VersionDocumentoTec>? historial = null) =>
        new(id, cliente, nombre, "Montajes Moncayo S.L.", "Contratista", EntidadRequerimientoTec.Trabajador, "Luis Gómez Ara", "DNI 12345678Z",
            "Planta Zaragoza Norte", estado, VigenciaRequerimientoTec.Vigente, new DateOnly(2027, 3, 4), 2, motivo, null,
            "Certificado con resultado «Apto».", accion, conDocumento, aptitud,
            [new CriterioLecturaTec("Fecha de emisión", LecturaCampoTec.Correcto), new CriterioLecturaTec("Firma y sello", LecturaCampoTec.Error)],
            niveles ?? [new NivelRequerimientoTec("Legal", "Aptitud médica", new DateOnly(2027, 3, 4))],
            historial ?? []);

    private static IReadOnlyList<RequerimientoTec> Datos() =>
    [
        Requerimiento(PendienteId, "Logística Delta Sur S.A.", "Aptitud médica", EstadoRequerimientoTec.PendienteDeSubir, AccionRequerimientoTec.Subir,
            aptitud: true, niveles:
            [
                new NivelRequerimientoTec("Legal", "Aptitud médica", new DateOnly(2027, 3, 4)),
                new NivelRequerimientoTec("Riesgo", "Aptitud médica", new DateOnly(2027, 3, 4)),
            ]),
        Requerimiento(RechazadoId, "Logística Delta Sur S.A.", "Formación en altura", EstadoRequerimientoTec.Rechazado, AccionRequerimientoTec.Subir,
            motivo: "Documento ilegible", conDocumento: true,
            historial: [new VersionDocumentoTec(Guid.NewGuid(), new DateTimeOffset(2026, 11, 5, 17, 20, 0, TimeSpan.Zero),
                EstadoRequerimientoTec.Rechazado, "Javier Lacasa", false, "Documento ilegible", new DateOnly(2026, 11, 4))]),
        Requerimiento(ValidadoId, "Logística Delta Sur S.A.", "Alta en la Seguridad Social", EstadoRequerimientoTec.Validado, AccionRequerimientoTec.Ninguna),
        Requerimiento(OtroClienteEmpresarialId, "Frío Industrial Cierzo S.L.", "Seguro de responsabilidad civil", EstadoRequerimientoTec.PendienteDeValidar, AccionRequerimientoTec.Ninguna),
    ];

    private sealed class RequerimientosDoble(bool disponible, IReadOnlyList<RequerimientoTec> datos) : IRequerimientosTenantTec
    {
        public bool Disponible => disponible;

        public Task<IReadOnlyList<RequerimientoTec>> ListarAsync(CancellationToken ct = default) => Task.FromResult(datos);
    }

    private sealed class SubidaDoble(bool disponible, RespuestaSubidaTec respuesta) : ISubidaDocumentoTenantTec
    {
        public bool Disponible => disponible;

        public SolicitudSubidaTec? Recibida { get; private set; }

        public Task<RespuestaSubidaTec> SubirAsync(SolicitudSubidaTec solicitud, CancellationToken ct = default)
        {
            Recibida = solicitud;
            return Task.FromResult(respuesta);
        }
    }

    private void UsarSinBackend()
    {
        Services.AddScoped<IRequerimientosTenantTec, RequerimientosTenantTecSinBackend>();
        Services.AddScoped<ISubidaDocumentoTenantTec, SubidaDocumentoTenantTecSinBackend>();
        Services.AddScoped<IQuienLeeTenantTec, QuienLeeTenantTecSinBackend>();
        Services.AddScoped<IAvisoContratoTenantTec, AvisoContratoTenantTecSinBackend>();
    }

    private static IElement Boton(IRenderedComponent<IComponent> cut, string texto) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto);

    // El drawer se pinta sobre la lista: el «Subir» de la fila y el del drawer no son el mismo botón.
    private static IElement BotonDelDialogo(IRenderedComponent<IComponent> cut, string texto) =>
        cut.Find("[role=dialog]").QuerySelectorAll("button").First(b => b.TextContent.Trim() == texto);

    // ---- Tarjeta 7 · Requerimientos ------------------------------------------------------------------------------------

    [Fact]
    public void Requerimientos_sin_backend_avisa_de_que_todavia_no_esta_disponible_y_no_enseña_ninguna_lista()
    {
        UsarSinBackend();

        var cut = Render<Requerimientos>();

        cut.Markup.Should().Contain("Los requerimientos todavía no están disponibles");
        cut.FindAll("table").Should().BeEmpty();
        cut.Find("h1").TextContent.Should().Contain("Requerimientos");
    }

    [Fact]
    public void Requerimientos_pinta_pestañas_por_Cliente_empresarial_franja_de_estados_y_las_acciones_de_cada_fila()
    {
        Services.AddScoped<IRequerimientosTenantTec>(_ => new RequerimientosDoble(true, Datos()));
        Services.AddScoped<ISubidaDocumentoTenantTec, SubidaDocumentoTenantTecSinBackend>();

        var cut = Render<Requerimientos>();

        cut.Find("h1").TextContent.Should().Contain("Requerimientos");
        cut.Markup.Should().Contain("Contratos de acceso");
        var pestañas = cut.FindAll("[role=tab]").Select(t => t.TextContent).ToList();
        pestañas.Should().HaveCount(2);
        pestañas.Should().Contain(t => t.Contains("Logística Delta Sur S.A.") && t.Contains("2"));
        pestañas.Should().Contain(t => t.Contains("Frío Industrial Cierzo S.L."));

        var franja = cut.Find(".franja-estado").TextContent;
        foreach (var estado in new[] { "Rechazado", "Pendiente de subir", "Incompleto", "Pendiente de validar", "Validado" })
        {
            franja.Should().Contain(estado);
        }

        cut.FindAll("th").Select(t => t.TextContent.Trim()).Should().Contain(["Requerimiento", "Empresa", "Entidad", "Centro", "Estado", "Vigencia"]);
        var filas = cut.FindAll("tbody tr");
        filas.Should().HaveCount(3);
        filas.Single(f => f.TextContent.Contains("Aptitud médica")).TextContent.Should().Contain("Cubre 2 niveles: Legal, Riesgo");
        filas.Single(f => f.TextContent.Contains("Formación en altura")).TextContent.Should().Contain("Motivo: Documento ilegible");
        filas.Count(f => f.QuerySelectorAll("button").Any(b => b.TextContent.Trim() == "Subir")).Should().Be(2);
        filas.Single(f => f.TextContent.Contains("Alta en la Seguridad Social")).QuerySelectorAll("button")
            .Should().NotContain(b => b.TextContent.Trim() == "Subir");
    }

    [Fact]
    public void Requerimientos_el_filtro_de_estado_acota_la_lista_y_sin_coincidencias_dice_que_ninguna_coincide()
    {
        Services.AddScoped<IRequerimientosTenantTec>(_ => new RequerimientosDoble(true, Datos()));
        Services.AddScoped<ISubidaDocumentoTenantTec, SubidaDocumentoTenantTecSinBackend>();
        var cut = Render<Requerimientos>();

        cut.Find("[data-estado=Rechazado]").Click();

        cut.FindAll("tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("Formación en altura");

        cut.Find("[data-estado=Rechazado]").Click();
        cut.Find("input[type=search]").Input("zzz");

        // El buscador espera 300 ms antes de filtrar.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ningún requerimiento coincide con los filtros."), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Requerimientos_el_nombre_abre_el_detalle_con_criterio_motivos_niveles_e_historial_y_sin_documento_dice_que_falta()
    {
        Services.AddScoped<IRequerimientosTenantTec>(_ => new RequerimientosDoble(true, Datos()));
        Services.AddScoped<ISubidaDocumentoTenantTec, SubidaDocumentoTenantTecSinBackend>();
        var cut = Render<Requerimientos>();

        cut.FindAll("button.req-nombre").First(b => b.TextContent == "Aptitud médica").Click();

        var detalle = cut.Find("[role=dialog]").TextContent;
        detalle.Should().Contain("Todavía no has subido ningún documento.");
        detalle.Should().Contain("Criterio de aceptación");
        detalle.Should().Contain("Por qué se pide");
        detalle.Should().Contain("Se pide una sola vez: un documento cubre los 2 niveles.");
        detalle.Should().Contain("Sin documentos anteriores.");
        cut.Find("[role=dialog]").QuerySelectorAll("li").Should().Contain(li => li.TextContent.Contains("Firma y sello") && li.TextContent.Contains("No coincide"));
    }

    [Fact]
    public void Requerimientos_con_documento_el_detalle_ensena_el_motivo_del_rechazo_y_el_historial()
    {
        Services.AddScoped<IRequerimientosTenantTec>(_ => new RequerimientosDoble(true, Datos()));
        Services.AddScoped<ISubidaDocumentoTenantTec, SubidaDocumentoTenantTecSinBackend>();
        var cut = Render<Requerimientos>();

        cut.FindAll("button.req-nombre").First(b => b.TextContent == "Formación en altura").Click();

        var detalle = cut.Find("[role=dialog]").TextContent;
        detalle.Should().Contain("Motivo del rechazo: Documento ilegible");
        detalle.Should().Contain("Último documento");
        detalle.Should().Contain("Subido por Javier Lacasa");
        detalle.Should().Contain("Emisión 04/11/2026");
    }

    // ---- Tarjeta 12 · Subir documento ----------------------------------------------------------------------------------

    [Fact]
    public void SubirDocumento_en_la_ruta_de_subida_muestra_el_requerimiento_y_deja_Subir_deshabilitado_con_su_motivo_sin_backend()
    {
        Services.AddScoped<IRequerimientosTenantTec>(_ => new RequerimientosDoble(true, Datos()));
        Services.AddScoped<ISubidaDocumentoTenantTec, SubidaDocumentoTenantTecSinBackend>();

        var cut = Render<Requerimientos>(p => p.Add(x => x.RequerimientoId, PendienteId));

        var dialogo = cut.Find("[role=dialog]");
        dialogo.TextContent.Should().Contain("Subir documento");
        dialogo.TextContent.Should().Contain("Aptitud médica");
        dialogo.TextContent.Should().Contain("Cliente");
        dialogo.TextContent.Should().Contain("Sube solo el certificado de aptitud; un informe con datos de salud no se acepta ni se guarda.");
        dialogo.TextContent.Should().Contain("PDF, Word o foto (JPG, PNG, HEIC), hasta 10 MB.");
        dialogo.TextContent.Should().Contain("Usar la cámara");
        dialogo.TextContent.Should().Contain("Comprobación al momento");
        var subir = BotonDelDialogo(cut, "Subir");
        subir.HasAttribute("disabled").Should().BeTrue();
        subir.GetAttribute("title").Should().Contain("todavía no está disponible");
    }

    [Fact]
    public void SubirDocumento_un_formato_no_admitido_se_marca_en_la_comprobacion_al_momento_y_no_deja_subir()
    {
        var subida = new SubidaDoble(true, new RespuestaSubidaTec(ResultadoSubidaTec.Subido));
        Services.AddScoped<IRequerimientosTenantTec>(_ => new RequerimientosDoble(true, Datos()));
        Services.AddScoped<ISubidaDocumentoTenantTec>(_ => subida);
        var cut = Render<Requerimientos>(p => p.Add(x => x.RequerimientoId, PendienteId));

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("zip", "datos.zip"));

        cut.Find("[role=dialog]").TextContent.Should().Contain("Formato no admitido: ZIP");
        BotonDelDialogo(cut, "Subir").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void SubirDocumento_un_PDF_se_sube_y_el_estado_hecho_dice_que_lo_validara_el_Gestor_CAE_del_Cliente_empresarial()
    {
        var subida = new SubidaDoble(true, new RespuestaSubidaTec(ResultadoSubidaTec.Subido, RechazadoId, "Formación en altura · Luis Gómez Ara · Planta Zaragoza Norte"));
        Services.AddScoped<IRequerimientosTenantTec>(_ => new RequerimientosDoble(true, Datos()));
        Services.AddScoped<ISubidaDocumentoTenantTec>(_ => subida);
        var cut = Render<Requerimientos>(p => p.Add(x => x.RequerimientoId, PendienteId));
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("%PDF", "aptitud.pdf"));

        BotonDelDialogo(cut, "Subir").HasAttribute("disabled").Should().BeFalse();
        BotonDelDialogo(cut, "Subir").Click();

        subida.Recibida.Should().NotBeNull();
        subida.Recibida!.RequerimientoId.Should().Be(PendienteId);
        subida.Recibida.NombreArchivo.Should().Be("aptitud.pdf");
        var dialogo = cut.Find("[role=dialog]").TextContent;
        dialogo.Should().Contain("Pendiente de validar");
        dialogo.Should().Contain("Lo validará el Gestor CAE de Logística Delta Sur S.A.");
        dialogo.Should().Contain("Siguiente pendiente: Formación en altura");
        cut.Find("[role=dialog]").QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().Contain(["Más tarde", "Continuar"]);
    }

    [Fact]
    public void SubirDocumento_un_informe_con_datos_de_salud_no_se_acepta_y_dice_que_no_se_ha_guardado()
    {
        var subida = new SubidaDoble(true, new RespuestaSubidaTec(ResultadoSubidaTec.NoAceptadoPorDatosDeSalud));
        Services.AddScoped<IRequerimientosTenantTec>(_ => new RequerimientosDoble(true, Datos()));
        Services.AddScoped<ISubidaDocumentoTenantTec>(_ => subida);
        var cut = Render<Requerimientos>(p => p.Add(x => x.RequerimientoId, PendienteId));
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("informe", "informe.pdf"));

        BotonDelDialogo(cut, "Subir").Click();

        var dialogo = cut.Find("[role=dialog]").TextContent;
        dialogo.Should().Contain("No aceptado: contiene datos de salud");
        dialogo.Should().Contain("informe.pdf no se ha guardado.");
        // El rechazo sale por el aviso del kit (MensajeError), no por un role="alert" escrito a mano en el cuerpo.
        cut.Find("[role=dialog] [role=alert]").TextContent.Should().Contain("informe.pdf no se ha guardado.");
    }

    // ---- Tarjetas 26 y 5 · Quién lee mis documentos ---------------------------------------------------------------------

    private sealed class QuienLeeDoble(PanoramaLecturaTec? panorama) : IQuienLeeTenantTec
    {
        public bool Disponible => true;

        public List<Guid> Revocados { get; } = [];

        public bool Concedida { get; private set; }

        public Task<PanoramaLecturaTec?> ObtenerAsync(CancellationToken ct = default) => Task.FromResult(panorama);

        public Task<IReadOnlyList<RegistroLecturaTec>> RegistroAsync(Guid clienteEmpresarialId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RegistroLecturaTec>>(
                [new RegistroLecturaTec(new DateTimeOffset(2026, 11, 12, 9, 30, 0, TimeSpan.Zero), "Alta en la Seguridad Social", "Luis Gómez Ara", "Logística Delta Sur S.A.")]);

        public Task<ResultadoAccionTec> ConcederAutorizacionUnicaAsync(CancellationToken ct = default)
        {
            Concedida = true;
            return Task.FromResult(ResultadoAccionTec.Hecho);
        }

        public Task<ResultadoAccionTec> AutorizarAsync(Guid clienteEmpresarialId, CancellationToken ct = default) => Task.FromResult(ResultadoAccionTec.Hecho);

        public Task<ResultadoAccionTec> RevocarAsync(Guid clienteEmpresarialId, CancellationToken ct = default)
        {
            Revocados.Add(clienteEmpresarialId);
            return Task.FromResult(ResultadoAccionTec.Hecho);
        }
    }

    private static PanoramaLecturaTec Panorama(bool concedida = true) => new(
        new AutorizacionUnicaTec(concedida, concedida ? new DateOnly(2026, 11, 10) : null, concedida ? "Javier Lacasa" : null),
        [
            new LecturaClienteTec(OtroClienteEmpresarialId, "Logística Delta Sur S.A.", 2, ["Planta Zaragoza Norte", "Almacén Plaza"], 24, 6,
                new DateTimeOffset(2026, 11, 12, 9, 30, 0, TimeSpan.Zero), null),
            new LecturaClienteTec(Guid.NewGuid(), "Frío Industrial Cierzo S.L.", 1, ["Planta Cierzo"], 3, 1, null, new DateOnly(2026, 10, 1)),
        ]);

    [Fact]
    public void AddInboundTenantTec_registra_los_cuatro_servicios_sin_backend_que_las_paginas_inyectan()
    {
        var proveedor = InboundTenantTecServiceCollectionExtensions.AddInboundTenantTec(new ServiceCollection()).BuildServiceProvider();

        proveedor.GetRequiredService<IRequerimientosTenantTec>().Should().NotBeNull();
        proveedor.GetRequiredService<ISubidaDocumentoTenantTec>().Disponible.Should().BeFalse();
        proveedor.GetRequiredService<IQuienLeeTenantTec>().Should().NotBeNull();
        proveedor.GetRequiredService<IAvisoContratoTenantTec>().Should().NotBeNull();
    }

    [Fact]
    public void QuienLee_sin_backend_avisa_de_que_todavia_no_se_puede_consultar_y_no_inventa_una_autorizacion()
    {
        UsarSinBackend();

        var cut = Render<QuienLeeMisDocumentos>();

        cut.Markup.Should().Contain("Todavía no se puede consultar quién lee tus documentos");
        cut.Markup.Should().NotContain("Concedida");
        cut.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public void QuienLee_muestra_la_autorizacion_unica_concedida_y_a_cada_Cliente_empresarial_con_su_estado_y_su_accion()
    {
        Services.AddScoped<IQuienLeeTenantTec>(_ => new QuienLeeDoble(Panorama()));

        var cut = Render<QuienLeeMisDocumentos>();

        cut.Find("h1").TextContent.Should().Contain("Quién lee mis documentos");
        cut.Markup.Should().Contain("Mi documentación");
        cut.Markup.Should().Contain("Autorización única de reutilización");
        cut.Markup.Should().Contain("Concedida");
        cut.Markup.Should().Contain("El 10/11/2026, por Javier Lacasa");
        cut.Markup.Should().Contain("Pendiente de validar");
        var filas = cut.FindAll("tbody tr");
        filas.Should().HaveCount(2);
        var lee = filas.Single(f => f.TextContent.Contains("Logística Delta Sur S.A."));
        lee.TextContent.Should().Contain("2 contratos activos");
        lee.TextContent.Should().Contain("Documentos de la Empresa, de 24 Trabajadores y de 6 vehículos");
        lee.TextContent.Should().Contain("Lee");
        lee.QuerySelectorAll("button").Should().Contain(b => b.TextContent.Trim() == "Revocar");
        var revocado = filas.Single(f => f.TextContent.Contains("Frío Industrial Cierzo S.L."));
        revocado.TextContent.Should().Contain("Revocado el 01/10/2026");
        revocado.TextContent.Should().Contain("Leía: Documentos de la Empresa, de 3 Trabajadores y de 1 vehículos");
        revocado.QuerySelectorAll("button").Should().Contain(b => b.TextContent.Trim() == "Autorizar");
    }

    [Fact]
    public void QuienLee_revocar_pide_confirmacion_con_sus_consecuencias_y_revoca_al_Cliente_empresarial_correcto()
    {
        var doble = new QuienLeeDoble(Panorama());
        Services.AddScoped<IQuienLeeTenantTec>(_ => doble);
        var cut = Render<QuienLeeMisDocumentos>();

        cut.FindAll("tbody tr").Single(f => f.TextContent.Contains("Logística Delta Sur S.A.")).QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "Revocar").Click();

        var dialogo = cut.Find("[role=dialog], [role=alertdialog]").TextContent;
        dialogo.Should().Contain("Revocar la lectura de Logística Delta Sur S.A.");
        dialogo.Should().Contain("Lo que ya validó se conserva como prueba.");
        dialogo.Should().Contain("Puedes volver a autorizarlo cuando quieras.");
        doble.Revocados.Should().BeEmpty();

        cut.Find("[role=dialog] .boton-destructivo, [role=alertdialog] .boton-destructivo").Click();

        doble.Revocados.Should().Equal(OtroClienteEmpresarialId);
    }

    [Fact]
    public void QuienLee_sin_autorizacion_concedida_ofrece_Autorizar_y_el_dialogo_exige_marcar_la_casilla()
    {
        var doble = new QuienLeeDoble(Panorama(concedida: false));
        Services.AddScoped<IQuienLeeTenantTec>(_ => doble);
        var cut = Render<QuienLeeMisDocumentos>();
        cut.Markup.Should().Contain("Sin conceder");

        cut.Find(".ql-autorizacion button").Click();

        var dialogo = cut.Find("[role=dialog]");
        dialogo.TextContent.Should().Contain("Una sola vez para toda tu Empresa");
        dialogo.TextContent.Should().Contain("Qué ocurre");
        dialogo.TextContent.Should().Contain("Qué no ocurre");
        dialogo.TextContent.Should().Contain("Quién leerá tus documentos al confirmar");
        dialogo.TextContent.Should().Contain("Clientes futuros");
        // «Quién leerá al confirmar» solo nombra a los Clientes que hoy leen: el revocado no aparece como futuro lector.
        dialogo.TextContent.Should().Contain("Logística Delta Sur S.A.");
        dialogo.TextContent.Should().NotContain("Frío Industrial Cierzo S.L.");
        var autorizar = dialogo.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Autorizar");
        autorizar.HasAttribute("disabled").Should().BeTrue();

        cut.Find("[role=dialog] input[type=checkbox]").Change(true);
        cut.Find("[role=dialog]").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Autorizar").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[role=dialog]").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Autorizar").Click();

        doble.Concedida.Should().BeTrue();
    }

    [Fact]
    public void QuienLee_el_registro_de_lecturas_nombra_al_Cliente_empresarial_y_ninguna_persona_de_su_organizacion()
    {
        Services.AddScoped<IQuienLeeTenantTec>(_ => new QuienLeeDoble(Panorama()));
        var cut = Render<QuienLeeMisDocumentos>();

        cut.FindAll("button.req-nombre").First(b => b.TextContent == "Logística Delta Sur S.A.").Click();

        var registro = cut.Find("[role=dialog]").TextContent;
        registro.Should().Contain("Registro de lecturas");
        registro.Should().Contain("Alta en la Seguridad Social");
        registro.Should().Contain("1 lecturas");
        registro.Should().Contain("sin nombres de personas de su organización");
    }

    // ---- Tarjeta 4 · Aviso de contrato nuevo ---------------------------------------------------------------------------

    private sealed class AvisosDoble : IAvisoContratoTenantTec
    {
        public bool Disponible => true;

        public List<string> Llamadas { get; } = [];

        public Task<IReadOnlyList<AvisoContratoTec>> PendientesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AvisoContratoTec>>([]);

        public Task<ResultadoAccionTec> ConfirmarAsync(Guid clienteEmpresarialId, CancellationToken ct = default)
        {
            Llamadas.Add($"confirmar:{clienteEmpresarialId}");
            return Task.FromResult(ResultadoAccionTec.Hecho);
        }

        public Task<ResultadoAccionTec> RechazarAsync(Guid clienteEmpresarialId, CancellationToken ct = default)
        {
            Llamadas.Add($"rechazar:{clienteEmpresarialId}");
            return Task.FromResult(ResultadoAccionTec.Hecho);
        }
    }

    private static AvisoContratoTec Aviso(bool nuevo) => new(
        OtroClienteEmpresarialId, "Logística Delta Sur S.A.", "LD", new DateOnly(2026, 11, 13), "Logística Delta Sur S.A.", nuevo, 2, "Recurrente",
        "01/12/2026 – 30/11/2027", "Carpintería metálica, Instalaciones frigoríficas", "Trabajadores, vehículos y maquinaria",
        [
            new CentroAvisoTec("Planta Zaragoza Norte", "Polígono Malpica, Zaragoza", 12, 17, ["Trabajos en caliente", "Trabajos en altura"]),
            new CentroAvisoTec("Almacén Plaza", "Plataforma Logística Plaza, Zaragoza", 12, 16, ["Convivencia con carretillas"]),
        ],
        5, 7);

    [Fact]
    public void AvisoContratoNuevo_de_un_Cliente_empresarial_nuevo_ofrece_Rechazar_Mas_tarde_y_Confirmar_y_Confirmar_llama_al_servicio()
    {
        var avisos = new AvisosDoble();
        Services.AddScoped<IAvisoContratoTenantTec>(_ => avisos);
        var respuestas = new List<bool>();
        var cut = Render<AvisoContratoNuevo>(p => p
            .Add(x => x.Aviso, Aviso(nuevo: true))
            .Add(x => x.Visible, true)
            .Add(x => x.OnRespondido, EventCallback.Factory.Create<bool>(this, respuestas.Add)));

        var texto = cut.Find("[role=dialog]").TextContent;
        texto.Should().Contain("Logística Delta Sur S.A. te ha dado de alta 2 contratos de acceso");
        texto.Should().Contain("Desde tu última sesión");
        texto.Should().Contain("Pendiente de confirmar");
        texto.Should().Contain("Confirma que trabajas para él");
        texto.Should().Contain("Planta Zaragoza Norte");
        texto.Should().Contain("Ya tienes 5 de 7 documentos de la Empresa que pide.");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain(["Rechazar", "Más tarde", "Confirmar"]);

        Boton(cut, "Confirmar").Click();

        avisos.Llamadas.Should().Equal($"confirmar:{OtroClienteEmpresarialId}");
        respuestas.Should().Equal(true);
    }

    [Fact]
    public void AvisoContratoNuevo_Rechazar_llama_al_servicio_y_de_un_Cliente_empresarial_conocido_no_se_ofrece_Rechazar_ni_la_nota()
    {
        var avisos = new AvisosDoble();
        Services.AddScoped<IAvisoContratoTenantTec>(_ => avisos);
        var nuevo = Render<AvisoContratoNuevo>(p => p.Add(x => x.Aviso, Aviso(nuevo: true)).Add(x => x.Visible, true));

        Boton(nuevo, "Rechazar").Click();

        avisos.Llamadas.Should().Equal($"rechazar:{OtroClienteEmpresarialId}");

        var conocido = Render<AvisoContratoNuevo>(p => p.Add(x => x.Aviso, Aviso(nuevo: false)).Add(x => x.Visible, true));
        var botones = conocido.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
        botones.Should().Contain(["Más tarde", "Continuar"]);
        botones.Should().NotContain("Rechazar");
        conocido.Markup.Should().Contain("Activos");
        conocido.Markup.Should().NotContain("Confirma que trabajas para él");
        conocido.Markup.Should().Contain("Ya le hemos aportado 5 de 7 documentos");
    }

    [Fact]
    public void AvisoContratoNuevo_sin_backend_no_inventa_la_confirmacion_y_avisa_del_error()
    {
        UsarSinBackend();
        var cut = Render<AvisoContratoNuevo>(p => p.Add(x => x.Aviso, Aviso(nuevo: true)).Add(x => x.Visible, true));

        Boton(cut, "Confirmar").Click();

        cut.Markup.Should().Contain("No se ha podido registrar tu respuesta.");
    }
}
