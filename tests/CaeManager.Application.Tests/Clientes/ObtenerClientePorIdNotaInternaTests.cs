using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Empresas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Clientes;

/// <summary>
/// La «Nota interna» de un Cliente empresarial solo viaja a quien opera desde
/// el lado de gestión. El Usuario de Cliente (rol Cliente) tiene a su propio
/// Cliente empresarial en el alcance de lectura: recibe la ficha, no la nota.
/// Qué responde el servicio de alcance real para cada rol lo mide
/// <c>NotaInternaDeClientePorRolTests</c> (integración).
/// </summary>
public class ObtenerClientePorIdNotaInternaTests
{
    private const string Nota = "Nota interna de prueba";

    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly Empresa _cliente = Empresa.CrearComoCliente("Cliente Repro S.L.", "B10380392", false, Nota, null);

    public ObtenerClientePorIdNotaInternaTests() => _empresas.ListaEmpresas.Add(_cliente);

    [Fact]
    public async Task El_usuario_de_portal_recibe_la_ficha_de_su_Cliente_sin_la_nota_interna()
    {
        var portal = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [_cliente.Id], ladoDeGestion: false);

        var detalle = await HandleAsync(portal);

        detalle.Should().NotBeNull("su propio Cliente empresarial está en su alcance de lectura");
        detalle!.RazonSocial.Should().Be("Cliente Repro S.L.");
        detalle.Notas.Should().BeNull();
    }

    [Fact]
    public async Task El_lado_de_gestion_recibe_la_nota_interna()
    {
        var gestion = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_cliente.Id]);

        (await HandleAsync(gestion))!.Notas.Should().Be(Nota);
    }

    [Fact]
    public async Task Fuera_del_alcance_de_lectura_no_hay_ficha_aunque_se_opere_desde_gestion()
    {
        var sinCartera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: []);

        (await HandleAsync(sinCartera)).Should().BeNull();
    }

    private Task<ClienteDetalleDto?> HandleAsync(AlcanceDatosServiceFalso alcance) =>
        new ObtenerClientePorIdQueryHandler(_empresas, alcance)
            .Handle(new ObtenerClientePorIdQuery(_cliente.Id), CancellationToken.None);
}
