using CaeManager.Application.Centros;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Documentos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// Un documento en renuncia (art. 22.1 LPRL, «Certificado de aptitud médica») nunca vale para acceder a un Centro: no
/// es una conformidad. <see cref="EvaluacionDeAccesoPorCentroService"/> no lo trae, y el requisito bloqueante queda como
/// sin documento mientras no haya otro del tipo que sí valga. Catálogo de Tipos de Documento del 2026-10-17.
/// </summary>
public class EvaluacionDeAccesoConRenunciaTests
{
    [Theory]
    [InlineData(AmbitoAplicacion.Trabajador)]
    [InlineData(AmbitoAplicacion.Empresa)]
    public async Task Un_documento_en_renuncia_no_cuenta_como_valido_para_acceder(AmbitoAplicacion ambito)
    {
        var (mundo, centro, tipo, documento) = Escenario(ambito);
        documento.EstablecerDatosAdicionales(tipo, horas: null, nivel: null, esRenuncia: true);

        var evaluacion = await Evaluar(mundo, centro);

        var requisito = evaluacion.Requisitos.Should().ContainSingle().Subject;
        requisito.Resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Ausente,
            "la renuncia no se trae: es como no tener documento, aunque esté vigente en fechas");
        ReglaBloqueoDeAcceso.Bloquea(requisito.Resultado.Situacion).Should().BeTrue();
    }

    [Theory]
    [InlineData(AmbitoAplicacion.Trabajador)]
    [InlineData(AmbitoAplicacion.Empresa)]
    public async Task Control_el_mismo_documento_sin_renuncia_cumple(AmbitoAplicacion ambito)
    {
        var (mundo, centro, _, _) = Escenario(ambito);

        var evaluacion = await Evaluar(mundo, centro);

        evaluacion.Requisitos.Should().ContainSingle()
            .Which.Resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Cumplido);
    }

    [Fact]
    public async Task Una_renuncia_junto_a_un_apto_vigente_deja_que_el_apto_cumpla()
    {
        var (mundo, centro, tipo, renuncia) = Escenario(AmbitoAplicacion.Trabajador);
        renuncia.EstablecerDatosAdicionales(tipo, horas: null, nivel: null, esRenuncia: true);
        var trabajador = mundo.TrabajadoresContexto.ListaTrabajadores.Single();
        mundo.DocumentoDe(trabajador, tipo);

        var evaluacion = await Evaluar(mundo, centro);

        evaluacion.Requisitos.Should().ContainSingle()
            .Which.Resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Cumplido,
                "la renuncia no tapa un apto vigente: solo deja de valer ella");
    }

    private static (MundoAcreditaciones Mundo, Domain.Centros.Centro Centro, TipoDocumento Tipo, Documento Documento) Escenario(
        AmbitoAplicacion ambito)
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, ambito, "Certificado de aptitud médica");
        tipo.EstablecerDatosAdicionales(DatosAdicionalesDocumento.Renuncia);
        mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(
            new TipoDocumentoCentro(tipo.Id, centro.Id, incluido: true, bloqueaAcceso: true));
        mundo.Asignacion(trabajador, centro);
        var documento = ambito == AmbitoAplicacion.Empresa ? mundo.DocumentoDeEmpresa(tipo) : mundo.DocumentoDe(trabajador, tipo);
        return (mundo, centro, tipo, documento);
    }

    private static Task<EvaluacionDeAccesoPorCentro> Evaluar(MundoAcreditaciones mundo, Domain.Centros.Centro centro) =>
        new EvaluacionDeAccesoPorCentroService(mundo.CentrosContexto, mundo.TiposDocumentoContexto, mundo.TrabajadoresContexto,
                mundo.AsignacionesContexto, mundo.DocumentosContexto, new AlcanceDatosServiceFalso())
            .EvaluarAsync([centro.Id], CancellationToken.None);
}
