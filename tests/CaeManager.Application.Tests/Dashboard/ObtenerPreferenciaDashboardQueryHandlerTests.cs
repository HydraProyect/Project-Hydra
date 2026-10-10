using CaeManager.Application.Dashboard.Catalogo;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Configuracion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// Una preferencia guardada es un contrato persistido: guarda códigos de KPI, y el
/// catálogo puede retirar uno después. <c>"ope.ocupacion-gestores"</c> (ocupación por
/// Gestor CAE) se retiró con preferencias ya guardadas que lo contienen; el Dashboard
/// tiene que seguir cargándolas, sin el código retirado y sin lanzar.
/// </summary>
public class ObtenerPreferenciaDashboardQueryHandlerTests
{
    private const string CodigoRetirado = "ope.ocupacion-gestores";

    private sealed class RepositorioFalso(PreferenciaDashboardUsuario? preferencia) : IPreferenciaDashboardUsuarioRepository
    {
        public Task<PreferenciaDashboardUsuario?> ObtenerPorUsuarioIdAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(preferencia?.UsuarioId == usuarioId ? preferencia : null);

        public void Agregar(PreferenciaDashboardUsuario preferencia) => throw new NotSupportedException("una lectura no escribe");
    }

    private static Task<IReadOnlyList<string>> Cargar(Guid usuarioId, string? rol, params string[] codigosGuardados) =>
        new ObtenerPreferenciaDashboardQueryHandler(
                new CurrentUserServiceFalso(usuarioId, rol),
                new RepositorioFalso(new PreferenciaDashboardUsuario(usuarioId, codigosGuardados)))
            .Handle(new ObtenerPreferenciaDashboardQuery(), CancellationToken.None);

    [Fact]
    public async Task Una_preferencia_guardada_con_el_KPI_de_ocupacion_carga_sin_el_y_conserva_el_resto_en_orden()
    {
        var usuario = Guid.NewGuid();

        var seleccion = await Cargar(usuario, "CoordinadorCae",
            CatalogoKpis.HorasPorCliente, CodigoRetirado, CatalogoKpis.PalancaIa);

        seleccion.Should().Equal(CatalogoKpis.HorasPorCliente, CatalogoKpis.PalancaIa);
    }

    [Fact]
    public async Task Una_preferencia_que_solo_tenia_el_KPI_de_ocupacion_cae_en_el_conjunto_por_defecto_del_rol()
    {
        var usuario = Guid.NewGuid();

        var seleccion = await Cargar(usuario, "DireccionCae", CodigoRetirado);

        seleccion.Should().Equal(CatalogoKpis.KpisPorDefectoPorRol("DireccionCae"),
            "una selección que se queda vacía al filtrar no puede dejar el Dashboard en blanco");
        seleccion.Should().NotContain(CodigoRetirado);
    }

    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData(null)]
    public void El_codigo_retirado_no_esta_en_el_catalogo_ni_en_ningun_conjunto_por_defecto(string? rol)
    {
        CatalogoKpis.Todos.Select(k => k.Codigo).Should().NotContain(CodigoRetirado,
            "reutilizar el código resucitaría, con otro significado, las preferencias que aún lo guardan");
        CatalogoKpis.KpisPorDefectoPorRol(rol).Should().NotContain(CodigoRetirado);
        CatalogoKpis.KpisPorDefectoPorRol(rol).Should().OnlyContain(c => CatalogoKpis.Todos.Any(k => k.Codigo == c),
            "un conjunto por defecto que nombra un KPI inexistente se filtraría en silencio en la página");
    }
}
