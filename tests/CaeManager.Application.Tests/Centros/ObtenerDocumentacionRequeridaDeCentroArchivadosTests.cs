using CaeManager.Application.Centros.Queries.ObtenerDocumentacionExigidaDeCliente;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// Los tipos de documento archivados (revisión del catálogo del 2026-10-17) en la pestaña «Requisitos» del Centro:
/// marcar uno como requerido lo rechaza <c>EstablecerDocumentacionRequeridaCentroCommand</c> («exige su sustituto»), así que
/// la consulta no lo ofrece; solo lo lista si el Centro ya lo exige, con el nombre de su sustituto, para poder desmarcarlo.
/// La consulta del Cliente empresarial comparte el catálogo y no pierde nada de lo que aplica.
/// Contextos falsos, sin Postgres.
/// </summary>
public class ObtenerDocumentacionRequeridaDeCentroArchivadosTests
{
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();

    private readonly Empresa _clienteEmpresarial = Empresa.CrearComoCliente("Refrielectric S.A.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
    private readonly Empresa _proveedora = new("Ibertec GmbH", "B12345674");
    private readonly Centro _barakaldo;
    private readonly Centro _getafe;
    private readonly TipoDocumento _rlc;
    private readonly TipoDocumento _reciboArchivadoNoExigido;
    private readonly TipoDocumento _reciboArchivadoYExigido;
    private readonly TipoDocumento _archivadoSinSustituto;
    private readonly TipoDocumento _archivadoYaExcluido;

    public ObtenerDocumentacionRequeridaDeCentroArchivadosTests()
    {
        _barakaldo = new Centro(_clienteEmpresarial.Id, _proveedora.Id, "Planta Barakaldo");
        _getafe = new Centro(_clienteEmpresarial.Id, _proveedora.Id, "Almacén Getafe");
        _centros.ListaCentros.AddRange([_barakaldo, _getafe]);

        _rlc = Tipo("RLC (TC1)", 1, RequisitoDocumental.Si);
        _reciboArchivadoNoExigido = Tipo("Recibo de pago RLC/TC1", 2, RequisitoDocumental.No);
        _reciboArchivadoNoExigido.Archivar(_rlc);
        _reciboArchivadoYExigido = Tipo("Recibo de pago TC2", 3, RequisitoDocumental.No);
        _reciboArchivadoYExigido.Archivar(_rlc);
        _archivadoSinSustituto = Tipo("Justificante antiguo", 4, RequisitoDocumental.No);
        _archivadoSinSustituto.Archivar(null);
        _archivadoYaExcluido = Tipo("Certificado retirado", 5, RequisitoDocumental.No);
        _archivadoYaExcluido.Archivar(null);

        _tipos.ListaTiposDocumentoCentros.AddRange([
            // Barakaldo exigía estos dos antes de archivarlos: son filas explícitas Incluido = true.
            new TipoDocumentoCentro(_reciboArchivadoYExigido.Id, _barakaldo.Id, incluido: true),
            new TipoDocumentoCentro(_archivadoSinSustituto.Id, _barakaldo.Id, incluido: true),
            // Barakaldo ya lo desmarcó: Incluido = false, no lo exige y por tanto no se ofrece.
            new TipoDocumentoCentro(_archivadoYaExcluido.Id, _barakaldo.Id, incluido: false)]);
    }

    private TipoDocumento Tipo(string nombre, int orden, RequisitoDocumental requerido)
    {
        var tipo = new TipoDocumento(nombre, vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden, AmbitoAplicacion.Empresa, requerido);
        _tipos.ListaTiposDocumento.Add(tipo);
        return tipo;
    }

    private async Task<IReadOnlyList<DocumentacionRequeridaCentroDto>> DelCentro(Centro centro) =>
        (await new ObtenerDocumentacionRequeridaDeCentroQueryHandler(_centros, _tipos, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerDocumentacionRequeridaDeCentroQuery(centro.Id), CancellationToken.None))!;

    [Fact]
    public async Task Un_tipo_archivado_que_el_Centro_no_exige_no_se_devuelve()
    {
        var filas = await DelCentro(_getafe);

        filas.Select(f => f.TipoDocumentoId).Should().BeEquivalentTo(new[] { _rlc.Id },
            "Getafe no exige ningún archivado: solo se ofrece el tipo vigente (control positivo: la lista no está vacía)");
    }

    [Fact]
    public async Task Un_tipo_archivado_con_fila_de_Incluido_false_tampoco_se_devuelve()
    {
        var filas = await DelCentro(_barakaldo);

        filas.Should().NotContain(f => f.TipoDocumentoId == _archivadoYaExcluido.Id);
        filas.Should().NotContain(f => f.TipoDocumentoId == _reciboArchivadoNoExigido.Id);
    }

    [Fact]
    public async Task Un_tipo_archivado_que_el_Centro_ya_exige_se_devuelve_con_su_sustituto()
    {
        var filas = await DelCentro(_barakaldo);

        var recibo = filas.Should().ContainSingle(f => f.TipoDocumentoId == _reciboArchivadoYExigido.Id).Subject;
        recibo.Archivado.Should().BeTrue();
        recibo.Aplica.Should().BeTrue();
        recibo.Incluido.Should().BeTrue();
        recibo.NombreSustituto.Should().Be("RLC (TC1)");
    }

    [Fact]
    public async Task Un_tipo_archivado_exigido_sin_sustituto_se_devuelve_sin_nombre_de_sustituto()
    {
        var filas = await DelCentro(_barakaldo);

        var antiguo = filas.Should().ContainSingle(f => f.TipoDocumentoId == _archivadoSinSustituto.Id).Subject;
        antiguo.Archivado.Should().BeTrue();
        antiguo.NombreSustituto.Should().BeNull();
    }

    [Fact]
    public async Task Un_tipo_vigente_no_lleva_marca_de_archivado()
    {
        var filas = await DelCentro(_barakaldo);

        var rlc = filas.Should().ContainSingle(f => f.TipoDocumentoId == _rlc.Id).Subject;
        rlc.Archivado.Should().BeFalse();
        rlc.NombreSustituto.Should().BeNull();
    }

    [Fact]
    public async Task La_consulta_del_Cliente_empresarial_sigue_contando_lo_que_aplica_y_no_ofrece_lo_que_nadie_exige()
    {
        var porCliente = await new ObtenerDocumentacionExigidaDeClienteQueryHandler(_centros, _tipos, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerDocumentacionExigidaDeClienteQuery(_clienteEmpresarial.Id), CancellationToken.None);

        porCliente!.Requisitos.Select(r => (r.TipoDocumentoId, r.Centros)).Should().BeEquivalentTo(
            [
                (_rlc.Id, 2),
                (_reciboArchivadoYExigido.Id, 1),
                (_archivadoSinSustituto.Id, 1)
            ],
            "el archivado que Barakaldo exige sigue contando para el Cliente empresarial; los que ningún Centro exige no salen");
    }
}
