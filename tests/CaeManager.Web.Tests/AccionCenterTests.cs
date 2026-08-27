using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Comunicaciones.Matching;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Action Center + AI Action Review (docs/COMUNICACIONES.md § 12.6): tarjeta
/// compacta + RevisionSugerenciaModal. El botón principal de la tarjeta
/// siempre abre la revisión — el modal decide solo internamente si hace
/// falta un paso de "campos a revisar" (confianza por campo &lt; 90) antes
/// de la vista previa.
/// </summary>
public class AccionCenterTests : BunitContext
{
    private static readonly CentroSelectorDto CentroA = new(Guid.NewGuid(), "Centro Norte", "Cliente Demo", "Empresa Demo");
    private static readonly TrabajadorSelectorDto TrabajadorA = new(Guid.NewGuid(), "Ana García", "12345678A", null);
    private static readonly TipoDocumentoListaDto TipoDocA = new(
        Guid.NewGuid(), "ITA", null, false, 1, CaeManager.Domain.Documentos.AmbitoAplicacion.Trabajador,
        CaeManager.Domain.Documentos.RequisitoDocumental.Si,
        CaeManager.Domain.Documentos.NaturalezaJuridica.RequisitoCliente,
        null, null, null, null, false, false, false, CaeManager.Domain.Documentos.PerfilDocumentoOficial.Ninguno);

    [Fact]
    public void Sin_ninguna_sugerencia_pendiente_no_renderiza_nada()
    {
        var cut = Render<AccionCenter>();

        cut.FindAll(".accion-center").Should().BeEmpty();
    }

    [Fact]
    public void La_tarjeta_de_visita_muestra_confianza_agregada_y_resumen()
    {
        var sugerencia = new SugerenciaVisitaDetalleDto(
            Guid.NewGuid(), CentroA.Id, CentroA.Nombre, new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20),
            "Pide una visita el jueves", 92, 95, 95);

        var cut = Render<AccionCenter>(parametros => parametros
            .Add(p => p.SugerenciasVisita, [sugerencia])
            .Add(p => p.CentrosDisponibles, [CentroA]));

        cut.Find(".accion-card-confianza").TextContent.Should().Be("92%");
        cut.Markup.Should().Contain("Pide una visita el jueves");
    }

    [Fact]
    public void Descartar_una_sugerencia_de_visita_dispara_el_callback_de_descarte_sin_abrir_el_modal()
    {
        Guid? idDescartado = null;
        var sugerencia = new SugerenciaVisitaDetalleDto(Guid.NewGuid(), null, null, null, null, "Resumen", 80, 0, 0);

        var cut = Render<AccionCenter>(parametros => parametros
            .Add(p => p.SugerenciasVisita, [sugerencia])
            .Add(p => p.OnDescartarSugerenciaVisita, id => idDescartado = id));

        cut.Find(".accion-card-botones button").Click();

        idDescartado.Should().NotBeNull();
        cut.FindAll(".revision-stepper").Should().BeEmpty();
    }

    [Fact]
    public void Una_visita_con_todos_los_campos_confirmados_abre_el_modal_directo_en_vista_previa()
    {
        var sugerencia = new SugerenciaVisitaDetalleDto(
            Guid.NewGuid(), CentroA.Id, CentroA.Nombre, new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20),
            "Resumen", 95, 96, 97);

        var cut = Render<AccionCenter>(parametros => parametros
            .Add(p => p.SugerenciasVisita, [sugerencia])
            .Add(p => p.CentrosDisponibles, [CentroA]));

        cut.FindAll(".accion-card-botones button")[1].Click();

        cut.Find(".revision-stepper-actual b").TextContent.Should().Be("Vista previa");
        cut.FindAll(".revision-campo-editable").Should().BeEmpty();
    }

    [Fact]
    public void Confirmar_una_visita_sin_tocar_nada_manda_los_valores_originales()
    {
        var sugerenciaId = Guid.NewGuid();
        var fecha = new DateOnly(2026, 8, 20);
        (Guid SugerenciaId, Guid? CentroId, DateOnly? FechaInicio, DateOnly? FechaFin)? recibido = null;

        var sugerencia = new SugerenciaVisitaDetalleDto(sugerenciaId, CentroA.Id, CentroA.Nombre, fecha, fecha, "Resumen", 95, 96, 97);

        var cut = Render<AccionCenter>(parametros => parametros
            .Add(p => p.SugerenciasVisita, [sugerencia])
            .Add(p => p.CentrosDisponibles, [CentroA])
            .Add(p => p.OnCrearVisitaDesdeSugerencia, args => recibido = args));

        cut.FindAll(".accion-card-botones button")[1].Click(); // abre el modal (paso 2, todo confirmado)
        cut.Find(".revision-pie-acciones button:last-child").Click(); // Continuar → paso 3
        cut.Find(".revision-pie-acciones button:last-child").Click(); // Ir al formulario de visita

        recibido.Should().Be((sugerenciaId, (Guid?)CentroA.Id, (DateOnly?)fecha, (DateOnly?)fecha));
    }

    [Fact]
    public void Un_centro_sin_resolver_abre_el_modal_en_el_paso_de_revision_y_permite_elegirlo()
    {
        var sugerenciaId = Guid.NewGuid();
        (Guid SugerenciaId, Guid? CentroId, DateOnly? FechaInicio, DateOnly? FechaFin)? recibido = null;

        var sugerencia = new SugerenciaVisitaDetalleDto(sugerenciaId, null, null, null, null, "No se identifica el centro", 60, 0, 0);

        var cut = Render<AccionCenter>(parametros => parametros
            .Add(p => p.SugerenciasVisita, [sugerencia])
            .Add(p => p.CentrosDisponibles, [CentroA])
            .Add(p => p.OnCrearVisitaDesdeSugerencia, args => recibido = args));

        cut.FindAll(".accion-card-botones button")[1].Click();

        cut.Find(".revision-stepper-actual b").TextContent.Should().Be("Revisar datos");
        cut.Find("select").Change(CentroA.Id.ToString());

        // Centro no es obligatorio para Visita (se puede resolver luego en /visitas) — Continuar nunca se bloquea.
        cut.Find(".revision-pie-acciones button:last-child").Click(); // Continuar → paso 2
        cut.Find(".revision-pie-acciones button:last-child").Click(); // Continuar → paso 3
        cut.Find(".revision-pie-acciones button:last-child").Click(); // Ir al formulario de visita

        recibido!.Value.CentroId.Should().Be(CentroA.Id);
    }

    [Fact]
    public void Una_sugerencia_de_gestion_con_trabajador_sin_resolver_bloquea_continuar_hasta_elegirlo()
    {
        var sugerencia = new SugerenciaGestionDetalleDto(
            Guid.NewGuid(), null, null, TipoDocA.Id, TipoDocA.Nombre, "Resumen", 70, 0, 95);

        var cut = Render<AccionCenter>(parametros => parametros
            .Add(p => p.SugerenciasGestion, [sugerencia])
            .Add(p => p.TrabajadoresDisponibles, [TrabajadorA])
            .Add(p => p.TiposDocumentoDisponibles, [TipoDocA]));

        cut.FindAll(".accion-card-botones button")[1].Click();

        var continuar = cut.Find(".revision-pie-acciones button:last-child");
        continuar.HasAttribute("disabled").Should().BeTrue();

        cut.Find("select").Change(TrabajadorA.Id.ToString());

        continuar = cut.Find(".revision-pie-acciones button:last-child");
        continuar.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Confirmar_una_gestion_completa_dispara_generar_gestiones_con_los_ids_finales()
    {
        var sugerenciaId = Guid.NewGuid();
        (Guid SugerenciaId, Guid TrabajadorId, Guid TipoDocumentoId)? recibido = null;

        var sugerencia = new SugerenciaGestionDetalleDto(
            sugerenciaId, TrabajadorA.Id, TrabajadorA.NombreCompleto, TipoDocA.Id, TipoDocA.Nombre, "Resumen", 95, 96, 97);

        var cut = Render<AccionCenter>(parametros => parametros
            .Add(p => p.SugerenciasGestion, [sugerencia])
            .Add(p => p.TrabajadoresDisponibles, [TrabajadorA])
            .Add(p => p.TiposDocumentoDisponibles, [TipoDocA])
            .Add(p => p.OnGenerarGestionesDesdeSugerencia, args => recibido = args));

        cut.FindAll(".accion-card-botones button")[1].Click();
        cut.Find(".revision-pie-acciones button:last-child").Click(); // Continuar → paso 3
        cut.Find(".revision-pie-acciones button:last-child").Click(); // Generar gestiones

        recibido.Should().Be((sugerenciaId, TrabajadorA.Id, TipoDocA.Id));
    }

    [Fact]
    public void Una_confianza_baja_en_la_cabecera_de_tarjeta_lleva_la_clase_de_aviso()
    {
        var sugerencia = new SugerenciaVisitaDetalleDto(Guid.NewGuid(), null, null, null, null, "Resumen", 60, 0, 0);

        var cut = Render<AccionCenter>(parametros => parametros
            .Add(p => p.SugerenciasVisita, [sugerencia]));

        cut.Find(".accion-card-confianza").ClassList.Should().Contain("accion-card-confianza-baja");
    }

    [Fact]
    public void Una_sugerencia_de_vinculacion_muestra_el_asunto_destino_y_dispara_el_callback_con_su_id()
    {
        var conversacionDestinoId = Guid.NewGuid();
        var desglose = new DesgloseCoincidenciaDto(40, 0, 0, 0, 0, 0, 10);
        Guid? destinoRecibido = null;

        var cut = Render<AccionCenter>(parametros => parametros
            .Add(p => p.SugerenciaVinculacion, new SugerenciaVinculacionDetalleDto(conversacionDestinoId, "Consulta sobre visita", desglose))
            .Add(p => p.OnVincularConversacion, id => destinoRecibido = id));

        cut.Markup.Should().Contain("Consulta sobre visita");

        cut.FindAll(".accion-card-botones button")[1].Click();

        destinoRecibido.Should().Be(conversacionDestinoId);
    }

    [Fact]
    public void Descartar_una_sugerencia_de_vinculacion_la_oculta_sin_llamar_al_callback()
    {
        var desglose = new DesgloseCoincidenciaDto(40, 0, 0, 0, 0, 0, 10);
        var vinculado = false;

        var cut = Render<AccionCenter>(parametros => parametros
            .Add(p => p.SugerenciaVinculacion, new SugerenciaVinculacionDetalleDto(Guid.NewGuid(), "Consulta sobre visita", desglose))
            .Add(p => p.OnVincularConversacion, _ => vinculado = true));

        cut.FindAll(".accion-card-botones button")[0].Click();

        cut.FindAll(".accion-card").Should().BeEmpty();
        vinculado.Should().BeFalse();
    }
}
