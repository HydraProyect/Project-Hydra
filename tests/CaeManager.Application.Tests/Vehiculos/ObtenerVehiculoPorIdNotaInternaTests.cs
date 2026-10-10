using CaeManager.Application.Tests.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Vehiculos.Commands.GuardarNotaInternaVehiculo;
using CaeManager.Domain.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Vehiculos;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Vehiculos;

/// <summary>
/// El corte de lectura de la «Nota interna» en <see cref="ObtenerVehiculoPorIdQueryHandler"/>. La página Vehículo 360
/// solo admite a los roles del equipo, pero la consulta responde por alcance y la envían más llamadores que la
/// página: el corte va en Application, porque lo que este handler no entrega no puede pintarlo nadie.
/// </summary>
public class ObtenerVehiculoPorIdNotaInternaTests
{
    private const string Nota = "Aparca en la nave 2. Las llaves las tiene Leire.";

    private sealed class VehiculosFalso(Vehiculo vehiculo) : IVehiculosQueryContext
    {
        public IQueryable<Vehiculo> Vehiculos => new TestAsyncQueryable<Vehiculo>(new[] { vehiculo }.AsQueryable());
    }

    private sealed class SinDocumentos : ICalculoEstadoDocumentalService
    {
        public Task<IReadOnlyList<EstadoDocumento>> CalcularEstadosDeDocumentosDeVehiculoAsync(Guid vehiculoId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EstadoDocumento>>([]);

        public Task<IReadOnlyDictionary<Guid, EstadoDocumento>> CalcularPeorEstadoAsync(
            AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, DesgloseDocumentalDto>> CalcularDesgloseAsync(
            AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static (ObtenerVehiculoPorIdQueryHandler Handler, Vehiculo Vehiculo) Montar(string? rol, string? nota = Nota)
    {
        var empresa = new Empresa("Montajes Skynet S.L.");
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);

        var vehiculo = Vehiculo.DeEmpresa(empresa.Id, "Camión grúa", "Iveco Daily", "9012 GHI");
        vehiculo.FijarNotaInterna(nota);

        var handler = new ObtenerVehiculoPorIdQueryHandler(
            empresas, new VehiculosFalso(vehiculo), new AlcanceDatosServiceFalso(vehiculoIdsVisibles: [vehiculo.Id]),
            new SinDocumentos(), PoliticaNotaInternaPruebas.Con(rol));

        return (handler, vehiculo);
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task El_equipo_recibe_la_nota(string rol)
    {
        var (handler, vehiculo) = Montar(rol);

        var dto = await handler.Handle(new ObtenerVehiculoPorIdQuery(vehiculo.Id), CancellationToken.None);

        dto.Should().NotBeNull();
        dto!.NotaInternaVisible.Should().BeTrue();
        dto.Notas.Should().Be(Nota);
    }

    [Fact]
    public async Task El_equipo_ve_la_tarjeta_aunque_el_vehiculo_no_tenga_nota()
    {
        var (handler, vehiculo) = Montar("GestorCae", nota: null);

        var dto = await handler.Handle(new ObtenerVehiculoPorIdQuery(vehiculo.Id), CancellationToken.None);

        dto!.NotaInternaVisible.Should().BeTrue();
        dto.Notas.Should().BeNull();
    }

    /// <summary>
    /// Con el vehículo en su alcance, quien no es del equipo recibe todo lo demás: lo único que no viaja es la nota,
    /// y el indicador le dice a quien pinte que no enseñe la tarjeta.
    /// </summary>
    [Theory]
    [InlineData("Cliente")]
    [InlineData("RolQueNoExiste")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Ni_el_usuario_de_Cliente_ni_un_rol_nulo_o_desconocido_reciben_la_nota(string? rol)
    {
        var (handler, vehiculo) = Montar(rol);

        var dto = await handler.Handle(new ObtenerVehiculoPorIdQuery(vehiculo.Id), CancellationToken.None);

        dto.Should().NotBeNull("la ficha sí se lee: el corte es solo de la nota");
        dto!.Nombre.Should().Be("Camión grúa");
        dto.NotaInternaVisible.Should().BeFalse();
        dto.Notas.Should().BeNull();
    }

    /// <summary>
    /// Quien puede guardar la nota del vehículo tiene que haberla visto: si un rol con escritura quedara fuera de la
    /// lista de lectura, el editor se abriría vacío y «Guardar» borraría una nota que nadie le enseñó. Se mide con
    /// el handler, rol a rol de la lista real de <c>AutorizacionEscrituraBehavior</c>: un rol de escritura nuevo que
    /// esta consulta no entregue pone el test en rojo, venga la lista de lectura de donde venga.
    /// </summary>
    [Fact]
    public async Task Todo_rol_que_puede_guardar_la_nota_del_vehiculo_la_recibe_al_leer()
    {
        var rolesConEscritura = AutorizacionEscrituraBehavior<GuardarNotaInternaVehiculoCommand, Result>.RolesConEscritura;
        rolesConEscritura.Should().NotBeEmpty("con la lista real vacía, el bucle de abajo no mediría nada");

        foreach (var rol in rolesConEscritura)
        {
            var (handler, vehiculo) = Montar(rol);

            var dto = await handler.Handle(new ObtenerVehiculoPorIdQuery(vehiculo.Id), CancellationToken.None);

            dto!.NotaInternaVisible.Should().BeTrue($"{rol} puede guardar la nota");
            dto.Notas.Should().Be(Nota);
        }
    }
}
