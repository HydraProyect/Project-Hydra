using CaeManager.Domain.DocumentosIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.DocumentosIa;

public class AuditoriaExtraccionIaTests
{
    private static string HashDeEjemplo() => new('b', AuditoriaExtraccionIa.LongitudHash);

    [Fact]
    public void Crea_un_registro_de_auditoria_con_los_datos_esperados()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(
            HashDeEjemplo(), "Póliza de seguro", "mistral-ocr+anthropic", tiempoProcesamientoMs: 1500,
            costeEstimadoOcr: 0.008m, costeEstimado: 0.03m, numeroPaginas: 3, confianzaGeneral: 92, incidencias: null);

        auditoria.HashSha256.Should().Be(HashDeEjemplo());
        auditoria.TipoEsperado.Should().Be("Póliza de seguro");
        auditoria.ProveedorCodigo.Should().Be("mistral-ocr+anthropic");
        auditoria.TiempoProcesamientoMs.Should().Be(1500);
        auditoria.CosteEstimadoOcr.Should().Be(0.008m);
        auditoria.CosteEstimado.Should().Be(0.03m);
        auditoria.NumeroPaginas.Should().Be(3);
        auditoria.ConfianzaGeneral.Should().Be(92);
    }

    [Fact]
    public void Registra_siempre_la_version_de_pipeline_vigente_aunque_no_se_indique()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(
            HashDeEjemplo(), "Certificado", "cache", 5, null, null, 1, 90, "Resultado servido desde caché documental.");

        auditoria.VersionPipeline.Should().Be(ExtraccionIaCache.VersionPipelineActual,
            "es lo que permite saber bajo qué prompt/esquema se procesó, incluso en un acierto de caché");
    }

    [Fact]
    public void Guarda_el_modelo_exacto_el_request_id_y_los_proveedores_invocados()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(
            HashDeEjemplo(), "Póliza de seguro", "gemini", 1500, 0.008m, 0.03m, 3, 92, null,
            modeloExacto: "gemini-3.5-flash-002", requestId: "x-goog-request-id=abc123", proveedoresInvocados: "anthropic,gemini");

        auditoria.ModeloExacto.Should().Be("gemini-3.5-flash-002");
        auditoria.RequestId.Should().Be("x-goog-request-id=abc123");
        auditoria.ProveedoresInvocados.Should().Be("anthropic,gemini");
    }

    [Fact]
    public void El_modelo_exacto_el_request_id_y_los_proveedores_invocados_quedan_nulos_por_defecto()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(HashDeEjemplo(), "Certificado", "ninguno", 100, null, null, 1, 0, "fallo de clasificación");

        auditoria.ModeloExacto.Should().BeNull();
        auditoria.RequestId.Should().BeNull();
        auditoria.ProveedoresInvocados.Should().BeNull();
    }

    [Fact]
    public void Trunca_el_modelo_exacto_por_encima_de_la_longitud_maxima()
    {
        var modeloLargo = new string('m', AuditoriaExtraccionIa.LongitudMaximaModeloExacto + 20);

        var auditoria = AuditoriaExtraccionIa.Crear(
            HashDeEjemplo(), "Certificado", "gemini", 100, null, null, 1, 80, null, modeloExacto: modeloLargo);

        auditoria.ModeloExacto.Should().HaveLength(AuditoriaExtraccionIa.LongitudMaximaModeloExacto);
    }

    [Fact]
    public void Permite_costes_nulos_cuando_el_proveedor_no_los_calcula()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(
            HashDeEjemplo(), "Certificado", "cache", 5, null, null, 1, 90, "Resultado servido desde caché documental.");

        auditoria.CosteEstimadoOcr.Should().BeNull();
        auditoria.CosteEstimado.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("hash-invalido")]
    public void Rechaza_un_hash_que_no_tenga_la_longitud_de_sha256(string hash)
    {
        var accion = () => AuditoriaExtraccionIa.Crear(hash, "Certificado", "anthropic", 100, null, null, 1, 80, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_codigo_de_proveedor_vacio()
    {
        var accion = () => AuditoriaExtraccionIa.Crear(HashDeEjemplo(), "Certificado", "   ", 100, null, null, 1, 80, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Sin_documentoId_no_queda_ligada_a_ningun_documento()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(HashDeEjemplo(), "Certificado", "anthropic", 100, null, null, 1, 80, null);

        auditoria.DocumentoId.Should().BeNull();
        auditoria.DecisionHumana.Should().BeNull();
    }

    [Fact]
    public void Registra_una_decision_automatica_sin_usuario_cuando_esta_ligada_a_un_documento()
    {
        var documentoId = Guid.NewGuid();
        var auditoria = AuditoriaExtraccionIa.Crear(HashDeEjemplo(), "Certificado", "anthropic", 100, null, null, 1, 95, null, documentoId);

        auditoria.RegistrarDecisionHumana(DecisionHumanaIa.AutomaticaSinRevision, usuarioId: null);

        auditoria.DecisionHumana.Should().Be(DecisionHumanaIa.AutomaticaSinRevision);
        auditoria.UsuarioDecisionId.Should().BeNull();
        auditoria.FechaDecisionUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData(DecisionHumanaIa.ConfirmadaManual)]
    [InlineData(DecisionHumanaIa.DescartadaManual)]
    public void Registra_una_decision_manual_con_el_usuario_que_la_tomo(DecisionHumanaIa decision)
    {
        var documentoId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();
        var auditoria = AuditoriaExtraccionIa.Crear(HashDeEjemplo(), "Certificado", "anthropic", 100, null, null, 1, 62, null, documentoId);

        auditoria.RegistrarDecisionHumana(decision, usuarioId);

        auditoria.DecisionHumana.Should().Be(decision);
        auditoria.UsuarioDecisionId.Should().Be(usuarioId);
    }

    [Fact]
    public void Rechaza_registrar_decision_sin_documento_ligado()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(HashDeEjemplo(), "Certificado", "anthropic", 100, null, null, 1, 95, null);

        var accion = () => auditoria.RegistrarDecisionHumana(DecisionHumanaIa.AutomaticaSinRevision, null);

        accion.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Rechaza_una_decision_manual_sin_usuario()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(HashDeEjemplo(), "Certificado", "anthropic", 100, null, null, 1, 62, null, Guid.NewGuid());

        var accion = () => auditoria.RegistrarDecisionHumana(DecisionHumanaIa.ConfirmadaManual, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_una_decision_automatica_con_usuario()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(HashDeEjemplo(), "Certificado", "anthropic", 100, null, null, 1, 95, null, Guid.NewGuid());

        var accion = () => auditoria.RegistrarDecisionHumana(DecisionHumanaIa.AutomaticaSinRevision, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_registrar_una_segunda_decision_sobre_la_misma_auditoria()
    {
        var auditoria = AuditoriaExtraccionIa.Crear(HashDeEjemplo(), "Certificado", "anthropic", 100, null, null, 1, 62, null, Guid.NewGuid());
        auditoria.RegistrarDecisionHumana(DecisionHumanaIa.DescartadaManual, Guid.NewGuid());

        var accion = () => auditoria.RegistrarDecisionHumana(DecisionHumanaIa.ConfirmadaManual, Guid.NewGuid());

        accion.Should().Throw<InvalidOperationException>();
    }
}
