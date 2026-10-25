using CaeManager.Application.Common;
using CaeManager.Application.EstadoMaterializado.Frescura;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.EstadoMaterializado.Frescura;

/// <summary>
/// La caché del estado materializado de un circuito se da de baja de los avisos del Tenant beneficiario anterior cuando la
/// selección se retira en caliente (<see cref="IAvisoSeleccionRetirada"/>), no solo al cerrarse el circuito: tras la
/// retirada, un aviso del Tenant anterior (publicado después o ya en vuelo) no llega; si el Tenant pasa de B a C, llegan
/// los de C y no los de B; una lectura que estuviera en vuelo con la versión de B no vuelve a suscribirlo ni guarda nada; y
/// con la selección vigente todo sigue como antes.
/// </summary>
public class CacheEstadoCircuitoTrasRetirarLaSeleccionTests
{
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Beneficiario = Guid.NewGuid();
    private static readonly Guid OtroBeneficiario = Guid.NewGuid();

    private sealed record Fila(Guid Entidad);

    private sealed class Usuario : ICurrentUserService
    {
        private readonly Guid _id = Guid.NewGuid();
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(_id);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("GestorCae");
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Origen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>La selección y el Tenant actual del circuito: retirarla (o cambiarla) cambia el Tenant y luego avisa.</summary>
    private sealed class Seleccion : IClienteActivoSeleccionado, IAvisoSeleccionRetirada, ITenantActual
    {
        public Guid? TenantIdSeleccionado { get; private set; } = Beneficiario;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
        public Guid? TenantId => TenantIdSeleccionado ?? Origen;
        public event Action<Guid>? TenantSeleccionadoRetirado;
        public int Oyentes => TenantSeleccionadoRetirado?.GetInvocationList().Length ?? 0;

        public void Retirar()
        {
            var retirado = TenantIdSeleccionado!.Value;
            TenantIdSeleccionado = null;
            TenantSeleccionadoRetirado?.Invoke(retirado);
        }

        /// <summary>Hoy no ocurre dentro de un circuito (cambiar de Tenant recarga la página), pero la caché lo aguanta.</summary>
        public void CambiarA(Guid tenant)
        {
            var retirado = TenantIdSeleccionado!.Value;
            TenantIdSeleccionado = tenant;
            TenantSeleccionadoRetirado?.Invoke(retirado);
        }
    }

    /// <summary>Lee la versión del Tenant que da el contexto en ese momento, como la RLS.</summary>
    private sealed class Version(ITenantActual tenant) : ILectorVersionEstadoTenant
    {
        public long Valor { get; set; } = 1;

        public Task<VersionEstadoLeida?> LeerAsync(CancellationToken cancellationToken) =>
            Task.FromResult<VersionEstadoLeida?>(new VersionEstadoLeida(tenant.TenantId!.Value, Valor));
    }

    private sealed class Circuito
    {
        public Seleccion Seleccion { get; } = new();
        public NotificadorEstadoTenant Notificador { get; } = new();
        public Version Version { get; }
        public CacheEstadoCircuito Cache { get; }
        public List<AvisoCambioEstado> Recibidos { get; } = [];
        public int LecturasCompletas { get; private set; }
        public int LecturasDeEntidades { get; private set; }
        public Action? AlLeerTodas { get; set; }

        public Circuito()
        {
            Version = new Version(Seleccion);
            Cache = new CacheEstadoCircuito(new Usuario(), Seleccion, Version, Notificador);
            Cache.CambioRecibido += Recibidos.Add;
        }

        public ConsultaEstadoEnCache<Fila> Consulta() => new(
            "centros",
            f => [f.Entidad],
            _ =>
            {
                LecturasCompletas++;
                AlLeerTodas?.Invoke();
                return Task.FromResult<IReadOnlyList<Fila>>([new Fila(Guid.NewGuid())]);
            },
            (_, _) =>
            {
                LecturasDeEntidades++;
                return Task.FromResult<IReadOnlyList<Fila>>([]);
            });

        public Task<IReadOnlyList<Fila>> LeerAsync() => Cache.ObtenerAsync(Consulta(), CancellationToken.None);
    }

    /// <summary>Un Tenant actual que no avisa de la retirada (fuera de un circuito, o un doble de pruebas).</summary>
    private sealed class SoloTenant(ITenantActual tenant) : ITenantActual
    {
        public Guid? TenantId => tenant.TenantId;
    }

    private static AvisoCambioEstado Aviso(Guid tenant, long version = 2) => new(tenant, version, [Guid.NewGuid()]);

    [Fact]
    public async Task Con_la_seleccion_vigente_los_avisos_del_Tenant_beneficiario_siguen_llegando()
    {
        var c = new Circuito();
        await c.LeerAsync();

        c.Notificador.SuscripcionesDe(Beneficiario).Should().Be(1);
        c.Notificador.Publicar(Aviso(Beneficiario));
        c.Version.Valor = 2;
        await c.LeerAsync();

        c.Recibidos.Should().ContainSingle().Which.TenantId.Should().Be(Beneficiario);
        c.Cache.RelecturasParciales.Should().Be(1, "con el aviso de la versión intermedia, la relectura sigue siendo parcial");
        c.LecturasCompletas.Should().Be(1);
    }

    [Fact]
    public async Task Tras_retirar_la_seleccion_un_aviso_publicado_para_el_Tenant_anterior_no_llega_al_circuito()
    {
        var c = new Circuito();
        await c.LeerAsync();
        c.Notificador.SuscripcionesDe(Beneficiario).Should().Be(1, "control positivo: suscrito a B por haber leído su versión");

        c.Seleccion.Retirar();

        c.Notificador.SuscripcionesDe(Beneficiario).Should().Be(0, "la baja de B es en la retirada, no al cerrarse el circuito");
        c.Notificador.Publicar(Aviso(Beneficiario));
        c.Recibidos.Should().BeEmpty();
        c.Cache.AvisosRecibidos.Should().Be(0);
    }

    [Fact]
    public async Task Un_aviso_del_Tenant_anterior_que_ya_estaba_en_vuelo_se_descarta_tras_la_retirada()
    {
        var c = new Circuito();
        await c.LeerAsync();

        c.Seleccion.Retirar();
        // El notificador recorre una instantánea de las suscripciones: puede llamar a la de B aunque ya se haya soltado.
        c.Cache.RecibirAviso(Aviso(Beneficiario));

        c.Recibidos.Should().BeEmpty();
        c.Cache.AvisosRecibidos.Should().Be(0);
    }

    [Fact]
    public async Task Tras_retirar_la_seleccion_el_circuito_se_suscribe_al_Tenant_de_origen_y_recibe_sus_avisos()
    {
        var c = new Circuito();
        await c.LeerAsync();

        c.Seleccion.Retirar();
        await c.LeerAsync();

        c.Notificador.SuscripcionesDe(Origen).Should().Be(1);
        c.Notificador.SuscripcionesDe(Beneficiario).Should().Be(0);
        c.Notificador.Publicar(Aviso(Beneficiario));
        c.Notificador.Publicar(Aviso(Origen));
        c.Recibidos.Should().ContainSingle().Which.TenantId.Should().Be(Origen);
    }

    [Fact]
    public async Task Al_cambiar_de_B_a_C_llegan_los_avisos_de_C_y_no_los_de_B()
    {
        var c = new Circuito();
        await c.LeerAsync();

        c.Seleccion.CambiarA(OtroBeneficiario);
        await c.LeerAsync();

        c.Notificador.SuscripcionesDe(Beneficiario).Should().Be(0);
        c.Notificador.SuscripcionesDe(OtroBeneficiario).Should().Be(1, "una sola suscripción a C, sin duplicar");
        c.Notificador.Publicar(Aviso(Beneficiario));
        c.Notificador.Publicar(Aviso(OtroBeneficiario));
        c.Recibidos.Should().ContainSingle().Which.TenantId.Should().Be(OtroBeneficiario);
    }

    [Fact]
    public async Task Una_lectura_en_vuelo_con_la_version_del_Tenant_retirado_no_lo_vuelve_a_suscribir_ni_guarda()
    {
        var c = new Circuito();
        var versionDeB = new VersionEstadoLeida(Beneficiario, 1);

        c.Seleccion.Retirar();
        await c.Cache.ObtenerAsync(c.Consulta(), versionDeB, [], CancellationToken.None);
        await c.Cache.ObtenerAsync(c.Consulta(), versionDeB, [], CancellationToken.None);

        c.Notificador.SuscripcionesDe(Beneficiario).Should().Be(0);
        c.Cache.EntradasDe(Beneficiario).Should().Be(0);
        c.LecturasCompletas.Should().Be(0, "sin poder guardar, solo se leen las entidades pedidas, no el Tenant entero");
        c.LecturasDeEntidades.Should().Be(2);
    }

    [Fact]
    public async Task Si_la_seleccion_se_retira_mientras_se_leen_las_filas_no_queda_ni_la_suscripcion_ni_la_entrada()
    {
        var c = new Circuito();
        c.AlLeerTodas = () =>
        {
            c.AlLeerTodas = null;
            c.Seleccion.Retirar();
        };

        await c.LeerAsync();

        c.Notificador.SuscripcionesDe(Beneficiario).Should().Be(0, "la suscripción hecha antes de leer las filas se soltó en la retirada");
        c.Cache.EntradasDe(Beneficiario).Should().Be(0, "las filas leídas con la versión de B durante la retirada no se guardan");
        await c.LeerAsync();
        c.LecturasCompletas.Should().Be(2, "las filas leídas durante la retirada no se guardaron");
        c.Notificador.SuscripcionesDe(Origen).Should().Be(1);
    }

    [Fact]
    public async Task La_retirada_solo_suelta_el_Tenant_retirado_y_no_lo_leido_con_otro_Tenant()
    {
        var c = new Circuito();
        // Una lectura de otro Tenant con su propia versión, como las de AmbitoTenantExplicito de la cartera.
        await c.Cache.ObtenerAsync(c.Consulta(), new VersionEstadoLeida(OtroBeneficiario, 1), [], CancellationToken.None);
        await c.LeerAsync();

        c.Seleccion.Retirar();

        c.Notificador.SuscripcionesDe(Beneficiario).Should().Be(0);
        c.Notificador.SuscripcionesDe(OtroBeneficiario).Should().Be(1, "lo que no es el Tenant retirado sigue suscrito");
        c.Cache.EntradasDe(OtroBeneficiario).Should().Be(1);
        c.Notificador.Publicar(Aviso(OtroBeneficiario));
        c.Recibidos.Should().ContainSingle().Which.TenantId.Should().Be(OtroBeneficiario);
    }

    [Fact]
    public void Al_cerrarse_el_circuito_deja_de_escuchar_la_retirada()
    {
        var c = new Circuito();
        c.Seleccion.Oyentes.Should().Be(1);

        c.Cache.Dispose();

        c.Seleccion.Oyentes.Should().Be(0);
    }

    [Fact]
    public async Task Sin_seleccion_que_avise_de_la_retirada_se_comporta_como_hasta_ahora()
    {
        var seleccion = new Seleccion();
        var notificador = new NotificadorEstadoTenant();
        var soloTenant = new SoloTenant(seleccion);
        var cache = new CacheEstadoCircuito(new Usuario(), soloTenant, new Version(soloTenant), notificador);
        var c = new Circuito();

        await cache.ObtenerAsync(c.Consulta(), CancellationToken.None);
        seleccion.Retirar();

        notificador.SuscripcionesDe(Beneficiario).Should().Be(1, "sin el aviso, la baja sigue siendo al cerrarse el circuito");
        cache.Dispose();
        notificador.SuscripcionesDe(Beneficiario).Should().Be(0);
    }
}
