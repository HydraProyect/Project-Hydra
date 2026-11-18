using CaeManager.Application.Retencion;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Retencion;

/// <summary>
/// El criterio de purga de Documentos (decisión del 2026-11-18): plazo efectivo = tipo ?? Tenant propietario, inicio =
/// <see cref="CriterioInicioRetencion.EventoMasTemprano"/> entre el evento del Documento y su sustitución. Se ejecuta
/// aquí sobre LINQ a objetos; la traducción a SQL la prueba la suite de integración en CI.
/// </summary>
public class CriterioPurgaDocumentosTests
{
    private static readonly DateOnly Hoy = new(2031, 6, 10);
    private static readonly Guid Cliente = Guid.NewGuid();

    [Fact]
    public void Un_tipo_sin_plazo_propio_hereda_el_del_Tenant_propietario()
    {
        var tipo = Tipo(null);
        var cumplido = Doc(tipo, new DateOnly(2020, 1, 1), vence: new DateOnly(2026, 6, 10));
        var pendiente = Doc(tipo, new DateOnly(2020, 1, 1), vence: new DateOnly(2026, 6, 11));

        var candidatos = Candidatos([tipo], aniosTenant: 5, cumplido, pendiente);

        candidatos.Should().ContainSingle().Which.Should().Be(new CandidatoPurgaDocumento(cumplido.Id, 5));
    }

    [Fact]
    public void El_plazo_del_tipo_manda_sobre_el_del_Tenant_propietario_en_los_dos_sentidos()
    {
        var corto = Tipo(2);
        var largo = Tipo(5);
        var vence = new DateOnly(2029, 1, 1);
        var delCorto = Doc(corto, new DateOnly(2020, 1, 1), vence);
        var delLargo = Doc(largo, new DateOnly(2020, 1, 1), vence);

        // Tenant propietario con 3 años: el tipo de 2 años purga antes; el de 5 conserva aunque el Tenant purgaría.
        var candidatos = Candidatos([corto, largo], aniosTenant: 3, delCorto, delLargo);

        candidatos.Should().ContainSingle().Which.Should().Be(new CandidatoPurgaDocumento(delCorto.Id, 2));
    }

    [Fact]
    public void Una_version_sustituida_antes_de_su_vencimiento_cuenta_desde_la_sustitucion()
    {
        var tipo = Tipo(null);
        var documento = Doc(tipo, new DateOnly(2025, 1, 1), vence: new DateOnly(2030, 1, 1));
        Sustituir(documento, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));

        Candidatos([tipo], aniosTenant: 5, documento).Should().ContainSingle(c => c.DocumentoId == documento.Id);
    }

    [Fact]
    public void Una_version_sustituida_despues_de_su_vencimiento_cuenta_desde_el_vencimiento()
    {
        var tipo = Tipo(null);
        var vencidaAntes = Doc(tipo, new DateOnly(2020, 1, 1), vence: new DateOnly(2026, 1, 1));
        Sustituir(vencidaAntes, new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var vencidaDespues = Doc(tipo, new DateOnly(2020, 1, 1), vence: new DateOnly(2027, 1, 1));
        Sustituir(vencidaDespues, new DateTime(2027, 6, 1, 9, 0, 0, DateTimeKind.Utc));

        var candidatos = Candidatos([tipo], aniosTenant: 5, vencidaAntes, vencidaDespues);

        candidatos.Should().ContainSingle().Which.DocumentoId.Should().Be(vencidaAntes.Id);
    }

    [Fact]
    public void La_sustitucion_cuenta_por_su_fecha_UTC_hasta_el_final_del_dia_de_corte()
    {
        var tipo = Tipo(null);
        var ultimoInstante = Doc(tipo, new DateOnly(2025, 1, 1), vence: new DateOnly(2030, 1, 1));
        Sustituir(ultimoInstante, new DateTime(2026, 6, 10, 23, 59, 59, DateTimeKind.Utc));
        var diaSiguiente = Doc(tipo, new DateOnly(2025, 1, 1), vence: new DateOnly(2030, 1, 1));
        Sustituir(diaSiguiente, new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc));

        var candidatos = Candidatos([tipo], aniosTenant: 5, ultimoInstante, diaSiguiente);

        candidatos.Should().ContainSingle().Which.DocumentoId.Should().Be(ultimoInstante.Id);
    }

    [Fact]
    public void Sin_vencimiento_cuenta_desde_la_emision()
    {
        var tipo = Tipo(null);
        var cumplido = Doc(tipo, new DateOnly(2026, 6, 10), vence: null);
        var pendiente = Doc(tipo, new DateOnly(2026, 6, 11), vence: null);

        Candidatos([tipo], aniosTenant: 5, cumplido, pendiente)
            .Should().ContainSingle().Which.DocumentoId.Should().Be(cumplido.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Un_plazo_fuera_de_rango_no_purga_nada(int aniosTenant)
    {
        // Falla cerrado: un valor que se colara en la base sin pasar por el dominio ni el CHECK no casa con ninguna rama.
        var tipo = Tipo(null);
        var documento = Doc(tipo, new DateOnly(2000, 1, 1), vence: new DateOnly(2001, 1, 1));

        Candidatos([tipo], aniosTenant, documento).Should().BeEmpty();
    }

    /// <summary>
    /// Coherencia con la regla de dominio: para cada plazo de 1 a 5 años y una rejilla de fechas, el criterio en
    /// consulta decide lo mismo que <see cref="CalculadoraRetencionDocumento.PuedePurgarse"/> con
    /// <see cref="CriterioInicioRetencion.EventoMasTemprano"/>, tomando la sustitución como el otro evento. La rejilla
    /// evita el 29 de febrero, donde la consulta conserva un día más (ver el test siguiente).
    /// </summary>
    [Fact]
    public void Coincide_con_la_calculadora_de_dominio_en_toda_la_rejilla()
    {
        var fechas = new[]
        {
            new DateOnly(2025, 6, 9), new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 11), new DateOnly(2027, 6, 10),
            new DateOnly(2028, 6, 11), new DateOnly(2029, 6, 10), new DateOnly(2030, 6, 10), new DateOnly(2030, 6, 11),
        };

        for (var anios = 1; anios <= 5; anios++)
        {
            var tipo = Tipo(anios);
            var documentos = new List<Documento>();
            foreach (var vence in fechas)
            foreach (var sustituido in fechas.Select(f => (DateOnly?)f).Append(null))
            {
                var documento = Doc(tipo, new DateOnly(2024, 1, 1), vence);
                if (sustituido is { } s)
                    Sustituir(documento, s.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
                documentos.Add(documento);
            }

            var candidatos = Candidatos([tipo], aniosTenant: 5, [.. documentos]).Select(c => c.DocumentoId).ToHashSet();

            foreach (var documento in documentos)
            {
                var esperado = CalculadoraRetencionDocumento.PuedePurgarse(
                    documento.FechaEmision, documento.FechaVencimiento,
                    documento.SustituidoEnUtc is { } s ? DateOnly.FromDateTime(s) : null,
                    Hoy, anios, CriterioInicioRetencion.EventoMasTemprano);
                candidatos.Contains(documento.Id).Should().Be(esperado,
                    $"plazo {anios}, vence {documento.FechaVencimiento}, sustituido {documento.SustituidoEnUtc}");
            }
        }
    }

    [Fact]
    public void Con_un_inicio_en_29_de_febrero_la_consulta_conserva_un_dia_mas_que_la_calculadora()
    {
        // 2028-02-29 + 1 año = 2029-02-28 para la calculadora; la consulta corta en 2029-02-28 − 1 año = 2028-02-28 y
        // todavía no lo alcanza. El desvío es de un día y siempre hacia conservar, nunca hacia purgar antes.
        var tipo = Tipo(1);
        var documento = Doc(tipo, new DateOnly(2026, 1, 1), vence: new DateOnly(2028, 2, 29));
        var hoy = new DateOnly(2029, 2, 28);

        CalculadoraRetencionDocumento.PuedePurgarse(
            documento.FechaEmision, documento.FechaVencimiento, null, hoy, 1, CriterioInicioRetencion.EventoMasTemprano)
            .Should().BeTrue();
        CriterioPurgaDocumentos.Candidatos(new[] { documento }.AsQueryable(), new[] { tipo }.AsQueryable(), 5, hoy)
            .Should().BeEmpty();
        CriterioPurgaDocumentos.Candidatos(new[] { documento }.AsQueryable(), new[] { tipo }.AsQueryable(), 5, hoy.AddDays(1))
            .Should().ContainSingle();
    }

    private static List<CandidatoPurgaDocumento> Candidatos(TipoDocumento[] tipos, int aniosTenant, params Documento[] documentos) =>
        [.. CriterioPurgaDocumentos.Candidatos(documentos.AsQueryable(), tipos.AsQueryable(), aniosTenant, Hoy)];

    private static TipoDocumento Tipo(int? anios)
    {
        var tipo = new TipoDocumento("Tipo", vigenciaMeses: 12, aplicaVencimientoAutomatico: false, orden: 1, AmbitoAplicacion.Cliente);
        tipo.FijarPlazoRetencionDocumentos(anios);
        return tipo;
    }

    private static Documento Doc(TipoDocumento tipo, DateOnly emision, DateOnly? vence) =>
        Documento.DeCliente(Cliente, tipo.Id, emision, vence is { } v ? VigenciaDocumento.VenceEl(v) : VigenciaDocumento.NoCaduca);

    private static void Sustituir(Documento documento, DateTime enUtc) =>
        documento.SustituirPor(
            Documento.DeCliente(Cliente, documento.TipoDocumentoId, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca),
            MotivoSustitucionDocumento.Renovacion, enUtc);
}
