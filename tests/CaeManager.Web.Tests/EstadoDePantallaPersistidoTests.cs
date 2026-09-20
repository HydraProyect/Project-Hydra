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
        Guid? AsignacionOperacion = null, Guid? SesionPrivilegiada = null,
        Guid? TenantSeleccionado = null)
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

    private sealed class SesionFalsa(Func<Sesion> sesionActual) : ICurrentUserService, ITenantActual, IClienteActivoSeleccionado
    {
        private Sesion Sesion => sesionActual();
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(Sesion.UsuarioId);
        // Con un workspace seleccionado el rol real sale de una consulta a la
        // cartera sobre el DbContext del ámbito, que la huella no puede lanzar
        // mientras el selector del layout usa el mismo: aquí, si se pregunta,
        // el test lo dice.
        public Task<string?> ObtenerRolActualAsync() => Sesion.TenantSeleccionado is null
            ? Task.FromResult(Sesion.Rol)
            : throw new InvalidOperationException("La huella no debe preguntar el rol con un workspace seleccionado (consulta la base).");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(Sesion.TenantOrigen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
        public Guid? TenantId => Sesion.TenantActual;
        public Guid? TenantIdSeleccionado => Sesion.TenantSeleccionado;
        public Guid? AsignacionOperacionIdSeleccionada => Sesion.AsignacionOperacion;
        public Guid? SesionPrivilegiadaIdSeleccionada => Sesion.SesionPrivilegiada;
    }

    private static HuellaDeSesion Huella(Sesion sesion) => Huella(() => sesion);

    private static HuellaDeSesion Huella(Func<Sesion> sesionActual)
    {
        var falsa = new SesionFalsa(sesionActual);
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
            await estado.GuardarAsync(await estado.EmpezarConsultaAsync(sePersiste: true), huellaConsulta, instantanea);

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
        { "otro workspace seleccionado", Sesion.Base with { TenantSeleccionado = Sesion.TenantY } },
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
    public async Task Con_un_workspace_seleccionado_la_huella_no_consulta_el_rol_y_recoge_con_la_misma_seleccion()
    {
        // Regresión medida en CI (E2E SeleccionSobreviveAlCircuito y
        // FlujoSoporte): con un workspace delegado, ObtenerRolActualAsync
        // consulta la cartera sobre el DbContext del ámbito, en paralelo con el
        // selector de workspace del layout: «A second operation was started on
        // this context instance». La SesionFalsa lanza si se le pregunta el rol.
        var reloj = new Reloj();
        var delegado = Sesion.Base with
        {
            TenantActual = Sesion.TenantY,
            TenantSeleccionado = Sesion.TenantY,
            AsignacionOperacion = Guid.NewGuid(),
        };
        var almacen = await PrerenderAsync(delegado, reloj, new Instantanea("filas del workspace delegado Y", 5));

        almacen.Contenido.Should().NotBeEmpty("el prerender bajo un workspace delegado sigue persistiendo");
        (await CircuitoAsync(almacen, delegado, reloj)).Should().NotBeNull();
    }

    [Fact]
    public async Task Con_un_ambito_de_tenant_explicito_no_se_persiste_ni_se_recoge()
    {
        var reloj = new Reloj();
        var almacen = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("filas", 1));

        using (AmbitoTenantExplicito.Establecer(Sesion.TenantY))
        {
            (await Huella(Sesion.Base).ObtenerAsync()).Should().BeNull();
            (await CircuitoAsync(almacen, Sesion.Base, reloj)).Should().BeNull();
        }
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

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Si_la_sesion_del_circuito_no_tiene_identidad_completa_no_recoge_nada(bool sinUsuario, bool sinTenant)
    {
        var reloj = new Reloj();
        var almacen = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("filas", 3));

        var sinIdentidad = Sesion.Base with
        {
            UsuarioId = sinUsuario ? null : Sesion.Usuario,
            TenantActual = sinTenant ? null : Sesion.TenantX,
        };

        var recogida = await CircuitoAsync(almacen, sinIdentidad, reloj);

        recogida.Should().BeNull();
    }

    [Fact]
    public async Task Una_consulta_que_empieza_bajo_un_contexto_y_vuelve_bajo_otro_no_se_persiste_con_ninguno()
    {
        // Una consulta que empezó bajo el Tenant X y cuyo contexto ya es otro
        // cuando vuelve la respuesta (p. ej. un ámbito explícito que se abrió
        // o cerró entre medias) no se persiste: ni con la huella de X (las
        // filas se leyeron bajo otro contexto) ni con la de Y (la pregunta
        // se hizo bajo X). Y la misma consulta con el contexto estable, sí.
        var reloj = new Reloj();
        var sesionEnVigor = Sesion.Base;
        using var contexto = new BunitContext();
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var fabrica = new FabricaEstadoDePantallaPersistido(gestor.State, Huella(() => sesionEnVigor), reloj);
        using var estado = fabrica.Crear<Instantanea>(Clave);

        var consulta = await estado.EmpezarConsultaAsync(sePersiste: true);
        sesionEnVigor = Sesion.Base with { TenantActual = Sesion.TenantY };
        await estado.GuardarAsync(consulta, HuellaConsulta, new Instantanea("filas leídas entre dos contextos", 3));

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, contexto.Renderer);
        almacen.Contenido.Should().BeEmpty();

        // Control positivo: con el contexto estable la misma secuencia persiste
        // (un gestor nuevo: el de Blazor solo persiste una vez).
        var estable = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("filas", 3));
        estable.Contenido.Should().HaveCount(1);
    }

    [Fact]
    public async Task Una_consulta_de_otro_estado_no_se_anota_aunque_su_numero_coincida()
    {
        var reloj = new Reloj();
        using var contexto = new BunitContext();
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var fabrica = new FabricaEstadoDePantallaPersistido(gestor.State, Huella(Sesion.Base), reloj);
        using var estadoA = fabrica.Crear<Instantanea>("lista-a");
        using var estadoB = fabrica.Crear<Instantanea>("lista-b");

        var deA = await estadoA.EmpezarConsultaAsync(sePersiste: true);
        await estadoB.EmpezarConsultaAsync(sePersiste: true);
        await estadoB.GuardarAsync(deA, HuellaConsulta, new Instantanea("filas de A anotadas en B", 1));
        await estadoA.GuardarAsync(default, HuellaConsulta, new Instantanea("consulta por defecto", 1));

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, contexto.Renderer);
        almacen.Contenido.Should().BeEmpty();
    }

    private sealed class RegistroEspia<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Si_la_huella_no_se_puede_resolver_queda_un_aviso_y_no_se_persiste()
    {
        // Un fallo sostenido apagaría el patrón entero en silencio: por eso
        // se dice. Vale para el fallo al empezar y para el fallo al volver.
        var reloj = new Reloj();
        var registro = new RegistroEspia<FabricaEstadoDePantallaPersistido>();
        var fallar = true;
        var sesion = Huella(() => fallar ? throw new InvalidOperationException("sin contexto") : Sesion.Base);
        using var contexto = new BunitContext();
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var fabrica = new FabricaEstadoDePantallaPersistido(gestor.State, sesion, reloj, registro);
        using var estado = fabrica.Crear<Instantanea>(Clave);

        // Fallo al empezar.
        var alEmpezar = await estado.EmpezarConsultaAsync(sePersiste: true);
        await estado.GuardarAsync(alEmpezar, HuellaConsulta, new Instantanea("filas", 3));
        registro.Entradas.Should().ContainSingle(e => e.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Which.Mensaje.Should().Contain(Clave);

        // Fallo al volver (empezó bien).
        fallar = false;
        var alVolver = await estado.EmpezarConsultaAsync(sePersiste: true);
        fallar = true;
        await estado.GuardarAsync(alVolver, HuellaConsulta, new Instantanea("filas", 3));
        registro.Entradas.Should().HaveCount(2);
        registro.Entradas.Should().OnlyContain(e => e.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning);

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, contexto.Renderer);
        almacen.Contenido.Should().BeEmpty();
    }

    [Fact]
    public async Task La_respuesta_de_una_consulta_superada_no_se_anota_llegue_en_el_orden_que_llegue()
    {
        var reloj = new Reloj();
        using var contexto = new BunitContext();
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var fabrica = new FabricaEstadoDePantallaPersistido(gestor.State, Huella(Sesion.Base), reloj);
        using var estado = fabrica.Crear<Instantanea>(Clave);

        var primera = await estado.EmpezarConsultaAsync(sePersiste: true);
        var segunda = await estado.EmpezarConsultaAsync(sePersiste: true);
        await estado.GuardarAsync(segunda, HuellaConsulta, new Instantanea("la reciente", 2));
        await estado.GuardarAsync(primera, HuellaConsulta, new Instantanea("la vieja, que llega la última", 1));

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, contexto.Renderer);

        (await CircuitoAsync(almacen, Sesion.Base, reloj)).Should().Be(new Instantanea("la reciente", 2));
    }

    private sealed class SesionConPuerta : ICurrentUserService, ITenantActual, IClienteActivoSeleccionado
    {
        /// <summary>Si no es nula, resolver el usuario espera a que se abra.</summary>
        public TaskCompletionSource? Puerta { get; set; }

        public async Task<Guid?> ObtenerUsuarioActualIdAsync()
        {
            if (Puerta is { } puerta)
                await puerta.Task;
            return Sesion.Usuario;
        }

        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Sesion.TenantX);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
        public Guid? TenantId => Sesion.TenantX;
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    [Fact]
    public async Task Una_consulta_que_empieza_mientras_se_resuelve_la_huella_de_la_anterior_la_deja_sin_anotar()
    {
        // La consulta A vuelve y su huella tarda en resolverse; mientras
        // tanto empieza y termina la consulta B. Cuando A por fin resuelve,
        // su respuesta está superada y no puede pisar la de B.
        var reloj = new Reloj();
        var sesion = new SesionConPuerta();
        using var contexto = new BunitContext();
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var fabrica = new FabricaEstadoDePantallaPersistido(
            gestor.State, new HuellaDeSesion(sesion, sesion, sesion), reloj);
        using var estado = fabrica.Crear<Instantanea>(Clave);

        var consultaA = await estado.EmpezarConsultaAsync(sePersiste: true);
        sesion.Puerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puertaDeA = sesion.Puerta;
        var guardandoA = estado.GuardarAsync(consultaA, HuellaConsulta, new Instantanea("la vieja", 1));

        sesion.Puerta = null;
        var consultaB = await estado.EmpezarConsultaAsync(sePersiste: true);
        await estado.GuardarAsync(consultaB, HuellaConsulta, new Instantanea("la reciente", 2));

        puertaDeA.SetResult();
        await guardandoA;

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, contexto.Renderer);
        (await CircuitoAsync(almacen, Sesion.Base, reloj)).Should().Be(new Instantanea("la reciente", 2));
    }

    [Fact]
    public async Task Una_pasada_que_no_se_persiste_no_resuelve_la_huella_ni_deja_estado()
    {
        // El circuito interactivo no persiste: no debe pagar la huella (para
        // un workspace delegado consulta la base) en cada acción del usuario.
        var reloj = new Reloj();
        var resoluciones = 0;
        using var contexto = new BunitContext();
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var fabrica = new FabricaEstadoDePantallaPersistido(
            gestor.State, Huella(() => { resoluciones++; return Sesion.Base; }), reloj);
        using var estado = fabrica.Crear<Instantanea>(Clave);

        var consulta = await estado.EmpezarConsultaAsync(sePersiste: false);
        await estado.GuardarAsync(consulta, HuellaConsulta, new Instantanea("filas", 3));

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, contexto.Renderer);

        resoluciones.Should().Be(0);
        almacen.Contenido.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_la_huella_de_sesion_no_se_puede_resolver_al_empezar_no_se_persiste_nada()
    {
        var reloj = new Reloj();
        using var contexto = new BunitContext();
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var fabrica = new FabricaEstadoDePantallaPersistido(
            gestor.State, Huella(() => throw new InvalidOperationException("sin contexto")), reloj);
        using var estado = fabrica.Crear<Instantanea>(Clave);

        var consulta = await estado.EmpezarConsultaAsync(sePersiste: true);
        await estado.GuardarAsync(consulta, HuellaConsulta, new Instantanea("filas", 3));

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, contexto.Renderer);
        almacen.Contenido.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_consulta_nueva_que_empieza_descarta_lo_anotado_de_la_anterior()
    {
        // Recarga tras eliminar una fila que falla: el estado que se
        // persistiría (p. ej. al pausar el circuito) sería la lista de antes
        // de eliminar, con la fila que ya no existe.
        var reloj = new Reloj();
        using var contexto = new BunitContext();
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var fabrica = new FabricaEstadoDePantallaPersistido(gestor.State, Huella(Sesion.Base), reloj);
        using var estado = fabrica.Crear<Instantanea>(Clave);
        await estado.GuardarAsync(await estado.EmpezarConsultaAsync(sePersiste: true), HuellaConsulta, new Instantanea("con la fila eliminada", 3));

        estado.Descartar();

        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, contexto.Renderer);
        almacen.Contenido.Should().BeEmpty();
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

    [Theory]
    [InlineData(59, true)]
    [InlineData(61, false)]
    public async Task La_vigencia_son_60_segundos_no_una_constante_que_se_pueda_bajar_sin_que_se_note(
        int segundosTranscurridos, bool seRecoge)
    {
        // Los otros tests de caducidad se escriben contra la propia constante:
        // bajarla a 1 s los dejaría verdes y volvería inerte el patrón para
        // cualquier cliente con latencia real. Estos dos fijan el número.
        var reloj = new Reloj();
        var almacen = await PrerenderAsync(Sesion.Base, reloj, new Instantanea("filas", 3));

        reloj.Ahora += TimeSpan.FromSeconds(segundosTranscurridos);

        var recogida = await CircuitoAsync(almacen, Sesion.Base, reloj);

        if (seRecoge)
            recogida.Should().Be(new Instantanea("filas", 3));
        else
            recogida.Should().BeNull();
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
    public async Task El_instante_es_el_de_cuando_se_pregunta_no_el_de_la_respuesta_ni_el_de_la_persistencia()
    {
        var reloj = new Reloj();
        using var contexto = new BunitContext();
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var fabrica = new FabricaEstadoDePantallaPersistido(gestor.State, Huella(Sesion.Base), reloj);
        using var estado = fabrica.Crear<Instantanea>(Clave);
        var consulta = await estado.EmpezarConsultaAsync(sePersiste: true);

        // La consulta tarda 30 s en responder y el render otros 15 s en persistir.
        reloj.Ahora += TimeSpan.FromSeconds(30);
        await estado.GuardarAsync(consulta, HuellaConsulta, new Instantanea("filas", 3));
        reloj.Ahora += TimeSpan.FromSeconds(15);
        var almacen = new AlmacenEnMemoria();
        await gestor.PersistStateAsync(almacen, contexto.Renderer);

        // 20 s después de persistir (35 s desde que volvió la respuesta) el
        // estado tiene 65 s desde que se PREGUNTÓ: vencido.
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
