using Bunit;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.Validacion;
using CaeManager.Web.Features.Inbound.Validacion.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las dos pantallas de validación del Tenant TCE (tarjetas 10 y 8 de la maqueta de Inbound): rótulos, acciones, estados y
/// estados vacíos. Los datos ficticios viven solo aquí: la pantalla real, sin backend fusionado, no pinta ninguno.
/// </summary>
public class ValidacionInboundTests : BunitContext
{
    private static readonly Guid IdA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid IdB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid IdC = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private readonly ServicioFalso _servicio = new();

    public ValidacionInboundTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddScoped<IServicioValidacionInbound>(_ => _servicio);
        Services.AddScoped<ColaEnCursoValidacion>();
    }

    private static DocumentoPorValidar Fila(Guid id, string requerimiento, string titular, string empresa, ResultadoLectura lectura, int afectados = 0,
        string? exclusion = null, string[]? centros = null) =>
        new(id, requerimiento, EntidadValidable.Trabajador, titular, "12345678Z", empresa, "Contratista", centros ?? ["Planta Norte"],
            DateTime.UtcNow.AddHours(-30), lectura, afectados, exclusion);

    private static ColaValidacion Cola(params DocumentoPorValidar[] documentos) => new(documentos, HechosHoy: 4, TopeLote: 25, MuestraUnoDeCada: 5);

    private static DocumentoAValidar Documento(Guid id, ResultadoLectura lectura = ResultadoLectura.Correcto, bool propio = false, int? vigenciaMeses = 12) =>
        new(id, "Aptitud médica", EntidadValidable.Trabajador, "Ana Pérez", "12345678Z", "Montajes Ebro", "Contratista", "B12345674",
            ["Planta Norte"], DateTime.UtcNow.AddHours(-5), "Subido por la Empresa contratista", "Aptitud médica en vigor", "aptitud.pdf", "/descarga/aptitud.pdf",
            [new PaginaDocumento("/img/p1.png", "Página 1", [new CajaDato("Titular", 0, 0.1, 0.1, 0.4, 0.05, ResultadoLectura.Correcto, null)])],
            [new DatoAVerificar("titular", "Titular", "Ana Pérez", lectura == ResultadoLectura.Correcto ? "Ana Pérez" : "Ana Perea", lectura, lectura == ResultadoLectura.Correcto ? null : "No coincide con la ficha", 1)],
            lectura == ResultadoLectura.Correcto ? [] : [new ChequeoLectura("firma", "Firma y sello", lectura, "No se ve el sello", 1)],
            new CriterioAceptacion("Aptitud emitida por un servicio de prevención.", ["Titular", "Emisión"]),
            new DateOnly(2026, 1, 10), new DateOnly(2027, 1, 10), vigenciaMeses, EstadoVigenciaValidacion.Vigente,
            [], [new DestinoValidacion(Guid.NewGuid(), "Planta Sur", "Falta")], null, "Sin propuesta de la IA: el documento contiene datos de salud.",
            "Datos erróneos", "Falta una página", "Revisa el documento.", propio);

    private static void Pulsar(IRenderedComponent<ValidacionDocumento> cut, string texto) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto).Click();

    // ---------------------------------------------------------------- Documentos por validar (tarjeta 10)

    [Fact]
    public void La_cola_sin_backend_dice_que_no_esta_disponible_y_no_pinta_datos()
    {
        var cut = Render<DocumentosPorValidar>();

        cut.Markup.Should().Contain("Documentos por validar");
        cut.Markup.Should().Contain("La cola de validación no está disponible");
        cut.FindAll("table").Should().BeEmpty("sin backend no se inventa ninguna fila");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Empezar a validar")).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void La_cola_pinta_rotulos_filtros_agrupacion_y_filas()
    {
        _servicio.Cola = RespuestaValidacion<ColaValidacion>.Correcta(Cola(
            Fila(IdA, "Aptitud médica", "Ana Pérez", "Montajes Ebro", ResultadoLectura.Correcto),
            Fila(IdB, "Aptitud médica", "Luis Gil", "Montajes Ebro", ResultadoLectura.ConError, 2),
            Fila(IdC, "Formación PRL", "Marta Soler", "Obras Duero", ResultadoLectura.SinDetectar, 1, centros: ["Planta Norte", "Planta Sur"])));

        var cut = Render<DocumentosPorValidar>();

        foreach (var rotulo in new[] { "Documentos por validar", "Requerimiento", "Entidad", "Empresa contratista", "Centro", "Lectura con IA", "Agrupar por", "Sin agrupar", "Titular", "Subido" })
            cut.Markup.Should().Contain(rotulo);
        cut.Markup.Should().Contain("Por requerimiento, después por Empresa contratista y por orden de subida");
        cut.Markup.Should().Contain("✓ Todo correcto").And.Contain("✕ 2 datos con error").And.Contain("! 1 sin detectar");
        cut.Markup.Should().Contain("Los 2 Centros");
        cut.Markup.Should().Contain("2 documentos").And.Contain("1 documento");
        cut.FindAll("button").Count(b => b.TextContent.Trim() == "Empezar a validar").Should().Be(3, "uno en la cabecera y uno por grupo");
    }

    [Fact]
    public void Un_filtro_sin_resultados_no_se_lee_como_cola_vacia()
    {
        _servicio.Cola = RespuestaValidacion<ColaValidacion>.Correcta(Cola(Fila(IdA, "Aptitud médica", "Ana Pérez", "Montajes Ebro", ResultadoLectura.Correcto)));
        var cut = Render<DocumentosPorValidar>();

        // El buscador de la barra tiene retardo propio: el filtro se aplica aquí con la pastilla «Lectura con IA».
        cut.FindAll("button.pfb-disparador").Single(b => b.TextContent.Contains("Lectura con IA")).Click();
        cut.FindAll("button.pfb-opcion").Single(b => b.TextContent.Trim() == "Con errores").Click();

        cut.Markup.Should().Contain("Ningún documento coincide con los filtros.");
        cut.Markup.Should().NotContain("No te queda nada por validar");
    }

    [Fact]
    public void La_cola_vacia_dice_que_no_queda_nada_por_validar()
    {
        _servicio.Cola = RespuestaValidacion<ColaValidacion>.Correcta(Cola());

        var cut = Render<DocumentosPorValidar>();

        cut.Markup.Should().Contain("No te queda nada por validar");
    }

    [Fact]
    public void Una_fila_que_no_entra_en_lote_lo_dice_y_no_se_puede_marcar()
    {
        _servicio.Cola = RespuestaValidacion<ColaValidacion>.Correcta(Cola(
            Fila(IdA, "Aptitud médica", "Ana Pérez", "Montajes Ebro", ResultadoLectura.Correcto, exclusion: "aptitud médica")));

        var cut = Render<DocumentosPorValidar>();

        var casilla = cut.Find("tbody input[type=checkbox]");
        casilla.HasAttribute("disabled").Should().BeTrue();
        casilla.GetAttribute("title").Should().Contain("No se valida en lote por su tipo de documento");
    }

    [Fact]
    public void Marcar_una_fila_muestra_la_barra_de_lote_con_la_muestra_para_revision()
    {
        _servicio.Cola = RespuestaValidacion<ColaValidacion>.Correcta(Cola(Fila(IdA, "Aptitud médica", "Ana Pérez", "Montajes Ebro", ResultadoLectura.Correcto)));
        var cut = Render<DocumentosPorValidar>();

        cut.Find("tbody input[type=checkbox]").Change(true);

        cut.Markup.Should().Contain("1 seleccionado");
        cut.Markup.Should().Contain("Se abrirá 1 de cada 5 para revisión");
    }

    // ---------------------------------------------------------------- Validación (tarjeta 8)

    private IRenderedComponent<ValidacionDocumento> RenderDetalle(Guid id) => Render<ValidacionDocumento>(p => p.Add(x => x.Id, id));

    [Fact]
    public void El_documento_sin_backend_dice_que_no_esta_disponible()
    {
        var cut = RenderDetalle(IdA);

        cut.Markup.Should().Contain("El documento no está disponible");
        cut.Markup.Should().NotContain("Datos a verificar");
    }

    [Fact]
    public void La_validacion_pinta_las_tres_acciones_y_dice_la_frontera_entre_incompleto_y_rechazado()
    {
        _servicio.Documento = RespuestaValidacion<DocumentoAValidar>.Correcta(Documento(IdA));

        var cut = RenderDetalle(IdA);

        foreach (var texto in new[] { "Validar", "Rechazar", "Marcar incompleto" })
            cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == texto);
        cut.Markup.Should().Contain("Datos a verificar").And.Contain("Fechas").And.Contain("Atajos (?)");
        cut.Markup.Should().Contain("Rechazado o incompleto");
        cut.Markup.Should().Contain("El requerimiento queda Incompleto: la Empresa contratista ve qué falta y lo completa subiendo de nuevo.");
        cut.Markup.Should().Contain("Todo correcto: pulsa Espacio para validar.");
        cut.Markup.Should().Contain("Sin propuesta de la IA: el documento contiene datos de salud.");
    }

    [Fact]
    public void Validar_con_todo_correcto_registra_el_veredicto_y_ofrece_deshacer_diez_segundos()
    {
        _servicio.Documento = RespuestaValidacion<DocumentoAValidar>.Correcta(Documento(IdA));
        _servicio.Veredicto = RespuestaValidacion<VeredictoRegistrado>.Correcta(new VeredictoRegistrado(Guid.NewGuid(), VeredictoValidacion.Validado));
        var cut = RenderDetalle(IdA);

        // «Al terminar, pasar al siguiente» viene marcada: se desmarca para quedarse en el documento y ver el aviso.
        cut.FindAll("input[type=checkbox]").Last().Change(false);
        Pulsar(cut, "Validar");

        _servicio.Llamadas.Should().ContainSingle(l => l.StartsWith("validar"));
        cut.Markup.Should().Contain("Deshacer (10 s)");
        cut.Markup.Should().Contain("El aviso a la Empresa contratista sale al terminar el plazo.");
    }

    [Fact]
    public void Validar_con_errores_pide_confirmarlo_antes_de_registrar()
    {
        _servicio.Documento = RespuestaValidacion<DocumentoAValidar>.Correcta(Documento(IdA, ResultadoLectura.ConError));
        var cut = RenderDetalle(IdA);

        Pulsar(cut, "Validar");

        cut.Markup.Should().Contain("Se han detectado errores en el documento");
        _servicio.Llamadas.Should().BeEmpty("el veredicto no sale hasta confirmar");
    }

    [Fact]
    public void Rechazar_pide_doble_pulsacion_con_motivo_y_explicacion()
    {
        _servicio.Documento = RespuestaValidacion<DocumentoAValidar>.Correcta(Documento(IdA));
        _servicio.Veredicto = RespuestaValidacion<VeredictoRegistrado>.Correcta(new VeredictoRegistrado(Guid.NewGuid(), VeredictoValidacion.Rechazado));
        var cut = RenderDetalle(IdA);

        Pulsar(cut, "Rechazar");
        cut.Markup.Should().Contain("Motivo de rechazo").And.Contain("Qué tiene que corregir (lo verá la Empresa contratista)");
        _servicio.Llamadas.Should().BeEmpty("la primera pulsación solo abre el formulario");

        Pulsar(cut, "Rechazar");
        _servicio.Llamadas.Should().ContainSingle(l => l.StartsWith("rechazar"));
    }

    [Fact]
    public void Marcar_incompleto_pide_que_se_diga_que_falta()
    {
        _servicio.Documento = RespuestaValidacion<DocumentoAValidar>.Correcta(Documento(IdA));
        var cut = RenderDetalle(IdA);

        Pulsar(cut, "Marcar incompleto");

        cut.Markup.Should().Contain("Qué falta").And.Contain("Falta la firma o el sello");
        cut.Markup.Should().Contain("Elige qué falta y pulsa Marcar incompleto otra vez (I) para confirmar.");
    }

    [Fact]
    public void Un_documento_subido_por_el_propio_gestor_no_se_puede_validar()
    {
        _servicio.Documento = RespuestaValidacion<DocumentoAValidar>.Correcta(Documento(IdA, propio: true));
        _servicio.Veredicto = RespuestaValidacion<VeredictoRegistrado>.Correcta(new VeredictoRegistrado(Guid.NewGuid(), VeredictoValidacion.Rechazado));

        var cut = RenderDetalle(IdA);

        // D-12 solo habla de Validar: Rechazar y Marcar incompleto siguen disponibles (la maqueta deshabilitaba los tres).
        cut.Find(".vd-aviso-propio").TextContent.Should().Contain("Lo subiste tú: lo valida otra persona.")
            .And.Contain("Puede validarlo otro Gestor CAE con este contrato en su cartera o el Administrador de esta organización.");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Validar").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Rechazar").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Marcar incompleto").HasAttribute("disabled").Should().BeFalse();

        // El atajo de teclado tampoco valida: la guarda vive en el handler, no en el marcado.
        var atajos = cut.FindComponent<CaeManager.Web.Features.Inbound.Validacion.Components.AtajosValidacion>().Instance;
        cut.InvokeAsync(() => atajos.RecibirAtajo("Espacio")).GetAwaiter().GetResult();
        cut.InvokeAsync(() => atajos.RecibirAtajo("v")).GetAwaiter().GetResult();
        _servicio.Llamadas.Should().NotContain(l => l.StartsWith("validar"));

        Pulsar(cut, "Rechazar");
        Pulsar(cut, "Rechazar");
        _servicio.Llamadas.Should().ContainSingle(l => l.StartsWith("rechazar"), "quien lo subió sí puede rechazarlo");
    }

    [Fact]
    public void La_cola_senala_el_documento_que_subio_quien_valida_y_lo_deja_en_la_cola()
    {
        _servicio.Cola = RespuestaValidacion<ColaValidacion>.Correcta(Cola(
            Fila(IdA, "Aptitud médica", "Ana Pérez", "Montajes Ebro", ResultadoLectura.Correcto) with { Propio = true },
            Fila(IdB, "Aptitud médica", "Luis Gil", "Montajes Ebro", ResultadoLectura.Correcto)));

        var cut = Render<DocumentosPorValidar>();

        var senales = cut.FindAll(".dpv-propio");
        senales.Should().ContainSingle().Which.TextContent.Should().Be("Lo subiste tú: lo valida otra persona.");
        cut.Markup.Should().Contain("Ana Pérez").And.Contain("Luis Gil");
    }

    // ---------------------------------------------------------------- Reglas puras

    [Fact]
    public void La_cola_se_ordena_por_requerimiento_empresa_y_subida()
    {
        var viejo = Fila(IdA, "Formación PRL", "A", "Obras Duero", ResultadoLectura.Correcto) with { SubidoUtc = DateTime.UtcNow.AddDays(-5) };
        var nuevo = Fila(IdB, "Formación PRL", "B", "Obras Duero", ResultadoLectura.Correcto) with { SubidoUtc = DateTime.UtcNow.AddDays(-1) };
        var otra = Fila(IdC, "Aptitud médica", "C", "Zeta", ResultadoLectura.Correcto);

        LogicaValidacionInbound.Ordenar([nuevo, viejo, otra]).Select(d => d.Id).Should().Equal(IdC, IdA, IdB);
    }

    [Fact]
    public void Solo_entra_en_lote_lo_que_sale_todo_correcto_y_no_es_de_un_tipo_excluido()
    {
        LogicaValidacionInbound.MotivoNoLote(Fila(IdA, "x", "t", "e", ResultadoLectura.Correcto)).Should().BeNull();
        LogicaValidacionInbound.MotivoNoLote(Fila(IdA, "x", "t", "e", ResultadoLectura.SinDetectar, 1)).Should().NotBeNull();
        LogicaValidacionInbound.MotivoNoLote(Fila(IdA, "x", "t", "e", ResultadoLectura.Correcto, exclusion: "maquinaria")).Should().Contain("maquinaria");
        LogicaValidacionInbound.MuestraParaRevision(11, 5).Should().Be(3);
    }

    [Fact]
    public void Sin_ninguna_lectura_el_peor_resultado_es_sin_detectar_y_no_correcto()
    {
        // Una lectura vacía (la IA no corrió o falló) no es «todo correcto»: antes de la corrección devolvía Correcto.
        LogicaValidacionInbound.Peor([]).Should().Be(ResultadoLectura.SinDetectar);
        LogicaValidacionInbound.Peor([ResultadoLectura.Correcto, ResultadoLectura.Correcto]).Should().Be(ResultadoLectura.Correcto);
        LogicaValidacionInbound.Peor([ResultadoLectura.Correcto, ResultadoLectura.SinDetectar]).Should().Be(ResultadoLectura.SinDetectar);
        LogicaValidacionInbound.Peor([ResultadoLectura.SinDetectar, ResultadoLectura.ConError]).Should().Be(ResultadoLectura.ConError);
    }

    [Fact]
    public void Un_documento_sin_ninguna_lectura_pide_confirmar_antes_de_validar()
    {
        var sinLectura = Documento(IdA) with { DatosAVerificar = [], Chequeos = [] };
        _servicio.Documento = RespuestaValidacion<DocumentoAValidar>.Correcta(sinLectura);
        var cut = RenderDetalle(IdA);

        cut.Markup.Should().Contain("Hay datos sin detectar: revísalos antes de validar.");
        cut.Markup.Should().NotContain("Todo correcto: pulsa Espacio para validar.");

        Pulsar(cut, "Validar");

        cut.Markup.Should().Contain("Hay datos sin detectar en el documento");
        _servicio.Llamadas.Should().BeEmpty("sin lectura no hay nada «correcto»: el veredicto no sale hasta confirmar");
    }

    // ---------------------------------------------------------------- Lote: solo filas visibles y elegibles

    private static void PulsarFiltro(IRenderedComponent<DocumentosPorValidar> cut, string pastilla, string opcion)
    {
        cut.FindAll("button.pfb-disparador").Single(b => b.TextContent.Contains(pastilla)).Click();
        cut.FindAll("button.pfb-opcion").Single(b => b.TextContent.Trim() == opcion).Click();
    }

    // La barra de lote abre el diálogo de confirmación; el botón «Validar» del diálogo es el que no cuelga de la barra.
    private static void ConfirmarLote(IRenderedComponent<DocumentosPorValidar> cut)
    {
        cut.FindAll(".blv button").Single(b => b.TextContent.Trim() == "Validar").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Validar" && b.Closest(".blv") is null).Click();
    }

    private static AngleSharp.Dom.IElement CasillaDe(IRenderedComponent<DocumentosPorValidar> cut, string titular) =>
        cut.FindAll("tbody tr").Single(f => f.TextContent.Contains(titular)).QuerySelector("input[type=checkbox]")!;

    [Fact]
    public void Una_marca_que_un_filtro_esconde_se_quita_y_no_entra_en_el_lote()
    {
        _servicio.Cola = RespuestaValidacion<ColaValidacion>.Correcta(Cola(
            Fila(IdA, "Aptitud médica", "Ana Pérez", "Montajes Ebro", ResultadoLectura.Correcto),
            Fila(IdB, "Aptitud médica", "Luis Gil", "Obras Duero", ResultadoLectura.Correcto)));
        var cut = Render<DocumentosPorValidar>();

        // Tras cada marca la tabla se repinta: la casilla se busca de nuevo en cada vuelta.
        CasillaDe(cut, "Ana Pérez").Change(true);
        CasillaDe(cut, "Luis Gil").Change(true);
        cut.Markup.Should().Contain("2 seleccionados");

        PulsarFiltro(cut, "Empresa contratista", "Montajes Ebro");

        cut.Markup.Should().Contain("1 seleccionado").And.NotContain("2 seleccionados");
        ConfirmarLote(cut);

        _servicio.IdsDeLote.Should().ContainSingle().Which.Should().Equal(IdA);
    }

    [Fact]
    public void El_lote_no_lleva_una_fila_no_elegible_aunque_el_navegador_reactive_su_casilla()
    {
        _servicio.Cola = RespuestaValidacion<ColaValidacion>.Correcta(Cola(
            Fila(IdA, "Aptitud médica", "Ana Pérez", "Montajes Ebro", ResultadoLectura.Correcto),
            Fila(IdB, "Aptitud médica", "Luis Gil", "Montajes Ebro", ResultadoLectura.ConError, 1),
            Fila(IdC, "Aptitud médica", "Marta Soler", "Montajes Ebro", ResultadoLectura.Correcto, exclusion: "aptitud médica")));
        var cut = Render<DocumentosPorValidar>();

        // El servidor del circuito no se fía del atributo disabled: se reactivan a mano las casillas de lo no elegible.
        foreach (var titular in new[] { "Luis Gil", "Marta Soler" })
        {
            var casilla = CasillaDe(cut, titular);
            casilla.HasAttribute("disabled").Should().BeTrue();
            casilla.RemoveAttribute("disabled");
            casilla.Change(true);
        }

        cut.FindAll(".blv").Should().BeEmpty("ninguna fila no elegible queda marcada");

        CasillaDe(cut, "Ana Pérez").Change(true);
        cut.Markup.Should().Contain("1 seleccionado");
        ConfirmarLote(cut);

        _servicio.IdsDeLote.Should().ContainSingle().Which.Should().Equal(IdA);
    }

    [Fact]
    public void Una_cola_rechazada_por_el_servidor_se_dice_como_error_y_no_como_cola_vacia()
    {
        _servicio.Cola = RespuestaValidacion<ColaValidacion>.Rechazada("No tienes ninguna cartera asignada.");

        var cut = Render<DocumentosPorValidar>();

        cut.Markup.Should().Contain("No se pudo cargar la cola");
        cut.Markup.Should().NotContain("No te queda nada por validar");
    }

    [Fact]
    public void La_recarga_quita_la_marca_de_una_fila_que_deja_de_ser_elegible()
    {
        _servicio.Cola = RespuestaValidacion<ColaValidacion>.Correcta(Cola(
            Fila(IdA, "Aptitud médica", "Ana Pérez", "Montajes Ebro", ResultadoLectura.Correcto),
            Fila(IdC, "Aptitud médica", "Marta Soler", "Montajes Ebro", ResultadoLectura.Correcto)));
        var cut = Render<DocumentosPorValidar>();

        CasillaDe(cut, "Ana Pérez").Change(true);
        _servicio.Lote = RespuestaValidacion<LoteRegistrado>.Correcta(new LoteRegistrado(Guid.NewGuid(), 1, 1));
        ConfirmarLote(cut);
        cut.Markup.Should().Contain("1 documento validado en lote");

        CasillaDe(cut, "Marta Soler").Change(true);
        cut.Markup.Should().Contain("1 seleccionado");

        // Deshacer recarga la cola: entre medias la lectura de la fila marcada pasó a «sin detectar» y ya no entra en lote.
        _servicio.Cola = RespuestaValidacion<ColaValidacion>.Correcta(Cola(
            Fila(IdA, "Aptitud médica", "Ana Pérez", "Montajes Ebro", ResultadoLectura.Correcto),
            Fila(IdC, "Aptitud médica", "Marta Soler", "Montajes Ebro", ResultadoLectura.SinDetectar, 1)));
        cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith("Deshacer")).Click();

        // El aviso espera a cancelar su cuenta atrás antes de llamar al servicio: la recarga llega de forma asíncrona.
        cut.WaitForAssertion(() => cut.FindAll(".blv").Should().BeEmpty("la marca de una fila que ya no es elegible no sobrevive a la recarga"));
        _servicio.Llamadas.Should().Contain("deshacer lote");
    }

    [Fact]
    public void Con_un_veredicto_en_vuelo_no_se_navega_a_otro_documento()
    {
        Services.GetRequiredService<ColaEnCursoValidacion>().Fijar([IdA, IdB]);
        _servicio.Documento = RespuestaValidacion<DocumentoAValidar>.Correcta(Documento(IdA));
        _servicio.Pendiente = new TaskCompletionSource<RespuestaValidacion<VeredictoRegistrado>>();
        var cut = RenderDetalle(IdA);
        var navegacion = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var uriInicial = navegacion.Uri;

        Pulsar(cut, "Validar");
        Pulsar(cut, "Siguiente (J)");

        navegacion.Uri.Should().Be(uriInicial, "el veredicto de A está en vuelo: navegar a B haría que su resultado cayera en B");

        _servicio.Pendiente.SetResult(RespuestaValidacion<VeredictoRegistrado>.Correcta(new VeredictoRegistrado(Guid.NewGuid(), VeredictoValidacion.Validado)));
        cut.WaitForAssertion(() => navegacion.Uri.Should().Contain(IdB.ToString(), "con «pasar al siguiente» se avanza una vez registrado"));
    }

    [Fact]
    public void Un_veredicto_que_vuelve_estando_ya_en_otro_documento_no_toca_el_estado_de_ese_otro()
    {
        Services.GetRequiredService<ColaEnCursoValidacion>().Fijar([IdA, IdB]);
        _servicio.Documento = RespuestaValidacion<DocumentoAValidar>.Correcta(Documento(IdA));
        _servicio.Pendiente = new TaskCompletionSource<RespuestaValidacion<VeredictoRegistrado>>();
        var cut = RenderDetalle(IdA);
        Pulsar(cut, "Validar");

        // Se entra en B por un camino que no pasa por J ni K (menú, «atrás» del navegador) con el veredicto de A en vuelo.
        cut.Render(p => p.Add(x => x.Id, IdB));
        _servicio.Pendiente.SetResult(RespuestaValidacion<VeredictoRegistrado>.Correcta(new VeredictoRegistrado(Guid.NewGuid(), VeredictoValidacion.Validado)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Deshacer (10 s)"));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Validar").HasAttribute("disabled").Should().BeFalse("B sigue pendiente: el veredicto era de A");
    }

    private sealed class ServicioFalso : IServicioValidacionInbound
    {
        public RespuestaValidacion<ColaValidacion> Cola { get; set; } = RespuestaValidacion<ColaValidacion>.NoDisponible;
        public RespuestaValidacion<DocumentoAValidar> Documento { get; set; } = RespuestaValidacion<DocumentoAValidar>.NoDisponible;
        public RespuestaValidacion<VeredictoRegistrado> Veredicto { get; set; } = RespuestaValidacion<VeredictoRegistrado>.NoDisponible;
        public List<string> Llamadas { get; } = [];
        public TaskCompletionSource<RespuestaValidacion<VeredictoRegistrado>>? Pendiente { get; set; }
        public RespuestaValidacion<LoteRegistrado> Lote { get; set; } = RespuestaValidacion<LoteRegistrado>.NoDisponible;

        public Task<RespuestaValidacion<ColaValidacion>> ObtenerColaAsync(CancellationToken cancellationToken = default) => Task.FromResult(Cola);

        public Task<RespuestaValidacion<DocumentoAValidar>> ObtenerDocumentoAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Documento);

        public Task<RespuestaValidacion<VeredictoRegistrado>> ValidarAsync(SolicitudValidar solicitud, CancellationToken cancellationToken = default)
        {
            Llamadas.Add("validar " + solicitud.ConfirmadoConErrores);
            return Pendiente is { } pendiente ? pendiente.Task : Task.FromResult(Veredicto);
        }

        public Task<RespuestaValidacion<VeredictoRegistrado>> RechazarAsync(SolicitudRechazar solicitud, CancellationToken cancellationToken = default)
        {
            Llamadas.Add("rechazar " + solicitud.Motivo);
            return Task.FromResult(Veredicto);
        }

        public Task<RespuestaValidacion<VeredictoRegistrado>> MarcarIncompletoAsync(SolicitudMarcarIncompleto solicitud, CancellationToken cancellationToken = default)
        {
            Llamadas.Add("incompleto " + solicitud.Falta);
            return Task.FromResult(Veredicto);
        }

        public Task<RespuestaValidacion<VeredictoRegistrado>> DeshacerAsync(Guid veredictoId, CancellationToken cancellationToken = default)
        {
            Llamadas.Add("deshacer");
            return Task.FromResult(Veredicto);
        }

        public List<IReadOnlyList<Guid>> IdsDeLote { get; } = [];

        public Task<RespuestaValidacion<LoteRegistrado>> ValidarLoteAsync(IReadOnlyList<Guid> documentoIds, CancellationToken cancellationToken = default)
        {
            IdsDeLote.Add(documentoIds);
            return Task.FromResult(Lote);
        }

        public Task<RespuestaValidacion<LoteRegistrado>> DeshacerLoteAsync(Guid loteId, CancellationToken cancellationToken = default)
        {
            Llamadas.Add("deshacer lote");
            return Task.FromResult(RespuestaValidacion<LoteRegistrado>.NoDisponible);
        }
    }
}
