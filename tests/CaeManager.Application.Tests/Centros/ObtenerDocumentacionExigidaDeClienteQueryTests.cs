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
/// Pestaña «Documentación» de Cliente 360: lo que el Cliente empresarial exige en sus Centros con gestión CAE, por
/// tipo de documento, en una sola consulta y con la misma regla que la pestaña «Requisitos» de Centro 360. Contextos
/// falsos, sin Postgres: lo que se prueba es la agregación y la Asignación de Cartera.
/// </summary>
public class ObtenerDocumentacionExigidaDeClienteQueryTests
{
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();

    private readonly Empresa _clienteEmpresarial = Empresa.CrearComoCliente("Refrielectric S.A.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
    private readonly Empresa _otroCliente = Empresa.CrearComoCliente("Otro Cliente S.A.", "B87654323", esCritico: false, notas: null, ejecutivoUsuarioId: null);
    private readonly Empresa _proveedora = new("Ibertec GmbH", "B12345674");
    private readonly Centro _barakaldo;
    private readonly Centro _getafe;
    private readonly Centro _sinGestion;
    private readonly Centro _deOtroCliente;
    private readonly TipoDocumento _seguro;
    private readonly TipoDocumento _formacion;
    private readonly TipoDocumento _epi;
    private readonly TipoDocumento _mutua;

    public ObtenerDocumentacionExigidaDeClienteQueryTests()
    {
        _barakaldo = new Centro(_clienteEmpresarial.Id, _proveedora.Id, "Planta Barakaldo");
        _getafe = new Centro(_clienteEmpresarial.Id, _proveedora.Id, "Almacén Getafe");
        _sinGestion = new Centro(_clienteEmpresarial.Id, _proveedora.Id, "Nave sin gestión");
        _sinGestion.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        _deOtroCliente = new Centro(_otroCliente.Id, _proveedora.Id, "Centro de otro Cliente");
        _centros.ListaCentros.AddRange([_barakaldo, _getafe, _sinGestion, _deOtroCliente]);

        // Catálogo: Trabajador antes que Empresa por orden, para comprobar que la Empresa sale primero.
        _formacion = Tipo("Formación PRL", 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.No);
        _epi = Tipo("EPI entregado", 2, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        _seguro = Tipo("Seguro RC", 3, AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
        _mutua = Tipo("Mutua", 4, AmbitoAplicacion.Empresa, RequisitoDocumental.No);

        _tipos.ListaTiposDocumentoCentros.AddRange([
            // Formación: sin criterio global, la pide solo Barakaldo.
            new TipoDocumentoCentro(_formacion.Id, _barakaldo.Id, incluido: true),
            // EPI: global, pero Getafe lo excluye.
            new TipoDocumentoCentro(_epi.Id, _getafe.Id, incluido: false),
            // Seguro: global en los dos; solo el segundo Centro bloquea el acceso.
            new TipoDocumentoCentro(_seguro.Id, _getafe.Id, incluido: true, bloqueaAcceso: true),
            // Mutua: solo la pide el Centro sin gestión CAE, que no exige nada; y el de otro Cliente, que no cuenta.
            new TipoDocumentoCentro(_mutua.Id, _sinGestion.Id, incluido: true, bloqueaAcceso: true),
            new TipoDocumentoCentro(_mutua.Id, _deOtroCliente.Id, incluido: true, bloqueaAcceso: true)]);
    }

    private TipoDocumento Tipo(string nombre, int orden, AmbitoAplicacion ambito, RequisitoDocumental requerido)
    {
        var tipo = new TipoDocumento(nombre, vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden, ambito, requerido);
        _tipos.ListaTiposDocumento.Add(tipo);
        return tipo;
    }

    private Task<DocumentacionExigidaClienteDto?> Obtener(AlcanceDatosServiceFalso alcance) =>
        new ObtenerDocumentacionExigidaDeClienteQueryHandler(_centros, _tipos, alcance)
            .Handle(new ObtenerDocumentacionExigidaDeClienteQuery(_clienteEmpresarial.Id), CancellationToken.None);

    [Fact]
    public async Task Agrega_por_tipo_los_Centros_con_gestion_CAE_y_bloquea_si_lo_hace_el_segundo_Centro()
    {
        var resultado = await Obtener(new AlcanceDatosServiceFalso());

        resultado.Should().NotBeNull();
        resultado!.CentrosConGestionCae.Should().Be(2, "el Centro sin gestión CAE y el de otro Cliente empresarial no cuentan");
        resultado.Requisitos.Should().Equal(
            [
                new RequisitoExigidoClienteDto(_seguro.Id, "Seguro RC", AmbitoAplicacion.Empresa, 2, BloqueaAcceso: true),
                new RequisitoExigidoClienteDto(_formacion.Id, "Formación PRL", AmbitoAplicacion.Trabajador, 1, BloqueaAcceso: false),
                new RequisitoExigidoClienteDto(_epi.Id, "EPI entregado", AmbitoAplicacion.Trabajador, 1, BloqueaAcceso: false)
            ],
            "primero lo de la Empresa y después lo de sus Trabajadores, en el orden del catálogo; la Mutua no la pide ningún Centro con gestión CAE");
    }

    [Fact]
    public async Task Cada_Centro_cuenta_lo_mismo_que_su_pestana_Requisitos()
    {
        var alcance = new AlcanceDatosServiceFalso();
        var porCliente = (await Obtener(alcance))!.Requisitos;

        foreach (var centro in new[] { _barakaldo, _getafe })
        {
            var delCentro = await new ObtenerDocumentacionRequeridaDeCentroQueryHandler(_centros, _tipos, alcance)
                .Handle(new ObtenerDocumentacionRequeridaDeCentroQuery(centro.Id), CancellationToken.None);
            foreach (var requisito in delCentro!.Where(r => r.Aplica))
                porCliente.Should().Contain(r => r.TipoDocumentoId == requisito.TipoDocumentoId,
                    $"«{requisito.Nombre}» aplica en {centro.Nombre} y la pestaña del Cliente tiene que mostrarlo");
        }
    }

    [Fact]
    public async Task Un_usuario_sin_el_Cliente_empresarial_en_cartera_no_recibe_nada()
    {
        var fueraDeCartera = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [_otroCliente.Id], centroIdsVisibles: [_deOtroCliente.Id]);

        var resultado = await Obtener(fueraDeCartera);

        resultado.Should().BeNull("un Cliente empresarial fuera de cartera se trata como si no existiera");
    }

    [Fact]
    public async Task Con_el_Cliente_en_cartera_solo_cuentan_los_Centros_visibles()
    {
        var soloBarakaldo = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [_clienteEmpresarial.Id], centroIdsVisibles: [_barakaldo.Id]);

        var resultado = await Obtener(soloBarakaldo);

        resultado!.CentrosConGestionCae.Should().Be(1);
        resultado.Requisitos.Should().OnlyContain(r => r.Centros == 1);
        resultado.Requisitos.Single(r => r.TipoDocumentoId == _seguro.Id).BloqueaAcceso
            .Should().BeFalse("el bloqueo es de Getafe, que queda fuera de la cartera");
    }
}
