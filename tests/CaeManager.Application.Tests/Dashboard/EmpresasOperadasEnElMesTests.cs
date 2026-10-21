using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// «Facturación» solo lista las Empresas que el Operador CAE opera con una Asignación de Operación EXTERNA en vigor en el
/// mes (<see cref="ObtenerEmpresasGestionadasDelMesQuery"/>, la única fuente de vigencia, la misma que la valoración del Cuadro).
/// Escenario: A, externa desde el 15-sep; E, desde el 10-oct; F, cerrada el 20-sep; B, solo Programada; C, fila raíz; el
/// propio Operador, con una Asignación interna sobre sí mismo; y otro Operador sobre G.
/// </summary>
public class EmpresasOperadasEnElMesTests
{
    private static readonly DateTime Enero = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime QuinceSep = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime VeinteSep = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DiezOct = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly Guid E = Guid.NewGuid();
    private static readonly Guid F = Guid.NewGuid();
    private static readonly Guid G = Guid.NewGuid();

    private static async Task<IReadOnlyList<Guid>> PedirAsync(int anyo, int mes, string rol = "DireccionCae", Guid? origen = null)
    {
        var handler = new ObtenerEmpresasGestionadasDelMesQueryHandler(new Usuario(rol, origen ?? Operador), new OperacionesFalsas());
        return (await handler.Handle(new ObtenerEmpresasGestionadasDelMesQuery(anyo, mes), default)).Tramos.Keys.ToList();
    }

    [Fact]
    public async Task Septiembre_son_las_vigentes_en_algun_trozo_del_mes_incluida_la_que_se_cerro_dentro_de_el()
    {
        var empresas = await PedirAsync(2026, 9);

        empresas.Should().BeEquivalentTo([A, F]);
    }

    [Fact]
    public async Task Octubre_entra_la_que_empieza_a_mitad_de_mes_y_sale_la_cerrada_antes()
    {
        var empresas = await PedirAsync(2026, 10);

        empresas.Should().BeEquivalentTo([A, E]);
    }

    [Fact]
    public async Task Nunca_salen_la_Programada_la_raiz_el_Tenant_del_propio_Operador_ni_uno_que_opera_otra_organizacion()
    {
        foreach (var mes in new[] { 9, 10 })
        {
            var empresas = await PedirAsync(2026, mes);

            empresas.Should().NotContain([B, C, Operador, G]);
        }
    }

    [Fact]
    public async Task El_Coordinador_CAE_tambien_la_lee_porque_no_lleva_importes_y_un_mes_no_valido_da_lista_vacia()
    {
        (await PedirAsync(2026, 10, rol: "CoordinadorCae")).Should().BeEquivalentTo([A, E]);
        (await PedirAsync(1900, 1)).Should().BeEmpty();
        (await PedirAsync(2026, 13)).Should().BeEmpty();
    }

    [Fact]
    public async Task Quien_no_lee_el_Cuadro_de_direccion_no_obtiene_la_lista()
    {
        var accion = () => PedirAsync(2026, 10, rol: "GestorCae");

        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private sealed class OperacionesFalsas : IOperacionesQueryContext
    {
        private readonly List<AsignacionOperacion> _asignaciones;

        public OperacionesFalsas()
        {
            var raizIncoherente = AsignacionOperacion.Externa(C, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Enero, null, Enero);
            typeof(AsignacionOperacion).GetProperty(nameof(AsignacionOperacion.EsRaiz))!.SetValue(raizIncoherente, true);

            var cerrada = AsignacionOperacion.Externa(F, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Enero, VeinteSep, Enero);

            _asignaciones =
            [
                AsignacionOperacion.Externa(A, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, QuinceSep, null, QuinceSep),
                AsignacionOperacion.Externa(E, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, DiezOct, null, DiezOct),
                cerrada,
                // Programada: se creó antes de su inicio y nunca se activó, aunque su inicio ya pasó.
                AsignacionOperacion.Externa(B, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Enero, null, Enero.AddDays(-1)),
                raizIncoherente,
                AsignacionOperacion.Interna(Operador, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Enero, null, Enero),
                AsignacionOperacion.Externa(G, Guid.NewGuid(), ServicioCae.Outbound, AmbitoAsignacion.Universal, Enero, null, Enero),
            ];
        }

        public IQueryable<AsignacionOperacion> AsignacionesOperacion => new TestAsyncQueryable<AsignacionOperacion>(_asignaciones.AsQueryable());

        public IQueryable<AsignacionCartera> AsignacionesCartera => new TestAsyncQueryable<AsignacionCartera>(new List<AsignacionCartera>().AsQueryable());
    }

    private sealed class Usuario(string rol, Guid origen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());

        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);

        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();

        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(origen);

        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
