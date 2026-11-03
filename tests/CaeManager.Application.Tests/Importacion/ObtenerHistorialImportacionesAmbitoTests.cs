using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Queries.ObtenerHistorialImportaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Importacion;
using FluentAssertions;
using Xunit;
using ClientesFalsos = CaeManager.Application.Tests.Clientes;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>
/// El historial de importaciones se acota con la misma autoridad que las cargas masivas: el Administrador ve el del
/// Tenant entero, la autoridad acotada que alcanza el Tenant entero también, el Coordinador CAE (o la Dirección CAE) con
/// una lista de cartera solo las cargas que ejecutó él, y quien no carga no ve nada. Cada caso lleva su control: hay
/// siempre una carga propia y otra ajena, para que el test no pase por vaciar el historial.
///
/// Usa la implementación REAL de <see cref="IAutorizacionEscrituraEfectiva"/> con
/// <see cref="SesionPrivilegiadaAusente"/>, así el rol decide la autoridad igual que en producción.
/// </summary>
public class ObtenerHistorialImportacionesAmbitoTests
{
    private static readonly Guid Yo = Guid.NewGuid();
    private static readonly Guid OtroUsuario = Guid.NewGuid();

    private sealed class ContextoFalso : IImportacionQueryContext
    {
        public List<HistorialImportacion> Lista { get; } =
        [
            HistorialImportacion.Exito("Clientes", "mi-carga.xlsx", Yo, 3, 0, 1),
            HistorialImportacion.Exito("Combinada", "carga-ajena.xlsx", OtroUsuario, 40, 2, 0),
            HistorialImportacion.Fallo("Documentos", "fallo-ajeno.xlsx", OtroUsuario, "Error de otra carga."),
        ];

        public IQueryable<HistorialImportacion> HistorialImportaciones =>
            new TestAsyncQueryable<HistorialImportacion>(Lista.AsQueryable());
    }

    private sealed class TenantActualFalso : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private static async Task<IReadOnlyList<HistorialImportacionDto>> Consultar(
        string? rol, bool alcanzaElTenantEntero, Guid? usuarioId = null)
    {
        var usuario = new CurrentUserServiceFalso(usuarioId ?? Yo, rol);
        var autorizacion = new AutorizacionEscrituraEfectiva(usuario, new SesionPrivilegiadaAusente(), new TenantActualFalso());
        var alcance = new ClientesFalsos.AlcanceDatosServiceFalso(
            tieneAccesoTotal: alcanzaElTenantEntero, clienteIdsVisibles: [Guid.NewGuid()]);
        var handler = new ObtenerHistorialImportacionesQueryHandler(new ContextoFalso(), autorizacion, alcance, usuario);

        return await handler.Handle(new ObtenerHistorialImportacionesQuery(), CancellationToken.None);
    }

    [Fact]
    public async Task El_Administrador_ve_el_historial_del_Tenant_entero()
    {
        var historial = await Consultar("Administrador", alcanzaElTenantEntero: true);

        historial.Select(h => h.NombreArchivo).Should()
            .BeEquivalentTo(["mi-carga.xlsx", "carga-ajena.xlsx", "fallo-ajeno.xlsx"]);
    }

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("DireccionCae")]
    public async Task Con_una_lista_de_cartera_solo_se_ven_las_cargas_propias(string rol)
    {
        var historial = await Consultar(rol, alcanzaElTenantEntero: false);

        historial.Should().ContainSingle()
            .Which.NombreArchivo.Should().Be("mi-carga.xlsx", "las cargas de otros usuarios delatarían archivos y volúmenes fuera de su cartera");
        historial.Should().OnlyContain(h => h.EjecutadaPorUsuarioId == Yo);
    }

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("DireccionCae")]
    public async Task La_autoridad_acotada_que_alcanza_el_Tenant_entero_ve_el_historial_entero(string rol)
    {
        var historial = await Consultar(rol, alcanzaElTenantEntero: true);

        historial.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Sin_autoridad_de_carga_masiva_no_se_ve_nada(string? rol)
    {
        var historial = await Consultar(rol, alcanzaElTenantEntero: true);

        historial.Should().BeEmpty();
    }

    [Fact]
    public async Task La_autoridad_acotada_sin_usuario_identificado_no_ve_nada()
    {
        var historial = await Consultar("CoordinadorCae", alcanzaElTenantEntero: false, usuarioId: Guid.Empty);

        historial.Should().BeEmpty("sin usuario no hay cargas propias que enseñar, y el filtro falla cerrado");
    }
}
