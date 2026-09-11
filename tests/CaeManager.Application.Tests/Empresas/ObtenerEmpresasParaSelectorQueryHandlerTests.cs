using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// Defecto detectado el 2026-09-11: el selector devolvía TODAS las Empresas
/// propias del tenant sin aplicar el alcance del usuario, a diferencia de su
/// hermano <c>ObtenerClientesParaSelectorQuery</c>. Un Gestor CAE con cartera
/// limitada veía en selectores y paneles (alta de Vehículo/Trabajador/
/// Documento, vínculo de Subcontrata, generación/reclamación de
/// documentación) los nombres de Empresas propias fuera de su cartera.
/// </summary>
public class ObtenerEmpresasParaSelectorQueryHandlerTests
{
    private readonly EmpresasQueryContextFalso _empresas = new();

    private readonly Empresa _empresaEnCartera = new("Ibertec Sevilla S.A.", "B12345674");
    private readonly Empresa _empresaAjena = new("Ibertec Bilbao S.L.", "B87654323");

    public ObtenerEmpresasParaSelectorQueryHandlerTests()
    {
        _empresas.ListaEmpresas.AddRange([_empresaEnCartera, _empresaAjena]);
    }

    [Fact]
    public async Task Sin_restriccion_de_cartera_devuelve_todas_las_Empresas_propias()
    {
        var resultado = await HandleAsync(new AlcanceDatosServiceFalso());

        resultado.Select(e => e.RazonSocial).Should().BeEquivalentTo([_empresaEnCartera.RazonSocial, _empresaAjena.RazonSocial]);
    }

    [Fact]
    public async Task Con_cartera_restringida_solo_devuelve_las_Empresas_gestionables()
    {
        var alcance = AlcanceRestringidoA(_empresaEnCartera.Id);

        var resultado = await HandleAsync(alcance);

        resultado.Should().ContainSingle().Which.RazonSocial.Should().Be(_empresaEnCartera.RazonSocial);
    }

    [Fact]
    public async Task Con_cartera_vacia_no_devuelve_ninguna_Empresa()
    {
        var alcance = AlcanceRestringidoA();

        var resultado = await HandleAsync(alcance);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task El_rol_Cliente_de_portal_no_ve_ninguna_Empresa_aunque_su_cartera_de_lectura_no_este_vacia()
    {
        // ObtenerEmpresaIdsParaGestionAsync es la variante de GESTIÓN: un
        // usuario de portal (rol Cliente) puede tener Empresas visibles para
        // LEER su documentación, pero el selector alimenta formularios de
        // alta/edición internos — nunca debe ofrecerle ninguna.
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false,
            empresaIdsVisibles: [_empresaEnCartera.Id, _empresaAjena.Id],
            empresaIdsParaGestion: []);

        var resultado = await HandleAsync(alcance);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task El_filtro_por_ClienteId_se_combina_con_el_alcance_sin_ensanchar_la_cartera()
    {
        // Ambas Empresas prestan servicio al mismo Cliente, pero solo una está
        // en la cartera del usuario — el selector no debe ofrecer la otra
        // aunque el vínculo con el Cliente exista.
        var clienteId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        _empresas.ListaRelacionesEmpresariales.AddRange([
            RelacionEmpresarial.Crear(_empresaEnCartera.Id, clienteId, ahora),
            RelacionEmpresarial.Crear(_empresaAjena.Id, clienteId, ahora)
        ]);

        var alcance = AlcanceRestringidoA(_empresaEnCartera.Id);

        var resultado = await HandleAsync(alcance, clienteId);

        resultado.Should().ContainSingle().Which.RazonSocial.Should().Be(_empresaEnCartera.RazonSocial);
    }

    private AlcanceDatosServiceFalso AlcanceRestringidoA(params Guid[] empresaIdsGestionables) => new(
        tieneAccesoTotal: false,
        empresaIdsVisibles: empresaIdsGestionables,
        empresaIdsParaGestion: empresaIdsGestionables);

    private Task<IReadOnlyList<EmpresaSelectorDto>> HandleAsync(AlcanceDatosServiceFalso alcance, Guid? clienteId = null) =>
        new ObtenerEmpresasParaSelectorQueryHandler(_empresas, alcance)
            .Handle(new ObtenerEmpresasParaSelectorQuery(clienteId), CancellationToken.None);
}
