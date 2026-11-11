using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionRechazada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Gestiones.Queries.GestionesAutomaticas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Gestiones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantalla de las Gestiones automáticas (Gestion Pasos TALVEG.dc.html, especificación del 23/10 D-1 a D-20): listado
/// y detalle con sus pasos. Cada elemento, estado y rol de la maqueta tiene su aserción. Todo sale de las consultas
/// ObtenerGestionesAutomaticasQuery y ObtenerGestionAutomaticaQuery; los comandos son los de «Gestionar» de Centro 360.
/// </summary>
public partial class GestionesAutomaticasPantallaTests : BunitContext
{
    private static readonly Guid GestionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DocumentoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TrabajadorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid EmpresaId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public GestionesAutomaticasPantallaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    // ---------- datos ----------

    private static PasoGestionAutomaticaDto Paso(
        int n, string plataforma, string centro, string? cliente, EstadoAcreditacion estado, string? url = "https://nalanda.example/",
        bool usuario = true, string? notas = null, DateOnly? hasta = null) =>
        new(Guid.Parse($"aaaaaaaa-0000-0000-0000-{n:D12}"), estado, GestionAutomatica.PasoHecho(estado), hasta,
            Guid.Parse($"bbbbbbbb-0000-0000-0000-{n:D12}"), plataforma, url, usuario, notas,
            Guid.Parse($"cccccccc-0000-0000-0000-{n:D12}"), centro, cliente);

    private static List<PasoGestionAutomaticaDto> SeisPasosNuevos() =>
    [
        Paso(1, "Nalanda", "Planta Norte", "Industrias del Moncayo S.A.", EstadoAcreditacion.PendienteDeSubir, notas: "Subir en Documentación > Trabajadores > Aptitud."),
        Paso(2, "Nalanda", "Almacén Sur", "Industrias del Moncayo S.A.", EstadoAcreditacion.PendienteDeSubir),
        Paso(3, "Nalanda", "Nave Ribera", "Logística del Huerva S.A.", EstadoAcreditacion.PendienteDeSubir),
        Paso(4, "Dokify", "Planta Norte", "Industrias del Moncayo S.A.", EstadoAcreditacion.PendienteDeSubir),
        Paso(5, "Dokify", "Parque Eólico Cierzo", "Energías del Cierzo S.A.", EstadoAcreditacion.PendienteDeSubir),
        Paso(6, "CTAIMA", "Parque Eólico Cierzo", "Energías del Cierzo S.A.", EstadoAcreditacion.PendienteDeSubir)
    ];

    private static GestionAutomaticaDetalleDto Detalle(
        List<PasoGestionAutomaticaDto> pasos, bool sustituida = false, bool vigente = true, bool conArchivo = true,
        OrigenGestionAutomatica origen = OrigenGestionAutomatica.RenovacionDocumento, Guid? sustituidaPor = null)
    {
        var estados = pasos.Select(p => p.Estado).ToList();
        var resumen = new GestionAutomaticaResumenDto(
            GestionId, DocumentoId, origen, "Reconocimiento médico", "Laura Gil Pardo", EmpresaId, "Construcciones Ebro S.L.",
            new DateTime(2026, 10, 23, 7, 12, 0, DateTimeKind.Utc), GestionAutomatica.ResolverEstado(sustituida, documentoSustituido: sustituida, estados),
            AvanceGestionAutomatica.De(estados), pasos.Select(p => p.Plataforma).Distinct().ToList(), estados.Contains(EstadoAcreditacion.Rechazada));
        return new GestionAutomaticaDetalleDto(
            resumen, null, sustituida ? new DateTime(2026, 10, 23, 7, 12, 0, DateTimeKind.Utc) : null, sustituidaPor,
            new DocumentoDeGestionAutomaticaDto(
                DocumentoId, TrabajadorId, null, "73215489K", new DateOnly(2026, 10, 14), new DateOnly(2027, 10, 14),
                EstadoVigenciaDocumento.VenceEnFecha, vigente, vigente, conArchivo),
            pasos);
    }

    private static GestionAutomaticaResumenDto Fila(Guid id, EstadoGestionAutomatica estado, bool rechazo = false, int hechos = 2, int validados = 1, int pasos = 6) =>
        new(id, DocumentoId, OrigenGestionAutomatica.RenovacionDocumento, "Reconocimiento médico", "Laura Gil Pardo", EmpresaId,
            "Construcciones Ebro S.L.", new DateTime(2026, 10, 23, 7, 12, 0, DateTimeKind.Utc), estado,
            new AvanceGestionAutomatica(pasos, hechos, validados, hechos - validados), ["Nalanda", "Dokify"], rechazo);

    // ---------- montaje ----------

    private MediatorFalso Montar(string rol = Roles.GestorCae)
    {
        this.ConRolDeEscritura(rol);
        var mediador = new MediatorFalso();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        return mediador;
    }

    private IRenderedComponent<GestionAutomaticaDetalle> Detallar(MediatorFalso m, GestionAutomaticaDetalleDto? detalle)
    {
        m.Detalle = detalle;
        var cut = Render<GestionAutomaticaDetalle>(p => p.Add(x => x.Id, GestionId));
        cut.WaitForAssertion(() => cut.FindAll("[data-estado='cargando']").Should().BeEmpty());
        return cut;
    }

    // ---------- listado ----------

    [Fact]
    public void Listado_enseña_una_fila_por_Gestion_con_titulo_empresa_origen_avance_plataformas_estado_y_accion()
    {
        var m = Montar();
        var enCurso = Guid.NewGuid();
        var pendiente = Guid.NewGuid();
        m.Listado = new ListadoGestionesAutomaticasDto(
            [Fila(enCurso, EstadoGestionAutomatica.EnCurso, rechazo: true), Fila(pendiente, EstadoGestionAutomatica.PendienteDeValidar, hechos: 6, validados: 4)],
            new Dictionary<EstadoGestionAutomatica, int> { [EstadoGestionAutomatica.EnCurso] = 1, [EstadoGestionAutomatica.PendienteDeValidar] = 1, [EstadoGestionAutomatica.Cerrada] = 3, [EstadoGestionAutomatica.Sustituida] = 2 });

        var cut = Render<GestionesAutomaticas>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(2));

        var fila = cut.Find($"tr[data-gestion-id='{enCurso}']");
        fila.QuerySelector(".ga-titulo-fila")!.TextContent.Should().Be("Actualizar Reconocimiento médico de Laura Gil Pardo");
        fila.QuerySelector(".ga-titulo-fila")!.GetAttribute("href").Should().Be($"gestiones/{enCurso}");
        fila.QuerySelector(".ga-empresa")!.TextContent.Should().Be("Construcciones Ebro S.L.");
        fila.QuerySelector("[data-celda='origen']")!.TextContent.Should().Be("Renovación");
        fila.QuerySelector("[data-celda='avance']")!.TextContent.Should().Contain("2/6");
        fila.QuerySelector("[data-barra-avance]")!.GetAttribute("aria-valuenow").Should().Be("2");
        fila.QuerySelector("[data-rechazo]").Should().NotBeNull("la Gestión con un paso rechazado lo dice");
        fila.QuerySelectorAll("[data-celda='plataformas'] .ga-chip").Select(c => c.TextContent).Should().Equal("Nalanda", "Dokify");
        fila.QuerySelector("[data-celda='estado']")!.TextContent.Should().Be("En curso");
        fila.QuerySelector("[data-accion-fila]")!.TextContent.Trim().Should().Be("Continuar");

        cut.Find($"tr[data-gestion-id='{pendiente}'] [data-celda='estado']").TextContent.Should().Be("Pendiente de validar");
        cut.FindAll($"tr[data-gestion-id='{pendiente}'] [data-rechazo]").Should().BeEmpty();
    }

    [Fact]
    public void Listado_no_ofrece_nueva_gestion_ni_reasignar_ni_marcar_como_resueltas()
    {
        var m = Montar();
        m.Listado = new ListadoGestionesAutomaticasDto([Fila(Guid.NewGuid(), EstadoGestionAutomatica.EnCurso)], Recuentos());

        var cut = Render<GestionesAutomaticas>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().NotBeEmpty());

        cut.Markup.Should().NotContain("Nueva gestión").And.NotContain("Reasignar").And.NotContain("Marcar como resueltas");
    }

    [Fact]
    public void Listado_filtra_por_estado_con_en_curso_por_defecto_y_cada_boton_dice_cuantas_hay()
    {
        var m = Montar();
        m.Listado = new ListadoGestionesAutomaticasDto([], new Dictionary<EstadoGestionAutomatica, int>
        { [EstadoGestionAutomatica.EnCurso] = 5, [EstadoGestionAutomatica.PendienteDeValidar] = 2, [EstadoGestionAutomatica.Cerrada] = 7, [EstadoGestionAutomatica.Sustituida] = 1 });

        var cut = Render<GestionesAutomaticas>();
        cut.WaitForAssertion(() => cut.FindAll("[data-filtro-estado]").Count.Should().Be(4));

        m.EstadoPedido.Should().Be(EstadoGestionAutomatica.EnCurso, "por defecto, En curso (§ 5.2)");
        cut.FindAll("[data-filtro-estado]").Select(b => b.TextContent.Replace("\n", " ").Trim())
            .Should().Equal("En curso 5", "Pendiente de validar 2", "Cerrada 7", "Sustituida 1");
        cut.Find("[data-filtro-estado='EnCurso']").GetAttribute("aria-pressed").Should().Be("true");

        cut.Find("[data-filtro-estado='Cerrada']").Click();

        m.EstadoPedido.Should().Be(EstadoGestionAutomatica.Cerrada);
        cut.Find("[data-filtro-estado='Cerrada']").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-filtro-estado='EnCurso']").GetAttribute("aria-pressed").Should().Be("false");
    }

    [Fact]
    public void Listado_en_las_cerradas_y_sustituidas_la_accion_es_Ver()
    {
        var m = Montar();
        var id = Guid.NewGuid();
        m.Listado = new ListadoGestionesAutomaticasDto([Fila(id, EstadoGestionAutomatica.Cerrada, hechos: 6, validados: 6)], Recuentos());

        var cut = Render<GestionesAutomaticas>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().NotBeEmpty());

        cut.Find("[data-accion-fila]").TextContent.Trim().Should().Be("Ver");
    }

    [Fact]
    public void Listado_alta_de_documento_dice_Subir_y_origen_Alta_de_documento()
    {
        var m = Montar();
        var fila = Fila(Guid.NewGuid(), EstadoGestionAutomatica.EnCurso) with { Origen = OrigenGestionAutomatica.AltaDocumento };
        m.Listado = new ListadoGestionesAutomaticasDto([fila], Recuentos());

        var cut = Render<GestionesAutomaticas>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().NotBeEmpty());

        cut.Find(".ga-titulo-fila").TextContent.Should().Be("Subir Reconocimiento médico de Laura Gil Pardo");
        cut.Find("[data-celda='origen']").TextContent.Should().Be("Alta de documento");
    }

    [Fact]
    public void Listado_vacio_dice_que_no_hay_Gestiones_con_ese_estado()
    {
        var m = Montar();
        m.Listado = new ListadoGestionesAutomaticasDto([], Recuentos());

        var cut = Render<GestionesAutomaticas>();
        cut.WaitForAssertion(() => cut.Find("[data-estado='vacio']").TextContent.Should().Contain("Sin Gestiones"));
    }

    [Fact]
    public void Listado_si_falla_la_consulta_enseña_el_error_con_reintento_y_reintentar_recarga()
    {
        var m = Montar();
        m.FallaListado = true;

        var cut = Render<GestionesAutomaticas>();
        cut.WaitForAssertion(() => cut.Find("[data-estado='error']").TextContent.Should().Contain("No se han podido cargar las Gestiones"));

        m.FallaListado = false;
        m.Listado = new ListadoGestionesAutomaticasDto([Fila(Guid.NewGuid(), EstadoGestionAutomatica.EnCurso)], Recuentos());
        cut.Find("[data-estado='error'] button").Click();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.Should().Be(1));
    }

    private static Dictionary<EstadoGestionAutomatica, int> Recuentos() =>
        Enum.GetValues<EstadoGestionAutomatica>().ToDictionary(e => e, _ => 1);

    // ---------- detalle: cabecera ----------

    [Fact]
    public void Detalle_cabecera_enseña_empresa_titulo_origen_estado_y_ver_documento()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.Find("[data-empresa-gestionada]").TextContent.Should().Be("Construcciones Ebro S.L.");
        cut.Find(".gp-resumen h1").TextContent.Should().Be("Actualizar Reconocimiento médico de Laura Gil Pardo");
        cut.Find("[data-origen]").TextContent.Should().Be("Nació al renovar el documento el 23/10/2026 a las 09:12.");
        cut.Find("[data-estado-gestion]").TextContent.Should().Be("En curso");
        cut.Find("[data-ver-documento]").GetAttribute("href").Should().Be($"documentos/{DocumentoId}");
        cut.Find(".gp-migas").TextContent.Should().Contain("Operación").And.Contain("Gestiones");
    }

    [Fact]
    public void Detalle_origen_de_alta_dice_que_nacio_al_subir()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos(), origen: OrigenGestionAutomatica.AltaDocumento));

        cut.Find(".gp-resumen h1").TextContent.Should().Be("Subir Reconocimiento médico de Laura Gil Pardo");
        cut.Find("[data-origen]").TextContent.Should().StartWith("Nació al subir el documento");
    }

    [Fact]
    public void Detalle_recien_nacida_barra_vacia_y_texto_0_de_6()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.Find("[data-barra-avance]").GetAttribute("aria-valuenow").Should().Be("0");
        cut.Find("[data-barra-avance]").GetAttribute("aria-valuemax").Should().Be("6");
        cut.Find("[data-avance-texto]").TextContent.Should().Be("0 de 6 pasos hechos · 0 validados, 0 pendientes de validar en la plataforma");
        cut.FindAll("[data-rechazados]").Should().BeEmpty();
    }

    [Fact]
    public void Detalle_a_mitad_barra_de_dos_tramos_y_texto_de_avance()
    {
        var pasos = SeisPasosNuevos();
        pasos[0] = pasos[0] with { Estado = EstadoAcreditacion.Aceptada, Hecho = true, FechaVencimientoEnPlataforma = new DateOnly(2027, 10, 14) };
        pasos[1] = pasos[1] with { Estado = EstadoAcreditacion.Subida, Hecho = true };
        pasos[2] = pasos[2] with { Estado = EstadoAcreditacion.Subida, Hecho = true };
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        var spans = cut.Find("[data-barra-avance]").QuerySelectorAll("span");
        spans[0].GetAttribute("style").Should().Contain("16.67%", "1 validado de 6");
        spans[1].GetAttribute("style").Should().Contain("33.33%", "2 subidos de 6");
        cut.Find("[data-avance-texto]").TextContent.Should().Be("3 de 6 pasos hechos · 1 validado, 2 pendientes de validar en la plataforma");
        cut.Find(".gp-avance-texto").TextContent.Should().Contain("Validado").And.Contain("Subido, pendiente de validar");
    }

    // ---------- detalle: lista de pasos ----------

    [Fact]
    public void Detalle_lista_de_pasos_agrupa_por_plataforma_con_centro_cliente_y_estado()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.FindAll(".gp-grupo").Select(g => g.GetAttribute("data-plataforma")).Should().Equal("Nalanda", "Dokify", "CTAIMA");
        var nalanda = cut.Find(".gp-grupo[data-plataforma='Nalanda']");
        nalanda.QuerySelector(".gp-grupo-hechos")!.TextContent.Should().Be("0 de 3 hechos");
        var primero = nalanda.QuerySelectorAll(".gp-paso")[0];
        primero.QuerySelector(".gp-paso-centro")!.TextContent.Should().Be("Planta Norte");
        primero.QuerySelector(".gp-paso-cliente")!.TextContent.Should().Be("Cliente: Industrias del Moncayo S.A.");
        primero.QuerySelector(".gp-paso-estado")!.TextContent.Should().Be("Pendiente de subir");
    }

    [Fact]
    public void Detalle_filtro_Por_hacer_y_Todos_con_sus_recuentos_y_oculta_los_hechos()
    {
        var pasos = SeisPasosNuevos();
        pasos[0] = pasos[0] with { Estado = EstadoAcreditacion.Aceptada, Hecho = true, FechaVencimientoEnPlataforma = new DateOnly(2027, 10, 14) };
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-filtro-pasos='por-hacer']").TextContent.Should().Be("Por hacer (5)");
        cut.Find("[data-filtro-pasos='todos']").TextContent.Should().Be("Todos (6)");
        cut.Find("[data-filtro-pasos='por-hacer']").GetAttribute("aria-pressed").Should().Be("true");
        cut.FindAll(".gp-paso").Count.Should().Be(5, "por defecto solo los por hacer");

        cut.Find("[data-filtro-pasos='todos']").Click();

        cut.FindAll(".gp-paso").Count.Should().Be(6);
        var hecho = cut.Find($"[data-paso='{pasos[0].AcreditacionId}']");
        hecho.QuerySelector(".gp-paso-estado")!.TextContent.Should().Be("Validado hasta 14/10/2027");
        hecho.QuerySelector(".gp-paso-hecho").Should().NotBeNull("el paso hecho va atenuado");
    }

    [Fact]
    public void Detalle_sin_pasos_por_hacer_el_filtro_lo_dice()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Aceptada, Hecho = true }).ToList();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-sin-por-hacer]").TextContent.Should().Be("No queda ningún paso por hacer. Pasa a «Todos» para revisarlos.");
    }

    [Fact]
    public void Detalle_abre_el_primer_paso_por_hacer_y_lo_marca_como_actual()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[0].AcreditacionId.ToString());
        cut.Find($"[data-paso='{pasos[0].AcreditacionId}']").GetAttribute("aria-current").Should().Be("step");
        cut.Find(".gp-paso-n").TextContent.Should().Be("Paso 1 de 6");
    }

    [Fact]
    public void Detalle_con_un_paso_rechazado_abre_ese_primero_y_la_cabecera_lo_cuenta()
    {
        var pasos = SeisPasosNuevos();
        pasos[4] = pasos[4] with { Estado = EstadoAcreditacion.Rechazada, Hecho = false };
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[4].AcreditacionId.ToString());
        cut.Find("[data-pastilla-paso]").TextContent.Should().Be("Rechazado");
        cut.Find("[data-rechazados]").TextContent.Should().Be("1 paso rechazado");
        cut.Find($"[data-paso='{pasos[4].AcreditacionId}'] .gp-paso-estado").TextContent.Should().Be("Rechazado");
    }

    [Fact]
    public void Detalle_clic_en_un_paso_lo_abre()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find($"[data-paso='{pasos[3].AcreditacionId}']").Click();

        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[3].AcreditacionId.ToString());
        cut.Find("[data-donde]").TextContent.Should().Contain("Dokify").And.Contain("Planta Norte");
    }

    // ---------- detalle: paso abierto ----------

    [Fact]
    public void Paso_abierto_enseña_donde_el_documento_y_las_notas_del_acceso()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.Find("[data-donde]").TextContent.Should().Be("Nalanda · Planta Norte · Cliente: Industrias del Moncayo S.A.");
        cut.Find(".gp-paso-cabecera h2").TextContent.Should().Be("Reconocimiento médico");
        var campos = cut.FindAll("[data-campo]").ToDictionary(c => c.GetAttribute("data-campo")!, c => c.TextContent);
        campos.Keys.Should().Equal("GpCampoTipo", "GpCampoTitular", "GpCampoDni", "GpCampoEmision", "GpCampoVencimiento", "GpCampoEstadoTalveg");
        cut.Find("[data-campo='GpCampoDni']").TextContent.Should().Contain("DNI del titular").And.Contain("73215489K");
        cut.Find("[data-campo='GpCampoEmision']").TextContent.Should().Contain("14/10/2026");
        cut.Find("[data-campo='GpCampoVencimiento']").TextContent.Should().Contain("14/10/2027");
        cut.Find("[data-campo='GpCampoEstadoTalveg']").TextContent.Should().Contain("Vigente hasta 14/10/2027");
        cut.Find("[data-descargar]").GetAttribute("href").Should().Be($"documentos/{DocumentoId}/archivo");
        cut.Find("[data-notas]").TextContent.Should().Contain("Subir en Documentación > Trabajadores > Aptitud.");
    }

    [Fact]
    public void Paso_abierto_sin_notas_no_pinta_el_bloque_y_sin_archivo_el_boton_de_descarga_esta_deshabilitado()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos(), conArchivo: false));
        cut.Find($"[data-paso='{SeisPasosNuevos()[1].AcreditacionId}']").Click();

        cut.FindAll("[data-notas]").Should().BeEmpty();
        cut.Find("[data-descargar]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-descargar]").GetAttribute("href").Should().BeNull();
    }

    [Fact]
    public void Entrar_en_la_plataforma_abre_la_direccion_y_ofrece_copiar_usuario_y_contrasena_con_escritura()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        var bloque = cut.Find("[data-entrar-plataforma]");
        bloque.TextContent.Should().Contain("Entrar en la plataforma").And.Contain("La misma sección de accesos de Centro 360");
        bloque.QuerySelector("a[href='https://nalanda.example/']").Should().NotBeNull("abre la dirección del acceso");
        bloque.TextContent.Should().Contain("Copiar usuario").And.Contain("Copiar contraseña");
        cut.FindAll("[data-sin-direccion]").Should().BeEmpty();
        cut.FindAll("[data-sin-credenciales]").Should().BeEmpty();
    }

    [Fact]
    public void Entrar_en_la_plataforma_sin_credenciales_dice_donde_anadirlas()
    {
        var pasos = SeisPasosNuevos();
        pasos[0] = pasos[0] with { TieneUsuario = false };
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-sin-credenciales]").TextContent.Should().Contain("Este acceso no tiene usuario guardado").And.Contain("Centro 360 › Accesos");
        cut.Find("[data-entrar-plataforma]").TextContent.Should().NotContain("Copiar usuario");
    }

    [Fact]
    public void Entrar_en_la_plataforma_sin_direccion_lo_dice_y_no_ofrece_abrir()
    {
        var pasos = SeisPasosNuevos();
        pasos[0] = pasos[0] with { UrlAcceso = null };
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-sin-direccion]").TextContent.Should().Be("Este acceso no tiene dirección guardada.");
        cut.FindAll("[data-entrar-plataforma] a[href^='http']").Should().BeEmpty();
    }

    [Fact]
    public void Rol_Consulta_puede_abrir_la_plataforma_pero_no_copia_ni_marca()
    {
        var m = Montar(Roles.Consulta);
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.Find("[data-entrar-plataforma] a[href='https://nalanda.example/']").Should().NotBeNull();
        cut.Find("[data-entrar-plataforma]").TextContent.Should().NotContain("Copiar usuario").And.NotContain("Copiar contraseña");
        cut.Find("[data-solo-consulta]").TextContent.Should().Contain("Con el rol Consulta puedes abrir la plataforma");
        cut.FindAll("[data-opciones]").Should().BeEmpty();
        cut.Find("[data-aviso='consulta']").TextContent.Should().Contain("no puedes marcar el estado de los pasos");
        cut.FindAll("[data-accion='guardar']").Should().BeEmpty();
        cut.Find("[data-accion='primario']").TextContent.Should().Be("Siguiente", "Consulta sigue navegando por los pasos");
    }

    // ---------- detalle: marcar un paso ----------

    [Fact]
    public void Las_cuatro_opciones_de_como_ha_quedado_con_sus_textos_y_la_cuarta_a_la_espera_de_comando()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.Find("[data-opciones] legend").TextContent.Should().Be("¿Cómo ha quedado?");
        var opciones = cut.FindAll("[data-opcion]");
        opciones.Select(o => o.GetAttribute("data-opcion")).Should().Equal("subido", "validado", "rechazado", "nopide");
        opciones[0].TextContent.Should().Contain("Subido, pendiente de validar").And.Contain("La plataforma aún no lo ha revisado.");
        opciones[1].TextContent.Should().Contain("Validado en la plataforma").And.Contain("La plataforma ya lo ha aceptado. Indica hasta cuándo vale allí.");
        opciones[2].TextContent.Should().Contain("Rechazado por la plataforma").And.Contain("Indica la causa y copia el motivo que da la plataforma.");
        opciones[3].TextContent.Should().Contain("Esta plataforma no lo pide");
        opciones[3].HasAttribute("disabled").Should().BeTrue("no existe aún el comando que la marca (D-20): no se ofrece algo que no se puede guardar");
        opciones.Take(3).Should().OnlyContain(o => !o.HasAttribute("disabled"));
    }

    [Fact]
    public void Documento_no_vigente_deshabilita_Subido_y_Validado_y_lo_avisa()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos(), vigente: false));

        cut.Find("[data-opcion='subido']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-opcion='validado']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-opcion='rechazado']").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-aviso='no-vigente']").TextContent.Should().Contain("Este documento no está vigente en TALVEG: renuévalo en la ficha de Laura Gil Pardo");
        cut.Find("[data-aviso='no-vigente'] a").GetAttribute("href").Should().Be($"trabajadores/{TrabajadorId}");
        cut.Find("[data-aviso-opciones]").TextContent.Should().Contain("«Subido» y «Validado» están desactivados");
        cut.Find("[data-campo='GpCampoEstadoTalveg']").TextContent.Should().Contain("No vigente");
    }

    [Fact]
    public void Sin_elegir_opcion_el_boton_primario_dice_Siguiente_y_abre_el_siguiente_por_hacer()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-accion='primario']").TextContent.Should().Be("Siguiente");
        cut.FindAll("[data-accion='guardar']").Should().BeEmpty();
        cut.Find("[data-accion='anterior']").HasAttribute("disabled").Should().BeTrue("es el primer paso");

        cut.Find("[data-accion='primario']").Click();

        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[1].AcreditacionId.ToString());
        cut.Find("[data-accion='anterior']").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-accion='anterior']").Click();
        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[0].AcreditacionId.ToString());
    }

    [Fact]
    public void Siguiente_se_salta_los_pasos_hechos()
    {
        var pasos = SeisPasosNuevos();
        pasos[1] = pasos[1] with { Estado = EstadoAcreditacion.Subida, Hecho = true };
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-accion='primario']").Click();

        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[2].AcreditacionId.ToString());
    }

    [Fact]
    public void Elegir_una_opcion_cambia_el_primario_a_Guardar_y_siguiente_y_enseña_Guardar_a_secas()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.Find("[data-opcion='subido']").Click();

        cut.Find("[data-opcion='subido']").GetAttribute("aria-checked").Should().Be("true");
        cut.Find("[data-accion='primario']").TextContent.Should().Be("Guardar y siguiente");
        cut.Find("[data-accion='guardar']").TextContent.Should().Be("Guardar");
    }

    [Fact]
    public void Guardar_Subido_envia_MarcarAcreditacionSubida_enseña_el_aviso_y_abre_el_siguiente()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-opcion='subido']").Click();
        cut.Find("[data-accion='primario']").Click();

        cut.WaitForAssertion(() => m.Enviados.OfType<MarcarAcreditacionSubidaCommand>().Should().ContainSingle()
            .Which.AcreditacionId.Should().Be(pasos[0].AcreditacionId));
        var toast = Toasts.Mensajes.Should().ContainSingle().Subject;
        toast.Mensaje.Should().Be("Paso guardado. Centro 360 ya muestra el estado nuevo.");
        toast.Tono.Should().Be(TonoToast.Exito);
        toast.OnAccion.Should().BeNull("Deshacer solo existe tras Validado: Subido no devuelve qué restaurar");
        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[1].AcreditacionId.ToString());
    }

    [Fact]
    public void Guardar_a_secas_se_queda_en_el_paso()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-opcion='subido']").Click();
        cut.Find("[data-accion='guardar']").Click();

        cut.WaitForAssertion(() => m.Enviados.OfType<MarcarAcreditacionSubidaCommand>().Should().ContainSingle());
        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[0].AcreditacionId.ToString());
    }

    [Fact]
    public void Validado_exige_fecha_o_No_lo_se_y_envia_la_vigencia_elegida()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-opcion='validado']").Click();
        cut.Find("[data-campos-opcion='validado']").TextContent.Should().Contain("Válido en la plataforma hasta").And.Contain("No lo sé");
        cut.Find("[data-accion='primario']").HasAttribute("disabled").Should().BeTrue("sin fecha ni «No lo sé» no se guarda");

        cut.Find("[data-campos-opcion='validado'] input[type='date']").Input("2027-10-14");
        cut.Find("[data-accion='primario']").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-accion='guardar']").Click();

        cut.WaitForAssertion(() => m.Enviados.OfType<MarcarAcreditacionAceptadaCommand>().Should().ContainSingle()
            .Which.Vigencia.Should().Be(VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 10, 14))));
        Toasts.Mensajes.Should().ContainSingle().Which.TextoAccion.Should().Be("Deshacer");
    }

    [Fact]
    public void Validado_con_No_lo_se_guarda_la_vigencia_sin_confirmar()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.Find("[data-opcion='validado']").Click();
        cut.Find("[data-campos-opcion='validado'] input[type='checkbox']").Change(true);
        cut.Find("[data-campos-opcion='validado'] input[type='date']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-accion='guardar']").Click();

        cut.WaitForAssertion(() => m.Enviados.OfType<MarcarAcreditacionAceptadaCommand>().Should().ContainSingle()
            .Which.Vigencia.Should().Be(VigenciaEnPlataforma.SinConfirmar));
    }

    [Fact]
    public void Rechazado_exige_causa_y_motivo_y_envia_ambos()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-opcion='rechazado']").Click();
        var campos = cut.Find("[data-campos-opcion='rechazado']");
        campos.QuerySelectorAll("option").Select(o => o.TextContent).Should().Equal(
            "Elige una causa", "Documento ilegible", "Documento caducado", "No corresponde al puesto", "Datos incorrectos", "Otra");
        campos.TextContent.Should().Contain("Motivo que da la plataforma").And.Contain("(obligatorio)");
        cut.Find("[data-accion='primario']").HasAttribute("disabled").Should().BeTrue();

        campos.QuerySelector("select")!.Change(nameof(CausaRechazoAcreditacion.Ilegible));
        cut.Find("[data-accion='primario']").HasAttribute("disabled").Should().BeTrue("falta el motivo");
        cut.Find("[data-campos-opcion='rechazado'] textarea").Input("No se lee la fecha de emisión");
        cut.Find("[data-accion='guardar']").Click();

        cut.WaitForAssertion(() => m.Enviados.OfType<MarcarAcreditacionRechazadaCommand>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new MarcarAcreditacionRechazadaCommand(
                pasos[0].AcreditacionId, CausaRechazoAcreditacion.Ilegible, "No se lee la fecha de emisión")));
    }

    [Fact]
    public void Si_el_comando_falla_el_aviso_dice_que_no_se_ha_guardado_y_conserva_lo_escrito()
    {
        var m = Montar();
        m.FalloAlMarcar = "Acreditación fuera de tu cartera";
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.Find("[data-opcion='subido']").Click();
        cut.Find("[data-accion='guardar']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-aviso='error-guardado']").TextContent
            .Should().Contain("No se ha guardado el paso.").And.Contain("Acreditación fuera de tu cartera"));
        cut.Find("[data-opcion='subido']").GetAttribute("aria-checked").Should().Be("true", "lo elegido sigue ahí");
        cut.Find("[data-aviso='error-guardado'] button").TextContent.Should().Be("Reintentar");
    }

    [Fact]
    public void Deshacer_tras_Validado_restaura_la_anotacion_con_la_version_devuelta()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-opcion='validado']").Click();
        cut.Find("[data-campos-opcion='validado'] input[type='checkbox']").Change(true);
        cut.Find("[data-accion='guardar']").Click();
        cut.WaitForAssertion(() => Toasts.Mensajes.Should().ContainSingle().Which.OnAccion.Should().NotBeNull());

        PulsarDeshacer(cut);

        cut.WaitForAssertion(() => m.Enviados.OfType<CaeManager.Application.Documentos.Commands.RestaurarAnotacionAcreditacion.RestaurarAnotacionAcreditacionCommand>()
            .Should().ContainSingle().Which.Should().Match<CaeManager.Application.Documentos.Commands.RestaurarAnotacionAcreditacion.RestaurarAnotacionAcreditacionCommand>(
                c => c.AcreditacionId == pasos[0].AcreditacionId && c.EstadoPrevio == EstadoAcreditacion.PendienteDeSubir && c.VersionEsperada == MediatorFalso.Version));
        Toasts.Mensajes.Should().BeEmpty("Deshacer descarta el aviso");
    }

    [Fact]
    public void En_el_ultimo_paso_por_hacer_el_boton_se_convierte_en_Siguiente_Gestion_y_navega_a_la_siguiente_en_curso()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Aceptada, Hecho = true }).ToList();
        pasos[5] = pasos[5] with { Estado = EstadoAcreditacion.PendienteDeSubir, Hecho = false };
        var m = Montar();
        var otra = Guid.NewGuid();
        m.Listado = new ListadoGestionesAutomaticasDto([Fila(GestionId, EstadoGestionAutomatica.EnCurso), Fila(otra, EstadoGestionAutomatica.EnCurso)], Recuentos());
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-accion='primario']").TextContent.Should().Be("Siguiente Gestión");
        cut.Find("[data-opcion='subido']").Click();
        cut.Find("[data-accion='primario']").TextContent.Should().Be("Guardar · Siguiente Gestión");
        cut.Find("[data-accion='primario']").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"gestiones/{otra}"));
    }

    [Fact]
    public void Sin_otra_Gestion_en_curso_Siguiente_Gestion_vuelve_a_Mi_trabajo()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Aceptada, Hecho = true }).ToList();
        pasos[5] = pasos[5] with { Estado = EstadoAcreditacion.PendienteDeSubir, Hecho = false };
        var m = Montar();
        m.Listado = new ListadoGestionesAutomaticasDto([Fila(GestionId, EstadoGestionAutomatica.EnCurso)], Recuentos());
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-accion='primario']").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("mi-trabajo"));
    }

    // ---------- detalle: estados de la Gestión ----------

    [Fact]
    public void Terminada_con_pasos_subidos_enseña_la_tarjeta_de_exito_y_la_Gestion_sigue_abierta()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Aceptada, Hecho = true }).ToList();
        pasos[2] = pasos[2] with { Estado = EstadoAcreditacion.Subida };
        pasos[3] = pasos[3] with { Estado = EstadoAcreditacion.Subida };
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-estado-gestion]").TextContent.Should().Be("Pendiente de validar");
        var fin = cut.Find("[data-aviso='fin']");
        fin.TextContent.Should().Contain("Has hecho todo lo que depende de ti").And.Contain("2 pasos están pendientes de que la plataforma los valide");
        fin.TextContent.Should().Contain("Siguiente Gestión").And.Contain("Volver a Mi trabajo");
        fin.QuerySelector("a[href='mi-trabajo']").Should().NotBeNull();
    }

    [Fact]
    public void Cerrada_enseña_Gestion_cerrada_y_todos_los_pasos_validados()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Aceptada, Hecho = true }).ToList();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-estado-gestion]").TextContent.Should().Be("Cerrada");
        cut.Find("[data-aviso='fin']").TextContent.Should().Contain("Gestión cerrada").And.Contain("Todos los pasos están validados en su plataforma.");
    }

    [Fact]
    public void Sustituida_enseña_el_aviso_con_enlace_a_la_nueva_y_no_deja_marcar()
    {
        var nueva = Guid.NewGuid();
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos(), sustituida: true, sustituidaPor: nueva));

        cut.Find("[data-estado-gestion]").TextContent.Should().Be("Sustituida");
        var aviso = cut.Find("[data-aviso='sustituida']");
        aviso.TextContent.Should().Contain("Esta Gestión se cerró porque el documento se renovó el 23/10/2026. Continúa en la Gestión nueva.");
        aviso.QuerySelector("a")!.GetAttribute("href").Should().Be($"gestiones/{nueva}");
        aviso.QuerySelector("a")!.TextContent.Should().Be("Ir a la Gestión nueva");
        cut.FindAll("[data-opciones]").Should().BeEmpty("una Gestión sustituida es de consulta");
    }

    [Fact]
    public void Rechazar_el_ultimo_paso_con_Guardar_y_siguiente_reabre_y_se_queda_en_la_Gestion()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Subida, Hecho = true }).ToList();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));
        m.AlMarcar = () => m.Detalle = Detalle(pasos.Select((p, i) => i == 0 ? p with { Estado = EstadoAcreditacion.Rechazada, Hecho = false } : p).ToList());

        cut.Find("[data-filtro-pasos='todos']").Click();
        cut.Find($"[data-paso='{pasos[0].AcreditacionId}']").Click();
        cut.Find("[data-opcion='rechazado']").Click();
        cut.Find("[data-campos-opcion='rechazado'] select").Change(nameof(CausaRechazoAcreditacion.Otro));
        cut.Find("[data-campos-opcion='rechazado'] textarea").Input("Motivo");
        cut.Find("[data-accion='primario']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-aviso='reabierta']").Should().NotBeNull());
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("gestiones/").And.NotContain("mi-trabajo");
    }

    [Fact]
    public void Cambiar_de_Gestion_en_la_misma_instancia_no_arrastra_el_aviso_de_reapertura_y_consulta_la_nueva()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Subida, Hecho = true }).ToList();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));
        m.AlMarcar = () => m.Detalle = Detalle(pasos.Select((p, i) => i == 0 ? p with { Estado = EstadoAcreditacion.Rechazada, Hecho = false } : p).ToList());
        cut.Find("[data-filtro-pasos='todos']").Click();
        cut.Find($"[data-paso='{pasos[0].AcreditacionId}']").Click();
        cut.Find("[data-opcion='rechazado']").Click();
        cut.Find("[data-campos-opcion='rechazado'] select").Change(nameof(CausaRechazoAcreditacion.Otro));
        cut.Find("[data-campos-opcion='rechazado'] textarea").Input("Motivo");
        cut.Find("[data-accion='guardar']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-aviso='reabierta']").Should().ContainSingle("control positivo: el aviso estaba"));

        var otra = Guid.NewGuid();
        cut.Render(p => p.Add(x => x.Id, otra));

        cut.WaitForAssertion(() => m.DetallesPedidos.Last().Should().Be(otra));
        cut.FindAll("[data-aviso='reabierta']").Should().BeEmpty();
    }

    [Fact]
    public void El_detalle_consulta_la_Gestion_de_la_ruta()
    {
        var m = Montar();
        Detallar(m, Detalle(SeisPasosNuevos()));

        m.DetallesPedidos.Should().Equal(GestionId);
    }

    [Fact]
    public void Deshacer_tras_Siguiente_Gestion_vuelve_a_la_Gestion_del_paso_con_ese_paso_abierto()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Aceptada, Hecho = true }).ToList();
        pasos[5] = pasos[5] with { Estado = EstadoAcreditacion.PendienteDeSubir, Hecho = false };
        var m = Montar();
        var otra = Guid.NewGuid();
        m.Listado = new ListadoGestionesAutomaticasDto([Fila(otra, EstadoGestionAutomatica.EnCurso)], Recuentos());
        var cut = Detallar(m, Detalle(pasos));
        cut.Find("[data-opcion='validado']").Click();
        cut.Find("[data-campos-opcion='validado'] input[type='checkbox']").Change(true);
        cut.Find("[data-accion='primario']").Click();
        var navegacion = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => navegacion.Uri.Should().EndWith($"gestiones/{otra}"));
        cut.Render(p => p.Add(x => x.Id, otra));

        Toasts.Mensajes.Should().ContainSingle("el aviso con Deshacer sobrevive a Siguiente Gestión");
        PulsarDeshacer(cut);

        cut.WaitForAssertion(() => navegacion.Uri.Should().EndWith($"gestiones/{GestionId}?paso={pasos[5].AcreditacionId}"));
        m.Enviados.OfType<CaeManager.Application.Documentos.Commands.RestaurarAnotacionAcreditacion.RestaurarAnotacionAcreditacionCommand>()
            .Should().ContainSingle().Which.AcreditacionId.Should().Be(pasos[5].AcreditacionId);
    }

    [Fact]
    public void Un_paso_rechazado_no_ofrece_Subido_y_explica_que_hace_falta_una_version_corregida()
    {
        var pasos = SeisPasosNuevos();
        pasos[0] = pasos[0] with { Estado = EstadoAcreditacion.Rechazada };
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[0].AcreditacionId.ToString());
        cut.Find("[data-opcion='subido']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-opcion='validado']").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-aviso-subido-rechazado]").TextContent.Should().Contain("versión corregida del documento");

        cut.Find($"[data-paso='{pasos[1].AcreditacionId}']").Click();
        cut.Find("[data-opcion='subido']").HasAttribute("disabled").Should().BeFalse("control: un paso por subir sí lo ofrece");
        cut.FindAll("[data-aviso-subido-rechazado]").Should().BeEmpty();
    }

    [Fact]
    public void Siguiente_Gestion_prefiere_la_de_la_misma_Empresa_gestionada_aunque_no_sea_la_primera()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Aceptada, Hecho = true }).ToList();
        pasos[5] = pasos[5] with { Estado = EstadoAcreditacion.PendienteDeSubir, Hecho = false };
        var m = Montar();
        var deOtraEmpresa = Guid.NewGuid();
        var deLaMisma = Guid.NewGuid();
        m.Listado = new ListadoGestionesAutomaticasDto(
            [Fila(deOtraEmpresa, EstadoGestionAutomatica.EnCurso) with { EmpresaGestionadaId = Guid.NewGuid(), EmpresaGestionadaNombre = "Otra S.L." },
             Fila(deLaMisma, EstadoGestionAutomatica.EnCurso)], Recuentos());
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-accion='primario']").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"gestiones/{deLaMisma}"));
    }

    [Fact]
    public void Sin_Empresa_gestionada_visible_el_detalle_se_pinta_y_Siguiente_Gestion_toma_la_primera()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Aceptada, Hecho = true }).ToList();
        pasos[5] = pasos[5] with { Estado = EstadoAcreditacion.PendienteDeSubir, Hecho = false };
        var m = Montar();
        var primera = Guid.NewGuid();
        m.Listado = new ListadoGestionesAutomaticasDto(
            [Fila(primera, EstadoGestionAutomatica.EnCurso) with { EmpresaGestionadaId = null, EmpresaGestionadaNombre = null },
             Fila(Guid.NewGuid(), EstadoGestionAutomatica.EnCurso)], Recuentos());
        var d = Detalle(pasos);
        d = d with { Resumen = d.Resumen with { EmpresaGestionadaId = null, EmpresaGestionadaNombre = null } };
        var cut = Detallar(m, d);

        cut.Markup.Should().NotContain("Construcciones Ebro S.L.");
        cut.Find("[data-accion='primario']").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"gestiones/{primera}"));
    }

    private ToastService Toasts => Services.GetRequiredService<ToastService>();

    private void PulsarDeshacer(IRenderedComponent<GestionAutomaticaDetalle> cut)
    {
        var toast = Toasts.Mensajes.Should().ContainSingle().Subject;
        toast.TextoAccion.Should().Be("Deshacer");
        cut.InvokeAsync(() => Toasts.EjecutarAccionAsync(toast.Id)).GetAwaiter().GetResult();
    }

    [Fact]
    public void Deshacer_reabre_el_paso_restaurado()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));
        cut.Find("[data-opcion='validado']").Click();
        cut.Find("[data-campos-opcion='validado'] input[type='checkbox']").Change(true);
        cut.Find("[data-accion='primario']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[1].AcreditacionId.ToString()));

        PulsarDeshacer(cut);

        cut.WaitForAssertion(() => cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[0].AcreditacionId.ToString()));
    }

    [Fact]
    public void Sustituida_sin_Gestion_nueva_visible_en_la_cartera_no_enseña_enlace()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos(), sustituida: true, sustituidaPor: null));

        cut.Find("[data-aviso='sustituida']").TextContent.Should().Contain("Esta Gestión se cerró");
        cut.FindAll("[data-aviso='sustituida'] a").Should().BeEmpty();
    }

    [Fact]
    public void Un_rechazo_sobre_una_Gestion_pendiente_de_validar_la_reabre_y_lo_dice()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Subida, Hecho = true }).ToList();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));
        cut.Find("[data-estado-gestion]").TextContent.Should().Be("Pendiente de validar");
        m.AlMarcar = () => m.Detalle = Detalle(pasos.Select((p, i) => i == 0 ? p with { Estado = EstadoAcreditacion.Rechazada, Hecho = false } : p).ToList());

        cut.Find("[data-filtro-pasos='todos']").Click();
        cut.Find($"[data-paso='{pasos[0].AcreditacionId}']").Click();
        cut.Find("[data-opcion='rechazado']").Click();
        cut.Find("[data-campos-opcion='rechazado'] select").Change(nameof(CausaRechazoAcreditacion.DatosErroneos));
        cut.Find("[data-campos-opcion='rechazado'] textarea").Input("El DNI no coincide");
        cut.Find("[data-accion='guardar']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-aviso='reabierta']").TextContent
            .Should().Contain("La Gestión se ha reabierto.").And.Contain("Nalanda ha rechazado el paso de Planta Norte."));
        cut.Find("[data-estado-gestion]").TextContent.Should().Be("En curso");
        cut.Find("[data-aviso='reabierta'] button").TextContent.Should().Be("Ir al paso rechazado");
    }

    [Fact]
    public void Menu_mas_acciones_lleva_al_Centro_360_del_paso_abierto_y_a_la_ficha_del_titular()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.FindAll("[role='menu']").Should().BeEmpty();
        cut.Find(".gp-menu-boton").Click();

        cut.Find("[data-menu-centro]").GetAttribute("href").Should().Be($"centros/{pasos[0].CentroId}");
        cut.Find("[data-menu-centro]").TextContent.Should().Be("Ver en Centro 360 · Planta Norte");
        cut.Find("[data-menu-titular]").GetAttribute("href").Should().Be($"trabajadores/{TrabajadorId}");
        cut.Find("[data-menu-titular]").TextContent.Should().Be("Ver ficha de Laura Gil Pardo");
    }

    [Fact]
    public void Gestion_de_Empresa_enlaza_la_ficha_de_la_Empresa_y_el_identificador_es_un_CIF()
    {
        var d = Detalle(SeisPasosNuevos());
        d = d with { Documento = d.Documento with { TrabajadorId = null, EmpresaId = EmpresaId, IdentificadorTitular = "B50331406" } };
        var m = Montar();
        var cut = Detallar(m, d);

        cut.Find("[data-campo='GpCampoCif']").TextContent.Should().Contain("CIF del titular").And.Contain("B50331406");
        cut.Find(".gp-menu-boton").Click();
        cut.Find("[data-menu-titular]").GetAttribute("href").Should().Be($"empresas/{EmpresaId}");
    }

    // ---------- detalle: estados de pantalla ----------

    [Fact]
    public void Gestion_sin_pasos_visibles_o_inexistente_enseña_el_vacio_con_Siguiente_Gestion_y_Mi_trabajo()
    {
        var m = Montar();
        var cut = Detallar(m, null);

        var vacio = cut.Find("[data-estado='vacio']");
        vacio.TextContent.Should().Contain("Esta Gestión no tiene pasos que atender");
        vacio.QuerySelector("button")!.TextContent.Should().Be("Siguiente Gestión");
        vacio.QuerySelector("a[href='mi-trabajo']")!.TextContent.Should().Be("Volver a Mi trabajo");
    }

    [Fact]
    public void Error_de_carga_enseña_el_aviso_con_Reintentar_y_Volver_a_Mi_trabajo_y_reintentar_recarga()
    {
        var m = Montar();
        m.FallaDetalle = true;
        m.Detalle = Detalle(SeisPasosNuevos());
        var cut = Render<GestionAutomaticaDetalle>(p => p.Add(x => x.Id, GestionId));

        cut.WaitForAssertion(() => cut.Find("[data-estado='error']").TextContent.Should().Contain("No se ha podido cargar la Gestión").And.Contain("Tu avance no se ha perdido"));
        cut.Find("[data-estado='error'] a[href='mi-trabajo']").TextContent.Should().Be("Volver a Mi trabajo");

        m.FallaDetalle = false;
        cut.Find("[data-estado='error'] button").Click();

        cut.WaitForAssertion(() => cut.Find(".gp-resumen h1").TextContent.Should().Contain("Reconocimiento médico"));
    }

    [Fact]
    public void Mientras_carga_enseña_el_esqueleto()
    {
        var m = Montar();
        var pendiente = new TaskCompletionSource<GestionAutomaticaDetalleDto?>();
        m.DetallePendiente = pendiente.Task;

        var cut = Render<GestionAutomaticaDetalle>(p => p.Add(x => x.Id, GestionId));

        cut.Find("[data-estado='cargando']").GetAttribute("aria-busy").Should().Be("true");
        pendiente.SetResult(Detalle(SeisPasosNuevos()));
        cut.WaitForAssertion(() => cut.FindAll("[data-estado='cargando']").Should().BeEmpty());
    }

    [Fact]
    public void El_parametro_paso_abre_la_Gestion_con_ese_paso_abierto()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        m.Detalle = Detalle(pasos);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"gestiones/{GestionId}?paso={pasos[3].AcreditacionId}");

        var cut = Render<GestionAutomaticaDetalle>(p => p.Add(x => x.Id, GestionId));

        cut.WaitForAssertion(() => cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[3].AcreditacionId.ToString()));
    }

    // ---------- doble ----------

    private sealed class MediatorFalso : IMediator
    {
        public static readonly Guid Version = Guid.Parse("99999999-9999-9999-9999-999999999999");

        public GestionAutomaticaDetalleDto? Detalle { get; set; }
        public Task<GestionAutomaticaDetalleDto?>? DetallePendiente { get; set; }
        public ListadoGestionesAutomaticasDto Listado { get; set; } = new([], new Dictionary<EstadoGestionAutomatica, int>());
        public EstadoGestionAutomatica? EstadoPedido { get; private set; }
        public bool FallaListado { get; set; }
        public bool FallaDetalle { get; set; }
        public string? FalloAlMarcar { get; set; }
        public Action? AlMarcar { get; set; }
        public List<object> Enviados { get; } = [];
        public List<Guid> DetallesPedidos { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerGestionesAutomaticasQuery q:
                    if (FallaListado) throw new InvalidOperationException("fallo simulado");
                    EstadoPedido = q.Estado;
                    return Responder<TResponse>(Listado);
                case ObtenerGestionAutomaticaQuery qd:
                    DetallesPedidos.Add(qd.Id);
                    if (FallaDetalle) throw new InvalidOperationException("fallo simulado");
                    return DetallePendiente is { } pendiente ? (Task<TResponse>)(object)pendiente : Responder<TResponse>(Detalle);
                case MarcarAcreditacionAceptadaCommand c:
                    Enviados.Add(c);
                    if (FalloAlMarcar is not null) return Responder<TResponse>(Result.Fallo<ResultadoAnotacionAcreditacionDto>(Error.Crear("X", FalloAlMarcar)));
                    AlMarcar?.Invoke();
                    return Responder<TResponse>(Result.Exito(new ResultadoAnotacionAcreditacionDto(
                        c.AcreditacionId, EstadoAcreditacion.PendienteDeSubir, VigenciaEnPlataforma.SinConfirmar, Version)));
                case MarcarAcreditacionSubidaCommand or MarcarAcreditacionRechazadaCommand:
                    Enviados.Add(request);
                    if (FalloAlMarcar is not null) return Responder<TResponse>(Result.Fallo(Error.Crear("X", FalloAlMarcar)));
                    AlMarcar?.Invoke();
                    return Responder<TResponse>(Result.Exito());
                case CaeManager.Application.Documentos.Commands.RestaurarAnotacionAcreditacion.RestaurarAnotacionAcreditacionCommand r:
                    Enviados.Add(r);
                    return Responder<TResponse>(Result.Exito());
                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }
        }

        private static Task<TResponse> Responder<TResponse>(object? valor) => Task.FromResult((TResponse)valor!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
