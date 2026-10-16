using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// Recomendaciones de Visita 360 (decisión de producto, 2026-10-16): solo salen de datos que ya existen. Sin datos de
/// los que salgan, la lista queda vacía y la página no pinta la sección.
/// </summary>
public class RecomendacionesVisitaTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 17);
    private static readonly DateOnly Inicio = new(2026, 10, 20);
    private static readonly DateOnly Fin = new(2026, 10, 24);

    private static DocumentoVisitaItemDto Documento(string tipo, EstadoDocumento estado, DateOnly? vence = null, Guid? trabajadorId = null) =>
        new(estado == EstadoDocumento.Faltante ? null : Guid.NewGuid(), trabajadorId, Guid.NewGuid(), tipo, estado, vence, null);

    private static SeccionDocumentacionDto Seccion(params DocumentoVisitaItemDto[] documentos) =>
        new(documentos.Length == 0 ? EstadoDocumento.Vigente : documentos.Max(d => d.Estado), documentos);

    private static DocumentacionVisitaDto Documentacion(SeccionDocumentacionDto empresa, params (string Nombre, SeccionDocumentacionDto Seccion)[] trabajadores) =>
        new(Guid.NewGuid(), empresa, [.. trabajadores.Select(t => new TrabajadorDocumentacionDto(Guid.NewGuid(), t.Nombre, "Empresa", t.Seccion))]);

    private static IReadOnlyList<RecomendacionVisita> Calcular(DocumentacionVisitaDto documentacion, bool cancelada = false, DateOnly? hoy = null) =>
        RecomendacionesVisita.Calcular(documentacion, "Montajes Springfield", Inicio, Fin, hoy ?? Hoy, cancelada);

    [Fact]
    public void Con_todo_en_regla_durante_la_visita_no_hay_ninguna_recomendacion()
    {
        var documentacion = Documentacion(
            Seccion(Documento("Seguro RC", EstadoDocumento.Vigente, Fin), Documento("Modelo 036", EstadoDocumento.SinCaducidad)),
            ("Homer Simpson", Seccion(Documento("Formación PRL", EstadoDocumento.Vigente, Fin.AddDays(30)))));

        Calcular(documentacion).Should().BeEmpty("sin datos de los que salga una recomendación, no se inventa ninguna");
    }

    [Fact]
    public void Sin_documentos_exigidos_no_hay_ninguna_recomendacion()
    {
        Calcular(Documentacion(Seccion(), ("Homer Simpson", Seccion()))).Should().BeEmpty();
    }

    [Fact]
    public void La_documentacion_pendiente_da_una_recomendacion_por_sujeto_con_sus_tipos()
    {
        var documentacion = Documentacion(
            Seccion(Documento("Seguro RC", EstadoDocumento.Vencido, Hoy.AddDays(-3))),
            ("Homer Simpson", Seccion(
                Documento("Formación PRL", EstadoDocumento.Faltante),
                Documento("Aptitud médica", EstadoDocumento.Vencido, Hoy.AddDays(-1)),
                Documento("Entrega de EPI", EstadoDocumento.Vigente, Fin.AddDays(60)))),
            ("Marge Bouvier", Seccion(Documento("Formación PRL", EstadoDocumento.Vigente, Fin.AddDays(60)))));

        var recomendaciones = Calcular(documentacion);

        recomendaciones.Should().BeEquivalentTo(new[]
        {
            new RecomendacionVisita(TipoRecomendacionVisita.DocumentacionPendiente, "Montajes Springfield", ["Seguro RC"]),
            new RecomendacionVisita(TipoRecomendacionVisita.DocumentacionPendiente, "Homer Simpson", ["Formación PRL", "Aptitud médica"]),
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Un_documento_en_regla_que_vence_antes_de_terminar_la_visita_se_recomienda_con_su_fecha()
    {
        var antesDeEntrar = Inicio.AddDays(-1);
        var durante = Fin.AddDays(-1);
        var documentacion = Documentacion(
            Seccion(Documento("Seguro RC", EstadoDocumento.Urgente, durante)),
            ("Homer Simpson", Seccion(
                Documento("Aptitud médica", EstadoDocumento.Proximo, antesDeEntrar),
                Documento("Formación PRL", EstadoDocumento.Vigente, Fin))));

        var recomendaciones = Calcular(documentacion);

        recomendaciones.Should().BeEquivalentTo(new[]
        {
            new RecomendacionVisita(TipoRecomendacionVisita.VenceAntesDeLaEntrada, "Homer Simpson", ["Aptitud médica"], antesDeEntrar),
            new RecomendacionVisita(TipoRecomendacionVisita.VenceDuranteLaVisita, "Montajes Springfield", ["Seguro RC"], durante),
        }, o => o.WithStrictOrdering(), "un documento que vence el último día de la visita sigue en regla toda la visita");
    }

    [Fact]
    public void Un_documento_vencido_no_se_repite_como_vencimiento()
    {
        var documentacion = Documentacion(Seccion(Documento("Seguro RC", EstadoDocumento.Vencido, Hoy.AddDays(-3))));

        Calcular(documentacion).Should().ContainSingle().Which.Tipo.Should().Be(TipoRecomendacionVisita.DocumentacionPendiente);
    }

    [Fact]
    public void Una_visita_cancelada_o_ya_terminada_no_recomienda_nada()
    {
        var documentacion = Documentacion(Seccion(Documento("Seguro RC", EstadoDocumento.Faltante)));

        Calcular(documentacion, cancelada: true).Should().BeEmpty();
        Calcular(documentacion, hoy: Fin.AddDays(1)).Should().BeEmpty();
        Calcular(documentacion, hoy: Fin).Should().NotBeEmpty("el último día la visita sigue activa");
    }
}
