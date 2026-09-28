using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.GuardarLogoTenant;
using CaeManager.Application.Tenants.Commands.RetirarLogoTenant;
using CaeManager.Application.Tenants.Logo;
using CaeManager.Application.Tenants.Queries.ObtenerLogoTenant;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Logo del Tenant (contrato del selector de Tenant, lote 1). Capa Application: quién escribe
/// (invariante I7, cada rama en positivo y en negativo), el orden blob → guardado → borrado del
/// anterior (C9) y que la lectura decide la autorización antes de tocar <c>Tenants</c> (I11).
/// </summary>
public class LogoTenantTests
{
    private static readonly Guid UsuarioId = Guid.NewGuid();
    private readonly Tenant _propietario = new("Tenant propietario");
    private readonly Tenant _otro = new("Otro Tenant");

    // ── Autorizador del logo: Administrador del Tenant propietario ─────────────

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_puede()
    {
        var autorizacion = Autorizador(sesion: null, administradorDe: _propietario.Id);

        (await autorizacion.PuedeEscribirAsync(_propietario.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task El_Administrador_de_otro_Tenant_no_puede_aunque_lo_tenga_seleccionado()
    {
        // Administrador (o Gestor CAE) del Operador CAE externo con el Tenant beneficiario
        // seleccionado por la vía de Operación: el Tenant actual es el beneficiario, pero su Tenant
        // de origen, leído en base, no.
        var autorizacion = Autorizador(sesion: null, administradorDe: _otro.Id);

        (await autorizacion.PuedeEscribirAsync(_propietario.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Un_Gestor_CAE_del_propio_Tenant_no_puede()
    {
        var autorizacion = new AutorizacionLogoTenant(
            new SesionPrivilegiadaFalsa(null), new CurrentUserServiceFalso(UsuarioId, "GestorCae", _propietario.Id),
            new AutorizacionDelegacionFalsa(autoriza: false));

        (await autorizacion.PuedeEscribirAsync(_propietario.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Sin_usuario_no_puede()
    {
        var autorizacion = new AutorizacionLogoTenant(
            new SesionPrivilegiadaFalsa(null), new CurrentUserServiceFalso(usuarioId: null),
            new AutorizacionDelegacionFalsa(autoriza: true));

        (await autorizacion.PuedeEscribirAsync(_propietario.Id)).Should().BeFalse();
    }

    // ── Autorizador del logo: Soporte TALVEG en Sesión Privilegiada ────────────

    [Fact]
    public async Task Soporte_con_Aprovisionamiento_sobre_ese_Tenant_puede()
    {
        var autorizacion = Autorizador(Sesion(CapacidadPrivilegio.Aprovisionamiento, _propietario.Id), administradorDe: null);

        (await autorizacion.PuedeEscribirAsync(_propietario.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Soporte_con_Aprovisionamiento_sobre_otro_Tenant_no_puede()
    {
        var autorizacion = Autorizador(Sesion(CapacidadPrivilegio.Aprovisionamiento, _otro.Id), administradorDe: null);

        (await autorizacion.PuedeEscribirAsync(_propietario.Id)).Should().BeFalse();
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    [InlineData(CapacidadPrivilegio.RestablecimientoSegundoFactor)]
    public async Task Soporte_sin_Aprovisionamiento_no_puede(CapacidadPrivilegio capacidad)
    {
        var autorizacion = Autorizador(Sesion(capacidad, _propietario.Id), administradorDe: null);

        (await autorizacion.PuedeEscribirAsync(_propietario.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Soporte_con_simulacion_de_usuario_no_puede()
    {
        var autorizacion = Autorizador(
            Sesion(CapacidadPrivilegio.Aprovisionamiento, _propietario.Id, usuarioSimulado: Guid.NewGuid()),
            administradorDe: null);

        (await autorizacion.PuedeEscribirAsync(_propietario.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Dentro_de_una_Sesion_Privilegiada_manda_la_sesion_y_no_el_rol_en_base()
    {
        // Aunque el usuario figurase como Administrador del Tenant, con una sesión de solo lectura
        // abierta no escribe: la vía del Administrador es solo fuera de Sesión Privilegiada.
        var autorizacion = Autorizador(Sesion(CapacidadPrivilegio.SoporteLectura, _propietario.Id), administradorDe: _propietario.Id);

        (await autorizacion.PuedeEscribirAsync(_propietario.Id)).Should().BeFalse();
    }

    // ── GuardarLogoTenantCommand ──────────────────────────────────────────────

    [Fact]
    public async Task Guardar_sin_autorizacion_no_convierte_ni_escribe_nada()
    {
        var escenario = new Escenario(_propietario, autoriza: false);

        var resultado = await escenario.GuardarAsync();

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error.Codigo.Should().Be("LogoTenant.NoAutorizado");
        escenario.Conversor.Llamadas.Should().Be(0);
        escenario.Almacenamiento.ArchivosGuardados.Should().Be(0);
        escenario.UnidadDeTrabajo.VecesGuardado.Should().Be(0);
        _propietario.LogoArchivoClave.Should().BeNull();
    }

    [Fact]
    public async Task Guardar_autoriza_contra_el_Tenant_actual()
    {
        var escenario = new Escenario(_propietario, autoriza: true);

        await escenario.GuardarAsync();

        escenario.Autorizacion.TenantConsultado.Should().Be(_propietario.Id);
    }

    [Fact]
    public async Task Guardar_escribe_el_blob_convertido_y_borra_el_anterior_despues_de_guardar()
    {
        var escenario = new Escenario(_propietario, autoriza: true);
        await escenario.GuardarAsync();
        var primera = _propietario.LogoArchivoClave!;

        escenario.Conversor.Salida = [9, 9, 9];
        var resultado = await escenario.GuardarAsync();

        resultado.EsExitoso.Should().BeTrue();
        _propietario.LogoArchivoClave.Should().NotBe(primera);
        _propietario.LogoVersion.Should().Be(GuardarLogoTenantCommandHandler.VersionDe([9, 9, 9]));
        escenario.Almacenamiento.Contiene(_propietario.LogoArchivoClave!).Should().BeTrue();
        escenario.Almacenamiento.Contenido(_propietario.LogoArchivoClave!).Should().Equal(9, 9, 9);
        escenario.Almacenamiento.Contiene(primera).Should().BeFalse("el blob anterior se borra tras guardar");
        escenario.UnidadDeTrabajo.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task Guardar_con_imagen_no_admitida_falla_sin_escribir_blob()
    {
        var escenario = new Escenario(_propietario, autoriza: true);
        escenario.Conversor.Rechazo = new LogoTenantNoAdmitidoException(
            MotivoLogoNoAdmitido.FormatoNoAdmitido, "Formato no admitido (PNG, JPG).");

        var resultado = await escenario.GuardarAsync();

        resultado.Error.Codigo.Should().Be("LogoTenant.ImagenNoAdmitida");
        resultado.Error.Mensaje.Should().Be("Formato no admitido (PNG, JPG).");
        escenario.Almacenamiento.ArchivosGuardados.Should().Be(0);
    }

    [Fact]
    public async Task Si_el_guardado_falla_el_blob_nuevo_se_borra_y_el_anterior_sigue()
    {
        var escenario = new Escenario(_propietario, autoriza: true);
        await escenario.GuardarAsync();
        var vigente = _propietario.LogoArchivoClave!;
        escenario.UnidadDeTrabajo.ExcepcionAlGuardar = new DbUpdateConcurrencyException("otra subida ganó");

        var accion = () => escenario.GuardarAsync();

        await accion.Should().ThrowAsync<DbUpdateConcurrencyException>();
        escenario.Almacenamiento.ArchivosGuardados.Should().Be(1, "solo queda el blob que la base sigue referenciando");
        escenario.Almacenamiento.Contiene(vigente).Should().BeTrue();
    }

    [Fact]
    public void El_validador_rechaza_mas_de_5_MB()
    {
        var validador = new GuardarLogoTenantCommandValidator();

        validador.Validate(new GuardarLogoTenantCommand(new byte[GuardarLogoTenantCommandValidator.TamanoMaximoBytes + 1]))
            .IsValid.Should().BeFalse();
        validador.Validate(new GuardarLogoTenantCommand(new byte[GuardarLogoTenantCommandValidator.TamanoMaximoBytes]))
            .IsValid.Should().BeTrue();
    }

    // ── RetirarLogoTenantCommand ──────────────────────────────────────────────

    [Fact]
    public async Task Retirar_sin_autorizacion_no_toca_nada()
    {
        var escenario = new Escenario(_propietario, autoriza: true);
        await escenario.GuardarAsync();
        var clave = _propietario.LogoArchivoClave!;
        escenario.Autorizacion.Autoriza = false;

        var resultado = await escenario.RetirarAsync();

        resultado.Error.Codigo.Should().Be("LogoTenant.NoAutorizado");
        _propietario.LogoArchivoClave.Should().Be(clave);
        escenario.Almacenamiento.Contiene(clave).Should().BeTrue();
    }

    [Fact]
    public async Task Retirar_vacia_la_columna_y_borra_el_blob()
    {
        var escenario = new Escenario(_propietario, autoriza: true);
        await escenario.GuardarAsync();
        var clave = _propietario.LogoArchivoClave!;

        var resultado = await escenario.RetirarAsync();

        resultado.EsExitoso.Should().BeTrue();
        _propietario.LogoArchivoClave.Should().BeNull();
        escenario.Almacenamiento.Contiene(clave).Should().BeFalse();
    }

    // ── ObtenerLogoTenantQuery ────────────────────────────────────────────────

    [Fact]
    public async Task La_lectura_no_autorizada_no_consulta_Tenants()
    {
        _otro.EstablecerLogo($"{_otro.Id:N}/{Guid.NewGuid():N}.png", "0123456789abcdef", DateTime.UtcNow);
        var tenants = new TenantsQueryContextQueCuenta(_propietario, _otro);
        var handler = new ObtenerLogoTenantQueryHandler(
            new SesionPrivilegiadaFalsa(null), new CurrentUserServiceFalso(UsuarioId, "GestorCae", _propietario.Id),
            new OperacionesVacias(), tenants);

        var logo = await handler.Handle(new ObtenerLogoTenantQuery(_otro.Id), CancellationToken.None);

        logo.Should().BeNull();
        tenants.LecturasDeTenants.Should().Be(0);
    }

    [Fact]
    public async Task La_lectura_del_propio_Tenant_devuelve_su_logo()
    {
        _propietario.EstablecerLogo($"{_propietario.Id:N}/{Guid.NewGuid():N}.png", "0123456789abcdef", DateTime.UtcNow);
        var handler = new ObtenerLogoTenantQueryHandler(
            new SesionPrivilegiadaFalsa(null), new CurrentUserServiceFalso(UsuarioId, "GestorCae", _propietario.Id),
            new OperacionesVacias(), new TenantsQueryContextQueCuenta(_propietario, _otro));

        var logo = await handler.Handle(new ObtenerLogoTenantQuery(_propietario.Id), CancellationToken.None);

        logo.Should().Be(new LogoTenantDto(_propietario.LogoArchivoClave!, "0123456789abcdef"));
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_solo_se_lee_el_Tenant_objetivo()
    {
        _propietario.EstablecerLogo($"{_propietario.Id:N}/{Guid.NewGuid():N}.png", "0123456789abcdef", DateTime.UtcNow);
        _otro.EstablecerLogo($"{_otro.Id:N}/{Guid.NewGuid():N}.png", "fedcba9876543210", DateTime.UtcNow);
        // Su Tenant de origen sería _otro: con la sesión abierta sobre _propietario, ni su origen vale.
        var handler = new ObtenerLogoTenantQueryHandler(
            new SesionPrivilegiadaFalsa(Sesion(CapacidadPrivilegio.SoporteLectura, _propietario.Id)),
            new CurrentUserServiceFalso(UsuarioId, null, _otro.Id),
            new OperacionesVacias(), new TenantsQueryContextQueCuenta(_propietario, _otro));

        (await handler.Handle(new ObtenerLogoTenantQuery(_propietario.Id), CancellationToken.None)).Should().NotBeNull();
        (await handler.Handle(new ObtenerLogoTenantQuery(_otro.Id), CancellationToken.None)).Should().BeNull();
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private static AutorizacionLogoTenant Autorizador(SesionPrivilegiadaActiva? sesion, Guid? administradorDe) =>
        new(new SesionPrivilegiadaFalsa(sesion), new CurrentUserServiceFalso(UsuarioId),
            administradorDe is { } tenant
                ? AutorizacionDelegacionFalsa.AdministradorDe(tenant)
                : new AutorizacionDelegacionFalsa(autoriza: false));

    private static SesionPrivilegiadaActiva Sesion(
        CapacidadPrivilegio capacidad, Guid tenantObjetivo, Guid? usuarioSimulado = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tenantObjetivo, capacidad, usuarioSimulado);

    private sealed class Escenario
    {
        private readonly Tenant _tenant;

        public Escenario(Tenant tenant, bool autoriza)
        {
            _tenant = tenant;
            Autorizacion = new AutorizacionLogoFalsa { Autoriza = autoriza };
        }

        public AutorizacionLogoFalsa Autorizacion { get; }
        public ConversorFalso Conversor { get; } = new();
        public AlmacenamientoConClaveDeTenant Almacenamiento { get; } = new();
        public UnitOfWorkFalso UnidadDeTrabajo { get; } = new();

        public Task<Domain.Common.Result> GuardarAsync() =>
            new GuardarLogoTenantCommandHandler(
                    new TenantActualFijo(_tenant.Id), Autorizacion, Conversor, new TenantRepositoryFalso(_tenant),
                    Almacenamiento.Para(_tenant.Id), UnidadDeTrabajo, NullLogger<GuardarLogoTenantCommandHandler>.Instance)
                .Handle(new GuardarLogoTenantCommand([1, 2, 3]), CancellationToken.None);

        public Task<Domain.Common.Result> RetirarAsync() =>
            new RetirarLogoTenantCommandHandler(
                    new TenantActualFijo(_tenant.Id), Autorizacion, new TenantRepositoryFalso(_tenant),
                    Almacenamiento.Para(_tenant.Id), UnidadDeTrabajo, NullLogger<RetirarLogoTenantCommandHandler>.Instance)
                .Handle(new RetirarLogoTenantCommand(), CancellationToken.None);
    }

    private sealed class AutorizacionLogoFalsa : IAutorizacionLogoTenant
    {
        public bool Autoriza { get; set; }
        public Guid? TenantConsultado { get; private set; }

        public Task<bool> PuedeEscribirAsync(Guid tenantObjetivoId, CancellationToken cancellationToken = default)
        {
            TenantConsultado = tenantObjetivoId;
            return Task.FromResult(Autoriza);
        }
    }

    private sealed class ConversorFalso : IConversorLogoTenantService
    {
        public int Llamadas { get; private set; }
        public byte[] Salida { get; set; } = [1, 1, 1];
        public LogoTenantNoAdmitidoException? Rechazo { get; set; }

        public byte[] ConvertirAPng(byte[] imagenOriginal)
        {
            Llamadas++;
            if (Rechazo is not null) throw Rechazo;
            return Salida;
        }
    }

    /// <summary>Como <c>DiskFileStorageService</c>: claves <c>{tenantId:N}/{guid}.png</c>.</summary>
    private sealed class AlmacenamientoConClaveDeTenant
    {
        private readonly Dictionary<string, byte[]> _archivos = [];

        public int ArchivosGuardados => _archivos.Count;
        public bool Contiene(string clave) => _archivos.ContainsKey(clave);
        public byte[] Contenido(string clave) => _archivos[clave];

        public IFileStorageService Para(Guid tenantId) => new Vista(this, tenantId);

        private sealed class Vista(AlmacenamientoConClaveDeTenant almacen, Guid tenantId) : IFileStorageService
        {
            public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
            {
                using var memoria = new MemoryStream();
                contenido.CopyTo(memoria);
                var clave = $"{tenantId:N}/{Guid.NewGuid():N}{Path.GetExtension(nombreArchivoOriginal)}";
                almacen._archivos[clave] = memoria.ToArray();
                return Task.FromResult(clave);
            }

            public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
                Task.FromResult<Stream>(new MemoryStream(almacen._archivos[identificador]));

            public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
            {
                almacen._archivos.Remove(identificador);
                return Task.CompletedTask;
            }
        }
    }

    private sealed class TenantActualFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class TenantRepositoryFalso(Tenant tenant) : ITenantRepository
    {
        public Task<bool> ExisteConNombreAsync(string nombre, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Tenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(id == tenant.Id ? tenant : null);

        public void Agregar(Tenant nuevo) => throw new NotSupportedException();
    }

    private sealed class SesionPrivilegiadaFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }

    private sealed class OperacionesVacias : IOperacionesQueryContext
    {
        public IQueryable<AsignacionOperacion> AsignacionesOperacion =>
            new TestAsyncQueryable<AsignacionOperacion>(new List<AsignacionOperacion>().AsQueryable());

        public IQueryable<AsignacionCartera> AsignacionesCartera =>
            new TestAsyncQueryable<AsignacionCartera>(new List<AsignacionCartera>().AsQueryable());
    }

    private sealed class TenantsQueryContextQueCuenta(params Tenant[] tenants) : ITenantsQueryContext
    {
        private readonly TenantsQueryContextFalso _interno = new();

        public int LecturasDeTenants { get; private set; }

        public IQueryable<Tenant> Tenants
        {
            get
            {
                LecturasDeTenants++;
                return new TestAsyncQueryable<Tenant>(tenants.AsQueryable());
            }
        }

        public IQueryable<DelegacionTenant> DelegacionesTenant => _interno.DelegacionesTenant;
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => _interno.AsignacionesOperadorDelegado;
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => _interno.RegistrosActividadSoporte;
    }
}
