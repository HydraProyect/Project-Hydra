using System.Text.RegularExpressions;
using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.SituacionEnCentro;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Segunda línea de un documento en las fichas 360 que fijan un Centro: su estado en las plataformas de ese Centro y
/// la última reclamación (<see cref="LineaPlataformaYReclamacion"/>).
///
/// La regla que más pesa es la de no redundancia (decisión del propietario, 2026-10-09): si la fila ya dice algo
/// sobre la plataforma, la segunda línea no se añade. Se fija en dos sitios: en el componente (con el parámetro, no
/// pinta nada) y en el código fuente (un fichero que ya pinta la plataforma por otra vía no puede usar la línea sin
/// declarar ese parámetro).
///
/// Lo que estas pruebas NO miden: que la línea quede bajo el nombre y no a su lado. Eso es CSS
/// (<c>.celda-documento-nombre:has(...)</c> en list-page.css) y bUnit no calcula estilos.
/// </summary>
public class LineaPlataformaYReclamacionTests : BunitContext
{
    private static readonly DateTime Envio = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);

    public LineaPlataformaYReclamacionTests() => Services.AddLocalization();

    private static AcreditacionResumenDto Acreditacion(string plataforma, EstadoAcreditacion estado) =>
        new(Guid.NewGuid(), plataforma, estado);

    private IRenderedComponent<LineaPlataformaYReclamacion> Pintar(
        EstadoDocumento estado,
        IReadOnlyList<AcreditacionResumenDto>? acreditaciones = null,
        UltimaReclamacionDocumentoDto? reclamacion = null,
        bool laFilaYaDiceLaPlataforma = false) =>
        Render<LineaPlataformaYReclamacion>(p => p
            .Add(x => x.Estado, estado)
            .Add(x => x.Acreditaciones, acreditaciones)
            .Add(x => x.UltimaReclamacion, reclamacion)
            .Add(x => x.LaFilaYaDiceLaPlataforma, laFilaYaDiceLaPlataforma));

    [Fact]
    public void Junta_el_estado_en_la_plataforma_y_la_ultima_reclamacion_en_una_sola_linea()
    {
        var cut = Pintar(
            EstadoDocumento.Vencido,
            [Acreditacion("Nalanda", EstadoAcreditacion.PendienteDeSubir)],
            new UltimaReclamacionDocumentoDto(Envio, SinRespuesta: true));

        cut.Find(".linea-plataforma-reclamacion").TextContent.Should().Be(
            $"Nalanda: {EstadoAcreditacionUi.Texto(EstadoAcreditacion.PendienteDeSubir)} · Reclamado el 02/10/2026, sin respuesta");
    }

    [Fact]
    public void El_estado_en_la_plataforma_usa_el_vocabulario_unico_de_EstadoAcreditacionUi()
    {
        foreach (var estado in Enum.GetValues<EstadoAcreditacion>())
        {
            var cut = Pintar(EstadoDocumento.Vigente, [Acreditacion("Portal", estado)]);

            cut.Find(".linea-plataforma-reclamacion").TextContent.Should().Be($"Portal: {EstadoAcreditacionUi.Texto(estado)}");
        }
    }

    [Fact]
    public void Con_varias_plataformas_en_el_Centro_las_nombra_todas_en_el_orden_recibido()
    {
        var cut = Pintar(
            EstadoDocumento.Vigente,
            [Acreditacion("Principal", EstadoAcreditacion.Aceptada), Acreditacion("Secundaria", EstadoAcreditacion.Subida)]);

        cut.Find(".linea-plataforma-reclamacion").TextContent.Should().Be(
            $"Principal: {EstadoAcreditacionUi.Texto(EstadoAcreditacion.Aceptada)} · Secundaria: {EstadoAcreditacionUi.Texto(EstadoAcreditacion.Subida)}");
    }

    [Fact]
    public void Si_la_fila_ya_dice_algo_sobre_la_plataforma_no_se_anade_la_segunda_linea()
    {
        IReadOnlyList<AcreditacionResumenDto> acreditaciones = [Acreditacion("Nalanda", EstadoAcreditacion.Rechazada)];
        var reclamacion = new UltimaReclamacionDocumentoDto(Envio, SinRespuesta: true);

        // Control positivo: con los mismos datos y sin el aviso, la línea existe.
        Pintar(EstadoDocumento.Vencido, acreditaciones, reclamacion).FindAll(".linea-plataforma-reclamacion").Should().ContainSingle();

        var cut = Pintar(EstadoDocumento.Vencido, acreditaciones, reclamacion, laFilaYaDiceLaPlataforma: true);

        cut.Markup.Trim().Should().BeEmpty("un mismo dato nunca aparece dos veces en la fila");
    }

    [Fact]
    public void Sin_plataforma_ni_reclamacion_no_pinta_nada()
    {
        Pintar(EstadoDocumento.Vencido).Markup.Trim().Should().BeEmpty();
        Pintar(EstadoDocumento.Vencido, acreditaciones: []).Markup.Trim().Should().BeEmpty();
    }

    [Theory]
    [InlineData(EstadoDocumento.Vigente)]
    [InlineData(EstadoDocumento.SinCaducidad)]
    [InlineData(EstadoDocumento.Proximo)]
    [InlineData(EstadoDocumento.Urgente)]
    public void Con_el_documento_al_dia_la_reclamacion_ya_no_se_ensena(EstadoDocumento estado)
    {
        var reclamacion = new UltimaReclamacionDocumentoDto(Envio, SinRespuesta: true);

        Pintar(estado, reclamacion: reclamacion).Markup.Trim().Should().BeEmpty();
        // La plataforma sí sigue: es un dato del documento vigente, no de lo que se pidió.
        Pintar(estado, [Acreditacion("Portal", EstadoAcreditacion.Subida)], reclamacion)
            .Find(".linea-plataforma-reclamacion").TextContent.Should().NotContain("Reclamado");
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Faltante)]
    [InlineData(EstadoDocumento.SinConfirmar)]
    [InlineData(EstadoDocumento.EnTolerancia)]
    public void Con_el_documento_pendiente_la_reclamacion_se_ensena(EstadoDocumento estado)
    {
        var cut = Pintar(estado, reclamacion: new UltimaReclamacionDocumentoDto(Envio, SinRespuesta: true));

        cut.Find(".linea-plataforma-reclamacion").TextContent.Should().Be("Reclamado el 02/10/2026, sin respuesta");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Sin_respuesta_solo_se_dice_cuando_se_sabe(bool? sinRespuesta)
    {
        var cut = Pintar(EstadoDocumento.Vencido, reclamacion: new UltimaReclamacionDocumentoDto(Envio, sinRespuesta));

        cut.Find(".linea-plataforma-reclamacion").TextContent.Should().Be("Reclamado el 02/10/2026");
    }

    [Fact]
    public void La_fecha_de_la_reclamacion_es_el_dia_de_negocio_no_el_dia_UTC()
    {
        // 23:30 UTC del día 1 son las 01:30 del día 2 en hora peninsular (CEST).
        var cut = Pintar(
            EstadoDocumento.Vencido,
            reclamacion: new UltimaReclamacionDocumentoDto(new DateTime(2026, 10, 1, 23, 30, 0, DateTimeKind.Utc), SinRespuesta: null));

        cut.Find(".linea-plataforma-reclamacion").TextContent.Should().Be("Reclamado el 02/10/2026");
    }

    [Fact]
    public void Solo_un_rechazo_en_la_plataforma_destaca_la_linea()
    {
        Pintar(EstadoDocumento.Vigente, [Acreditacion("Portal", EstadoAcreditacion.Rechazada)])
            .Find(".linea-plataforma-reclamacion").ClassList.Should().Contain("linea-plataforma-reclamacion--rechazada");
        Pintar(EstadoDocumento.Vigente, [Acreditacion("Portal", EstadoAcreditacion.PendienteDeSubir)])
            .Find(".linea-plataforma-reclamacion").ClassList.Should().NotContain("linea-plataforma-reclamacion--rechazada");
    }

    // ── Regla de no redundancia, en el código fuente ───────────────────────

    private static readonly Regex UsoDeLaLinea = new(@"<[\w.]*LineaPlataformaYReclamacion\b[^>]*>", RegexOptions.Compiled);

    /// <summary>Otras formas con las que una fila ya dice la plataforma: los badges de acreditación y el «Rechazado» de plataforma.</summary>
    private static readonly Regex OtraFormaDeDecirLaPlataforma = new(@"<BadgesAcreditacion\b|BadgeRechazadoPlataforma", RegexOptions.Compiled);

    [Fact]
    public void Un_fichero_que_ya_pinta_la_plataforma_por_otra_via_declara_si_la_fila_la_repite()
    {
        var raizWeb = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        var ficheros = Directory.EnumerateFiles(raizWeb, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != "LineaPlataformaYReclamacion.razor")
            .Select(f => (Ruta: Path.GetRelativePath(raizWeb, f), Texto: File.ReadAllText(f)))
            .ToList();

        var usos = ficheros
            .SelectMany(f => UsoDeLaLinea.Matches(f.Texto).Select(m => (f.Ruta, f.Texto, Etiqueta: m.Value)))
            .ToList();

        // El instrumento ve los usos: sin esto, un cambio en el marcado dejaría el test verde sin mirar nada.
        usos.Select(u => u.Ruta).Distinct().Should().HaveCountGreaterThanOrEqualTo(2, "Centro 360 y Trabajador 360 pintan la segunda línea");
        ficheros.Count(f => OtraFormaDeDecirLaPlataforma.IsMatch(f.Texto)).Should().BeGreaterThanOrEqualTo(3, "control: el detector de la otra vía encuentra los ficheros que ya la usan");

        var sinDeclarar = usos
            .Where(u => OtraFormaDeDecirLaPlataforma.IsMatch(u.Texto) && !u.Etiqueta.Contains("LaFilaYaDiceLaPlataforma="))
            .Select(u => u.Ruta)
            .ToList();

        sinDeclarar.Should().BeEmpty(
            "si el fichero ya enseña la plataforma con badges, cada uso de la segunda línea tiene que decir con " +
            "LaFilaYaDiceLaPlataforma si esa fila ya la nombra: un mismo dato nunca aparece dos veces en la fila");
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("No se encuentra la raíz del repositorio.");
    }
}
