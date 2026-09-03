using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

public class ObtenerClientesDeEmpresaQueryTests
{
    [Fact]
    public async Task Devuelve_los_clientes_de_una_empresa_dentro_de_la_cartera_de_gestion()
    {
        var contratista = new Empresa("Contrata propia S.L.");
        var cliente = Empresa.CrearComoCliente("Cliente real S.L.", "B00000000", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var contexto = new EmpresasQueryContextFalso();
        contexto.ListaEmpresas.Add(cliente);
        contexto.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(contratista.Id, cliente.Id, DateTime.UtcNow));

        var handler = new ObtenerClientesDeEmpresaQueryHandler(
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [contratista.Id]));

        var resultado = await handler.Handle(new ObtenerClientesDeEmpresaQuery(contratista.Id), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(cliente.Id);
    }

    /// <summary>
    /// REC-153: la contratista está en la cartera de LECTURA del usuario de
    /// portal (rol Cliente) — es justo lo que hace que el portal le enseñe su
    /// documentación — pero no en su cartera de GESTIÓN. Antes del arreglo esta
    /// consulta usaba la cartera de lectura como puerta, y ese mismo usuario
    /// veía la cartera COMERCIAL completa de la contratista: qué otros
    /// Clientes tiene, no documentación del propio Cliente.
    /// </summary>
    [Fact]
    public async Task Usuario_de_portal_no_ve_la_cartera_comercial_de_una_contratista_de_su_cliente()
    {
        var contratista = new Empresa("Contrata ajena S.L.");
        var otroCliente = Empresa.CrearComoCliente("Otro cliente de la contratista S.L.", "B00000000", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var contexto = new EmpresasQueryContextFalso();
        contexto.ListaEmpresas.Add(otroCliente);
        contexto.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(contratista.Id, otroCliente.Id, DateTime.UtcNow));

        var handler = new ObtenerClientesDeEmpresaQueryHandler(
            contexto,
            new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false,
                empresaIdsVisibles: [contratista.Id],
                empresaIdsParaGestion: []));

        var resultado = await handler.Handle(new ObtenerClientesDeEmpresaQuery(contratista.Id), CancellationToken.None);

        resultado.Should().BeEmpty();
    }
}
