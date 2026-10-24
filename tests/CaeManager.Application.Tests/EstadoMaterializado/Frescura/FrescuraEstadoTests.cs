using CaeManager.Application.Common;
using CaeManager.Application.EstadoMaterializado.Frescura;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.EstadoMaterializado.Frescura;

/// <summary>
/// Frescura del estado materializado (diseño § 5): la caché por circuito sirve lo guardado mientras no sube la versión del
/// Tenant, relee solo las filas avisadas cuando recibió el aviso de cada versión intermedia y relee entero en cualquier otro
/// caso; el notificador solo entrega a las suscripciones del Tenant; el buzón solo deja salir lo confirmado.
/// </summary>
public class FrescuraEstadoTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private sealed record Fila(Guid Entidad, int Valor);

    private sealed class Usuario(Guid usuario, Guid origen) : ICurrentUserService
    {
        public Guid Id { get; set; } = usuario;
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Id);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("GestorCae");
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(origen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class Tenant(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; set; } = tenantId;
    }

    private sealed class Version(Guid tenantId) : ILectorVersionEstadoTenant
    {
        public Guid TenantId { get; set; } = tenantId;
        public long? Valor { get; set; } = 1;

        public Task<VersionEstadoLeida?> LeerAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Valor is { } v ? new VersionEstadoLeida(TenantId, v) : null);
    }

    private sealed class Reloj : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; } = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Ahora;
    }

    /// <summary>La «base»: filas por entidad, y cuántas veces y con qué entidades se leyó.</summary>
    private sealed class Base
    {
        public Dictionary<Guid, int> Filas { get; } = new();
        public int LecturasCompletas { get; private set; }
        public List<IReadOnlyCollection<Guid>> LecturasParciales { get; } = [];

        public ConsultaEstadoEnCache<Fila> Consulta(string nombre = "centros") => new(
            nombre,
            f => [f.Entidad],
            _ =>
            {
                LecturasCompletas++;
                return Task.FromResult<IReadOnlyList<Fila>>(Filas.Select(p => new Fila(p.Key, p.Value)).ToList());
            },
            (entidades, _) =>
            {
                LecturasParciales.Add(entidades.ToList());
                return Task.FromResult<IReadOnlyList<Fila>>(
                    Filas.Where(p => entidades.Contains(p.Key)).Select(p => new Fila(p.Key, p.Value)).ToList());
            });
    }

    private sealed class Circuito
    {
        public Usuario Usuario { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public Tenant Tenant { get; } = new(TenantA);
        public Version Version { get; } = new(TenantA);
        public Reloj Reloj { get; } = new();
        public NotificadorEstadoTenant Notificador { get; }
        public CacheEstadoCircuito Cache { get; }

        public Circuito(NotificadorEstadoTenant? notificador = null)
        {
            Notificador = notificador ?? new NotificadorEstadoTenant();
            Cache = new CacheEstadoCircuito(Usuario, Tenant, Version, Notificador, Reloj);
        }
    }

    private static Base BaseConTres(out Guid[] entidades)
    {
        entidades = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        var @base = new Base();
        foreach (var e in entidades)
            @base.Filas[e] = 0;
        return @base;
    }

    [Fact]
    public async Task Con_la_misma_version_sirve_lo_guardado_sin_releer()
    {
        var @base = BaseConTres(out _);
        var c = new Circuito();

        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);
        var segunda = await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        segunda.Should().HaveCount(3);
        @base.LecturasCompletas.Should().Be(1);
        @base.LecturasParciales.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_aviso_de_cada_version_intermedia_relee_solo_las_filas_avisadas()
    {
        var @base = BaseConTres(out var e);
        var c = new Circuito();
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        @base.Filas[e[0]] = 7;
        @base.Filas[e[1]] = 8;
        var nueva = Guid.NewGuid();
        @base.Filas[nueva] = 9;
        c.Notificador.Publicar(new AvisoCambioEstado(TenantA, 2, [e[0]]));
        c.Notificador.Publicar(new AvisoCambioEstado(TenantA, 3, [e[1], nueva]));
        c.Version.Valor = 3;

        var filas = await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        filas.Should().BeEquivalentTo([new Fila(e[0], 7), new Fila(e[1], 8), new Fila(e[2], 0), new Fila(nueva, 9)]);
        @base.LecturasCompletas.Should().Be(1, "lo no avisado no se vuelve a leer");
        @base.LecturasParciales.Should().ContainSingle().Which.Should().BeEquivalentTo([e[0], e[1], nueva]);
        c.Cache.RelecturasParciales.Should().Be(1);
    }

    [Fact]
    public async Task Una_fila_que_desaparece_sale_de_la_cache_en_la_relectura_parcial()
    {
        var @base = BaseConTres(out var e);
        var c = new Circuito();
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        @base.Filas.Remove(e[2]);
        c.Notificador.Publicar(new AvisoCambioEstado(TenantA, 2, [e[2]]));
        c.Version.Valor = 2;

        (await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None)).Select(f => f.Entidad)
            .Should().BeEquivalentTo([e[0], e[1]]);
    }

    [Fact]
    public async Task Si_falta_el_aviso_de_alguna_version_relee_entero()
    {
        var @base = BaseConTres(out var e);
        var c = new Circuito();
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        // La versión 2 la escribió otro contenedor: su aviso no llegó.
        c.Notificador.Publicar(new AvisoCambioEstado(TenantA, 3, [e[0]]));
        c.Version.Valor = 3;
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        @base.LecturasCompletas.Should().Be(2);
        @base.LecturasParciales.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_ningun_aviso_una_version_nueva_relee_entero()
    {
        var @base = BaseConTres(out var e);
        var c = new Circuito();
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        @base.Filas[e[0]] = 5;
        c.Version.Valor = 2;

        (await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None)).Should().Contain(new Fila(e[0], 5));
        @base.LecturasCompletas.Should().Be(2);
    }

    [Fact]
    public async Task Otro_dia_de_negocio_relee_entero_aunque_no_cambie_la_version()
    {
        var @base = BaseConTres(out _);
        var c = new Circuito();
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        c.Reloj.Ahora = c.Reloj.Ahora.AddDays(1);
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        @base.LecturasCompletas.Should().Be(2);
    }

    [Fact]
    public async Task Otro_usuario_otro_Tenant_efectivo_u_otra_consulta_son_otra_entrada()
    {
        var @base = BaseConTres(out _);
        var c = new Circuito();
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        c.Tenant.TenantId = TenantB;
        c.Version.TenantId = TenantB;
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);
        c.Usuario.Id = Guid.NewGuid();
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);
        await c.Cache.ObtenerAsync(@base.Consulta("empresas"), CancellationToken.None);

        @base.LecturasCompletas.Should().Be(4, "ninguna entrada se comparte entre contextos ni entre consultas");
    }

    [Fact]
    public async Task Sin_fila_de_version_o_sin_usuario_no_guarda_nada()
    {
        var @base = BaseConTres(out _);
        var c = new Circuito();
        c.Version.Valor = null;
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        var sinUsuario = new CacheEstadoCircuito(new SinUsuario(), new Tenant(TenantA), new Version(TenantA), new NotificadorEstadoTenant());
        await sinUsuario.ObtenerAsync(@base.Consulta(), CancellationToken.None);
        await sinUsuario.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        @base.LecturasCompletas.Should().Be(4);
        c.Notificador.SuscripcionesDe(TenantA).Should().Be(0, "sin versión leída no hay Tenant al que suscribirse");
    }

    private sealed class SinUsuario : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(null);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(null);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }

    [Fact]
    public async Task Solo_se_suscribe_al_Tenant_cuya_version_leyo_y_un_aviso_de_otro_Tenant_no_lo_toca()
    {
        var @base = BaseConTres(out var e);
        var notificador = new NotificadorEstadoTenant();
        var a = new Circuito(notificador);
        var b = new Circuito(notificador);
        b.Tenant.TenantId = TenantB;
        b.Version.TenantId = TenantB;
        await a.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);
        await b.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);

        notificador.Publicar(new AvisoCambioEstado(TenantA, 2, [e[0]]));
        b.Cache.RecibirAviso(new AvisoCambioEstado(TenantA, 2, [e[0]]));

        a.Cache.AvisosRecibidos.Should().Be(1);
        b.Cache.AvisosRecibidos.Should().Be(0, "ni por el notificador ni forzado: B no está suscrito a A");
        notificador.SuscripcionesDe(TenantA).Should().Be(1);
        notificador.SuscripcionesDe(TenantB).Should().Be(1);
    }

    [Fact]
    public async Task Al_liberarse_el_circuito_se_da_de_baja()
    {
        var @base = BaseConTres(out _);
        var c = new Circuito();
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);
        c.Notificador.SuscripcionesDe(TenantA).Should().Be(1);

        c.Cache.Dispose();

        c.Notificador.SuscripcionesDe(TenantA).Should().Be(0);
        c.Notificador.Publicar(new AvisoCambioEstado(TenantA, 2, [Guid.NewGuid()]));
        c.Cache.AvisosRecibidos.Should().Be(0);
    }

    [Fact]
    public async Task El_aviso_dispara_el_evento_del_circuito()
    {
        var @base = BaseConTres(out var e);
        var c = new Circuito();
        await c.Cache.ObtenerAsync(@base.Consulta(), CancellationToken.None);
        var recibidos = new List<AvisoCambioEstado>();
        c.Cache.CambioRecibido += recibidos.Add;

        c.Notificador.Publicar(new AvisoCambioEstado(TenantA, 2, [e[0]]));

        recibidos.Should().ContainSingle().Which.Version.Should().Be(2);
    }

    [Fact]
    public void Un_suscriptor_que_falla_no_impide_que_los_demas_reciban_el_aviso()
    {
        var notificador = new NotificadorEstadoTenant();
        var recibidos = 0;
        using var roto = notificador.Suscribir(TenantA, _ => throw new InvalidOperationException("circuito cerrado"));
        using var sano = notificador.Suscribir(TenantA, _ => recibidos++);
        using var otro = notificador.Suscribir(TenantB, _ => recibidos += 100);

        notificador.Publicar(new AvisoCambioEstado(TenantA, 2, []));

        recibidos.Should().Be(1, "solo el sano del Tenant A");
    }

    [Fact]
    public void El_buzon_solo_entrega_lo_que_sobrevive_a_los_puntos_de_guardado()
    {
        var buzon = new BuzonAvisosEstado();
        var antes = new AvisoCambioEstado(TenantA, 1, []);
        var dentro = new AvisoCambioEstado(TenantA, 2, []);
        var liberado = new AvisoCambioEstado(TenantA, 3, []);

        buzon.Anotar(antes);
        buzon.AbrirPunto();
        buzon.Anotar(dentro);
        buzon.VolverAPunto();
        buzon.AbrirPunto();
        buzon.Anotar(liberado);
        buzon.LiberarPunto();

        buzon.Tomar().Should().Equal(antes, liberado);
        buzon.Pendientes.Should().Be(0);
    }

    [Fact]
    public void Volver_a_un_punto_descarta_desde_el_mas_antiguo_abierto_y_deshacer_descarta_todo()
    {
        var buzon = new BuzonAvisosEstado();
        buzon.AbrirPunto();
        buzon.Anotar(new AvisoCambioEstado(TenantA, 1, []));
        buzon.AbrirPunto();
        buzon.Anotar(new AvisoCambioEstado(TenantA, 2, []));
        buzon.VolverAPunto();
        buzon.Pendientes.Should().Be(0, "sin el nombre del punto, descartar de más es lo seguro");

        buzon.Anotar(new AvisoCambioEstado(TenantA, 3, []));
        buzon.Descartar();
        buzon.Tomar().Should().BeEmpty();
    }
}
