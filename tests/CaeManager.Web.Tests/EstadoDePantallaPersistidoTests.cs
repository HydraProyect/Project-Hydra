using Bunit;
using CaeManager.Application.Common;
using CaeManager.Web.Components.EstadoPersistido;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El patrón de estado persistido (<see cref="EstadoDePantallaPersistido{T}"/>)
/// con el gestor de persistencia REAL de Blazor: dos ámbitos, como en
/// producción — el del prerender guarda y serializa; el del circuito restaura
/// a partir de lo serializado y pregunta. Lo que se prueba es cuándo el
/// circuito NO debe recoger lo que dejó el prerender: otra sesión, otro Tenant
/// propietario, otras coordenadas de workspace, otra consulta, o un estado
/// vencido. En cada uno de esos casos la respuesta correcta es «nada» (la
/// pantalla consulta de nuevo), nunca «el estado ajeno».
/// </summary>
public class EstadoDePantallaPersistidoTests
{
    private sealed record Instantanea(string Texto, int Total);

    public sealed record Sesion(
        Guid? UsuarioId, Guid? TenantActual, Guid? TenantOrigen, string? Rol,
        Guid? AsignacionOperacion = null, Guid? SesionPrivilegiada = null)
    {
        public static readonly Guid Usuario = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        public static readonly Guid TenantX = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
        public static readonly Guid TenantY = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

        public static Sesion Base => new(Usuario, TenantX, TenantX, "GestorCae");
    }

    private sealed class Reloj : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; } = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Ahora;
    }

    private sealed class AlmacenEnMemoria : IPersistentComponentStateStore
    {
        public Dictionary<string, byte[]> Contenido { get; private set; } = [];

        public Task<IDictionary<string, byte[]>> GetPersistedStateAsync() =>
            Task.FromResult<IDictionary<string, byte[]>>(new Dictionary<string, byte[]>(Contenido));

        public Task PersistStateAsync(IReadOnlyDictionary<string, byte[]> state)
        {
            Contenido = new Dictionary<string, byte[]>(state);
            return Task.CompletedTask;
        }
    }

    private sealed class SesionFalsa(Sesion sesion) : ICurrentUserService, ITenantActual, IClienteActivoSeleccionado
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(sesion.UsuarioId);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult(sesion.Rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(sesion.TenantOrigen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
        public Guid? TenantId => sesion.TenantActual;
        public Guid? TenantIdSeleccionado => sesion.TenantActual;
        public Guid? AsignacionOperacionIdSeleccionada => sesion.AsignacionOperacion;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesion.SesionPrivilegiada;
    }

    private static HuellaDeSesion Huella(Sesion sesion)
    {
        var falsa = new SesionFalsa(sesion);
        return new HuellaDeSesion(falsa, falsa, falsa);
    }

    private const string Clave = "pantalla";
    private const string HuellaConsulta = "b=|e=|p=1|n=20";

    /// <summary>
    /// Prerender: guarda <paramref name="instantanea"/> con la sesión
    /// <paramref name="delPrerender"/> y lo serializa. Devuelve el almacén,
    /// que es lo que viajaría al navegador y volvería al circuito.
    /// </summary>
    private static async Task<AlmacenEnMemoria> PrerenderAsync(
        Sesion delPrerender, Reloj reloj, Instantanea? instantanea = null, string huellaConsulta = HuellaConsulta)
    {
        using var contexto = new BunitContext();
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var fabrica = new FabricaEstadoDePantallaPersistido(gestor.State, Huella(delPrerender), reloj);
        using var estado = fabrica.Crear<Instantanea>(Clave);
        if (instantanea is not null)
            estado.Guardar(huellaConsulta, instantanea);

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, contexto.Renderer);
        return almacen;
    }

    private static async Task<Instantanea?> CircuitoAsync(
        AlmacenEnMemoria almacen, Sesion delCircuito, Reloj reloj, string huellaConsulta = HuellaConsulta)
    {
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        await gestor.RestoreStateAsync(almacen);
        var fabrica = new FabricaEstadoDePantallaPersistido(gestor.State, Huella(delCircuito), reloj);
        using var estado = fabrica.Crear<Instantanea>(Clave);
        return await estado.TomarAsync(huellaConsulta);
    }

    [Fact]
    public async Task Misma_sesion_misma_consulta_y_en_plazo_el_circuito_recoge_lo_que_guardo_el_prerender()
    {
        var reloj = new Reloj();
        var almacen = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("filas", 3));

        var recogida = await CircuitoAsync(almacen, Sesion.Base, reloj);

        recogida.Should().Be(new Instantanea("filas", 3));
    }

    [Fact]
    public async Task Sin_nada_guardado_no_se_persiste_nada()
    {
        var almacen = await PrerenderAsync(Sesion.Base, new Reloj(), instantanea: null);

        almacen.Contenido.Should().BeEmpty();
        (await CircuitoAsync(almacen, Sesion.Base, new Reloj())).Should().BeNull();
    }

    // ── Tenant y sesión: lo persistido no lo recoge otra sesión ─────────────────

    public static TheoryData<string, Sesion> SesionesQueNoCoinciden => new()
    {
        { "otro usuario", Sesion.Base with { UsuarioId = Guid.Parse("00000000-0000-0000-0000-0000000000a2") } },
        { "otro Tenant propietario en el workspace activo", Sesion.Base with { TenantActual = Sesion.TenantY } },
        { "otro Tenant de origen", Sesion.Base with { TenantOrigen = Sesion.TenantY } },
        { "otro rol", Sesion.Base with { Rol = "Consulta" } },
        { "otra autorización de operación", Sesion.Base with { AsignacionOperacion = Guid.NewGuid() } },
        { "otra sesión privilegiada", Sesion.Base with { SesionPrivilegiada = Guid.NewGuid() } },
    };

    [Theory]
    [MemberData(nameof(SesionesQueNoCoinciden))]
    public async Task Una_sesion_distinta_a_la_del_prerender_no_recoge_su_estado(string caso, Sesion delCircuito)
    {
        var reloj = new Reloj();
        var almacen = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("filas del Tenant X", 3));

        var recogida = await CircuitoAsync(almacen, delCircuito, reloj);

        recogida.Should().BeNull($"«{caso}»: el estado del prerender no vale para otra sesión");
    }

    [Fact]
    public async Task El_workspace_que_se_resuelve_a_nulo_en_el_circuito_no_recoge_el_estado_del_workspace_delegado()
    {
        // El punto frágil documentado en TenantActual: si en el circuito la
        // selección de workspace se memoizara a nulo, el Tenant actual sería
        // el de origen y no el del workspace del prerender.
        var reloj = new Reloj();
        var delegado = Sesion.Base with { TenantActual = Sesion.TenantY };
        var almacen = await PrerenderAsync(delegado, reloj, new Instantanea("filas del workspace delegado Y", 5));

        var recogida = await CircuitoAsync(almacen, Sesion.Base, reloj);

        recogida.Should().BeNull();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Sin_usuario_o_sin_tenant_no_se_persiste_ni_se_recoge(bool sinUsuario, bool sinTenant)
    {
        var reloj = new Reloj();
        var anonima = Sesion.Base with
        {
            UsuarioId = sinUsuario ? null : Sesion.Usuario,
            TenantActual = sinTenant ? null : Sesion.TenantX,
        };

        var almacen = await PrerenderAsync(anonima, reloj, new Instantanea("filas", 1));

        almacen.Contenido.Should().BeEmpty("sin identidad resuelta no se persiste nada");
    }

    [Fact]
    public async Task Si_la_sesion_del_circuito_no_tiene_identidad_no_recoge_nada()
    {
        var reloj = new Reloj();
        var almacen = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("filas", 3));

        var recogida = await CircuitoAsync(almacen, Sesion.Base with { UsuarioId = null }, reloj);

        recogida.Should().BeNull();
    }

    // ── Consulta: otra pregunta no se contesta con la respuesta anterior ────────

    [Fact]
    public async Task Otra_consulta_otros_filtros_pagina_o_tamano_no_recoge_el_estado()
    {
        var reloj = new Reloj();
        var almacen = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("filas", 3));

        (await CircuitoAsync(almacen, Sesion.Base, reloj, huellaConsulta: "b=acme|e=|p=1|n=20")).Should().BeNull();
        (await CircuitoAsync(almacen, Sesion.Base, reloj, huellaConsulta: "b=|e=|p=2|n=20")).Should().BeNull();
        (await CircuitoAsync(almacen, Sesion.Base, reloj, huellaConsulta: "b=|e=|p=1|n=50")).Should().BeNull();
    }

    // ── Caducidad ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_estado_mas_viejo_que_la_vigencia_maxima_se_descarta()
    {
        var reloj = new Reloj();
        var almacen = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("filas", 3));

        reloj.Ahora += FabricaEstadoDePantallaPersistido.VigenciaMaxima + TimeSpan.FromSeconds(1);

        (await CircuitoAsync(almacen, Sesion.Base, reloj)).Should().BeNull();
    }

    [Fact]
    public async Task Un_estado_justo_dentro_de_la_vigencia_maxima_se_recoge()
    {
        var reloj = new Reloj();
        var almacen = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("filas", 3));

        reloj.Ahora += FabricaEstadoDePantallaPersistido.VigenciaMaxima;

        (await CircuitoAsync(almacen, Sesion.Base, reloj)).Should().NotBeNull();
    }

    [Fact]
    public async Task Un_estado_fechado_en_el_futuro_se_descarta()
    {
        // Un instante posterior al de ahora no puede venir de este servidor
        // hace un momento: descartarlo cierra la puerta a un estado reciclado
        // con el reloj atrasado.
        var reloj = new Reloj();
        var almacen = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("filas", 3));

        reloj.Ahora -= TimeSpan.FromMinutes(5);

        (await CircuitoAsync(almacen, Sesion.Base, reloj)).Should().BeNull();
    }

    [Fact]
    public async Task El_instante_es_el_de_la_consulta_no_el_de_la_persistencia()
    {
        var reloj = new Reloj();
        using var contexto = new BunitContext();
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var fabrica = new FabricaEstadoDePantallaPersistido(gestor.State, Huella(Sesion.Base), reloj);
        using var estado = fabrica.Crear<Instantanea>(Clave);
        estado.Guardar(HuellaConsulta, new Instantanea("filas", 3));

        // El render tarda: la persistencia ocurre 45 s después de consultar.
        reloj.Ahora += TimeSpan.FromSeconds(45);
        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, contexto.Renderer);

        // 20 s después de persistir el estado tiene 65 s desde la consulta.
        reloj.Ahora += TimeSpan.FromSeconds(20);

        (await CircuitoAsync(almacen, Sesion.Base, reloj)).Should().BeNull();
    }

    // ── Qué viaja ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Solo_viaja_la_instantanea_guardada_con_su_huella_y_su_instante()
    {
        var reloj = new Reloj();
        var almacen = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("solo-esto", 7));

        var texto = System.Text.Encoding.UTF8.GetString(almacen.Contenido.Single().Value);
        using var json = System.Text.Json.JsonDocument.Parse(texto);

        // El serializador del estado persistido escribe en camelCase.
        json.RootElement.EnumerateObject().Select(p => p.Name.ToLowerInvariant())
            .Should().BeEquivalentTo(["huelladesesion", "huellaconsulta", "persistidoen", "datos"]);
        json.RootElement.GetProperty("datos").GetRawText().Should().Contain("solo-esto");
    }
}
