using System.Data;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// El bloqueo de la respuesta sugerida por buzón (<c>ComprobarEnvioPorBuzonAsync</c>) con un <b>Gestor CAE de cartera
/// restringida</b>, no un Administrador, como <c>cae_app_runtime</c> y con el <see cref="AlcanceDatosService"/> real. Lo que
/// se mide es que el filtro de alcance, una <c>Expression</c> sobre la conversación que la consulta de candidatas aplica
/// en SQL, decide lo mismo que <c>ConversacionVisibleAsync</c>, en los dos ejes de ancla y en el triage compartido.
///
/// <para>
/// Escenario, en el Tenant propietario de la sesión, un buzón general (visible para todos) con cinco hilos, cada uno con
/// una sugerencia en Propuesta de texto distinto. La cartera del Gestor CAE es la de un solo Cliente empresarial:
/// <list type="bullet">
/// <item>Hilo anclado al Cliente empresarial de la cartera: visible.</item>
/// <item>Hilo anclado a otro Cliente empresarial, fuera de la cartera: no visible.</item>
/// <item>Hilo anclado a una Empresa contraparte con Centro en el Cliente de la cartera, que el Gestor gestiona: visible.</item>
/// <item>Hilo anclado a una Empresa contraparte que solo tiene Centro en el otro Cliente: no visible.</item>
/// <item>Hilo sin Cliente empresarial ni Empresa (triage compartido): visible.</item>
/// </list>
/// La siembra va como propietario de la base, porque no es lo que se mide.
/// </para>
/// </summary>
public class BloqueoRespuestaSugeridaPorBuzonConCarteraTests : IAsyncLifetime
{
    private const string Firma = "<p>Un saludo, Marta</p>";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestor = Guid.NewGuid();
    private readonly Guid _administrador = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtimeDelGestor = null!;
    private CaeManagerDbContext _runtimeDelAdministrador = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _conexion;
    private Hilo _clienteDeLaCartera = null!;
    private Hilo _clienteFueraDeLaCartera = null!;
    private Hilo _empresaQueGestiona = null!;
    private Hilo _empresaQueNoGestiona = null!;
    private Hilo _triageCompartido = null!;

    private sealed record Hilo(Guid ConversacionId, string Texto);

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        _propietario.Tenants.Add(tenantSesion);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;

        await SembrarAsync();

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
        _runtimeDelGestor = CrearRuntime(UsuarioGestor());
        _runtimeDelAdministrador = CrearRuntime(UsuarioAdministrador());
    }

    public async Task DisposeAsync()
    {
        await _runtimeDelAdministrador.DisposeAsync();
        await _runtimeDelGestor.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>
    /// Control del instrumento: la lectura va de verdad bajo RLS y los cinco hilos y sus sugerencias existen, de modo que
    /// un «no bloquea» de los tests de abajo mide la cartera y no una siembra que no se hizo.
    /// </summary>
    [Fact]
    public async Task Los_cinco_hilos_existen_y_el_Administrador_los_ve_todos()
    {
        await using var propietario = CrearPropietarioLimpio();
        (await propietario.RespuestasSugeridas.IgnoreQueryFilters().CountAsync(r => r.TenantId == _tenantSesion)).Should().Be(5);

        var filtro = await BloqueoRespuestaSugerida.HilosVisiblesDelBuzonAsync(
            Alcance(_runtimeDelAdministrador, UsuarioAdministrador()), _conexion, CancellationToken.None);
        var visibles = await _runtimeDelAdministrador.Conversaciones.Where(filtro!).Select(c => c.Id).ToListAsync();

        visibles.Should().BeEquivalentTo(Todos().Select(h => h.ConversacionId));
    }

    /// <summary>
    /// El filtro, traducido a SQL, deja pasar exactamente los hilos que <c>ConversacionVisibleAsync</c> da por visibles
    /// para el Gestor CAE, y son los tres esperados: ni uno más (los de fuera de la cartera) ni uno menos.
    /// </summary>
    [Fact]
    public async Task El_filtro_en_SQL_decide_igual_que_ConversacionVisibleAsync_para_el_Gestor_de_cartera_restringida()
    {
        var alcance = Alcance(_runtimeDelGestor, UsuarioGestor());
        var filtro = await BloqueoRespuestaSugerida.HilosVisiblesDelBuzonAsync(alcance, _conexion, CancellationToken.None);

        var enSql = await _runtimeDelGestor.Conversaciones.Where(filtro!).Select(c => c.Id).ToListAsync();

        var todas = await _runtimeDelAdministrador.Conversaciones.AsNoTracking()
            .Where(c => c.ConexionIntegracionId == _conexion).ToListAsync();
        todas.Should().HaveCount(5);
        var esperadas = new List<Guid>();
        foreach (var conversacion in todas)
        {
            if (await alcance.ConversacionVisibleAsync(conversacion, CancellationToken.None))
                esperadas.Add(conversacion.Id);
        }

        enSql.Should().BeEquivalentTo(esperadas);
        enSql.Should().BeEquivalentTo(
            [_clienteDeLaCartera.ConversacionId, _empresaQueGestiona.ConversacionId, _triageCompartido.ConversacionId]);
    }

    [Fact]
    public async Task Eje_Cliente_empresarial_el_de_la_cartera_bloquea_y_el_de_fuera_no()
    {
        // Controles positivos: el texto exacto y el texto con una firma añadida, en el hilo de la cartera.
        (await Comprobar(_runtimeDelGestor, UsuarioGestor(), _clienteDeLaCartera.Texto)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        (await Comprobar(_runtimeDelGestor, UsuarioGestor(), _clienteDeLaCartera.Texto + Firma)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);

        // Negativos: lo mismo en el hilo de otro Cliente empresarial, que existe pero no es de su cartera.
        (await Comprobar(_runtimeDelGestor, UsuarioGestor(), _clienteFueraDeLaCartera.Texto)).EsExitoso.Should().BeTrue();
        (await Comprobar(_runtimeDelGestor, UsuarioGestor(), _clienteFueraDeLaCartera.Texto + Firma)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Eje_Empresa_contraparte_la_que_gestiona_bloquea_y_la_que_no_gestiona_no()
    {
        (await Comprobar(_runtimeDelGestor, UsuarioGestor(), _empresaQueGestiona.Texto)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        (await Comprobar(_runtimeDelGestor, UsuarioGestor(), _empresaQueGestiona.Texto + Firma)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);

        (await Comprobar(_runtimeDelGestor, UsuarioGestor(), _empresaQueNoGestiona.Texto)).EsExitoso.Should().BeTrue();
        (await Comprobar(_runtimeDelGestor, UsuarioGestor(), _empresaQueNoGestiona.Texto + Firma)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Un_hilo_sin_Cliente_empresarial_ni_Empresa_triage_compartido_sigue_siendo_visible()
    {
        (await Comprobar(_runtimeDelGestor, UsuarioGestor(), _triageCompartido.Texto)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        (await Comprobar(_runtimeDelGestor, UsuarioGestor(), _triageCompartido.Texto + Firma)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    /// <summary>
    /// Control positivo de los negativos: con alcance total (Administrador) los hilos de fuera de la cartera sí bloquean,
    /// así que el «no bloquea» del Gestor CAE se debe a su cartera y no a que el texto no se compare.
    /// </summary>
    [Fact]
    public async Task Con_alcance_total_los_hilos_de_fuera_de_la_cartera_si_bloquean()
    {
        foreach (var hilo in new[] { _clienteFueraDeLaCartera, _empresaQueNoGestiona })
        {
            (await Comprobar(_runtimeDelAdministrador, UsuarioAdministrador(), hilo.Texto)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
            (await Comprobar(_runtimeDelAdministrador, UsuarioAdministrador(), hilo.Texto + Firma)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
        }
    }

    /// <summary>
    /// La instantánea es REPEATABLE READ de verdad: una sugerencia que otra conexión confirma después de que la primera
    /// consulta fijara la foto no se ve dentro (mismo recuento), y fuera, con una consulta nueva, sí. Es lo que el doble en
    /// memoria no puede probar.
    /// </summary>
    [Fact]
    public async Task La_instantanea_no_ve_lo_que_otra_conexion_confirma_despues_de_su_primera_consulta()
    {
        await using var runtime = CrearRuntime(UsuarioGestor());
        IComunicacionesQueryContext comunicaciones = runtime;

        var (antes, durante) = await comunicaciones.EnInstantaneaDeLecturaAsync(async ct =>
        {
            var primero = await runtime.RespuestasSugeridas.CountAsync(ct);
            await SembrarConfirmadaPorOtraConexionAsync("Confirmada durante la instantánea");
            return (primero, await runtime.RespuestasSugeridas.CountAsync(ct));
        });

        durante.Should().Be(antes, "la foto se fijó en la primera consulta");
        (await runtime.RespuestasSugeridas.CountAsync()).Should().Be(antes + 1, "fuera de la instantánea la sugerencia confirmada ya se ve");
    }

    /// <summary>
    /// De extremo a extremo, con la comprobación dentro de una transacción REPEATABLE READ ya abierta (ambiente: no abre
    /// otra y comparte su foto): una sugerencia sin revisar con el mismo texto confirmada por otra conexión después de que la
    /// foto se fijara no se ve y no bloquea (límite aceptado); la misma comprobación con una foto nueva, sí bloquea. El
    /// control previo sin esa sugerencia no bloquea, así que el cambio de resultado viene de la sugerencia y no del texto.
    /// </summary>
    [Fact]
    public async Task Una_sin_revisar_confirmada_despues_de_fijarse_la_foto_no_se_ve_y_con_una_foto_nueva_si()
    {
        await using var runtime = CrearRuntime(UsuarioGestor());
        (await Comprobar(runtime, UsuarioGestor(), TextoConfirmadoDespues)).EsExitoso.Should().BeTrue();

        await using (var foto = await runtime.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead))
        {
            await runtime.RespuestasSugeridas.CountAsync();
            await SembrarConfirmadaPorOtraConexionAsync("Confirmada tras fijarse la foto");

            (await Comprobar(runtime, UsuarioGestor(), TextoConfirmadoDespues + Firma)).EsExitoso.Should().BeTrue();
        }

        (await Comprobar(runtime, UsuarioGestor(), TextoConfirmadoDespues + Firma)).Error.Should().Be(BloqueoRespuestaSugerida.SugerenciaSinRevisar);
    }

    /// <summary>La confirma otra conexión (el propietario de la base, no la del Gestor CAE), en un hilo de triage compartido.</summary>
    private async Task SembrarConfirmadaPorOtraConexionAsync(string asunto)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);
        await SembrarHiloAsync(asunto, null, null, TextoConfirmadoDespues, sinRevisar: true);
    }

    private const string TextoConfirmadoDespues = "<p>Texto confirmado por otra conexión después de abrir la instantánea, con su documentación.</p>";

    private Task<Result> Comprobar(CaeManagerDbContext runtime, ICurrentUserService usuario, string cuerpo)
    {
        var alcance = Alcance(runtime, usuario);
        return runtime.ComprobarEnvioPorBuzonAsync(alcance, alcance, _conexion, cuerpo, CancellationToken.None);
    }

    private IEnumerable<Hilo> Todos() =>
        [_clienteDeLaCartera, _clienteFueraDeLaCartera, _empresaQueGestiona, _empresaQueNoGestiona, _triageCompartido];

    private CurrentUserServiceFalso UsuarioGestor() => new(_gestor, Roles.GestorCae, tenantOrigenId: _tenantSesion);

    private CurrentUserServiceFalso UsuarioAdministrador() => new(_administrador, Roles.Administrador, tenantOrigenId: _tenantSesion);

    private AlcanceDatosService Alcance(CaeManagerDbContext contexto, ICurrentUserService usuario) =>
        new(contexto, usuario, _tenantDeLaPeticion, new SesionPrivilegiadaAusente());

    private async Task SembrarAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);
        var ahora = DateTime.UtcNow;

        var clienteDeLaCartera = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var clienteFuera = Empresa.CrearComoCliente("Central Nuclear de Springfield", "B87654323", false, null, null);
        var propia = new Empresa("Montajes Springfield S.L.", "B66778895");
        var subcontrataQueGestiona = Empresa.CrearComoSubcontrata("Aislamientos Shelbyville S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        var subcontrataQueNoGestiona = Empresa.CrearComoSubcontrata("Soldaduras Ogdenville S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        _propietario.Empresas.AddRange(clienteDeLaCartera, clienteFuera, propia, subcontrataQueGestiona, subcontrataQueNoGestiona);
        await _propietario.SaveChangesAsync();

        // La Empresa contraparte que el Gestor gestiona es la que trabaja en un Centro de un Cliente de su cartera.
        _propietario.Centros.AddRange(
            new Centro(clienteDeLaCartera.Id, propia.Id, "Fábrica de Springfield"),
            new Centro(clienteDeLaCartera.Id, subcontrataQueGestiona.Id, "Almacén de Shelbyville"),
            new Centro(clienteFuera.Id, subcontrataQueNoGestiona.Id, "Sector 7G"));

        // Cartera del Gestor CAE: el Tenant entero en la cartera, acotado a UN Cliente empresarial en la operación.
        var operacion = AsignacionOperacion.Interna(
            _tenantSesion, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteDeLaCartera.Id), ahora, null, ahora);
        _propietario.AsignacionesOperacion.Add(operacion);
        _propietario.AsignacionesCartera.Add(AsignacionCartera.Interna(operacion, _gestor, AmbitoAsignacion.Universal, ahora, null, ahora));

        var conexion = new ConexionIntegracion("cae@example.test", "Buzón CAE");
        _propietario.ConexionesIntegracion.Add(conexion);
        await _propietario.SaveChangesAsync();
        _conexion = conexion.Id;

        _clienteDeLaCartera = await SembrarHiloAsync(
            "Cliente de la cartera", clienteDeLaCartera.Id, null, "<p>Texto del hilo del Cliente empresarial de la cartera, con su documentación.</p>");
        // Los textos de los hilos ocultos no se parecen a los visibles: el bloqueo también compara por parecido (a lo sumo 15
        // caracteres cambiados), y un negativo con un texto a seis letras de uno visible bloquearía por el visible, no por él.
        _clienteFueraDeLaCartera = await SembrarHiloAsync(
            "Cliente fuera de la cartera", clienteFuera.Id, null, "<p>Revisados los certificados de la central: faltan dos reconocimientos médicos.</p>");
        _empresaQueGestiona = await SembrarHiloAsync(
            "Empresa que gestiona", null, subcontrataQueGestiona.Id, "<p>Texto del hilo de la Empresa contraparte que el Gestor gestiona, con su documentación.</p>");
        _empresaQueNoGestiona = await SembrarHiloAsync(
            "Empresa que no gestiona", null, subcontrataQueNoGestiona.Id, "<p>Las soldaduras siguen pendientes de acreditar el recurso preventivo en obra.</p>");
        _triageCompartido = await SembrarHiloAsync(
            "Triage compartido", null, null, "<p>Texto del hilo sin Cliente empresarial ni Empresa, en triage compartido, con su documentación.</p>");
    }

    private async Task<Hilo> SembrarHiloAsync(string asunto, Guid? clienteId, Guid? empresaId, string texto, bool sinRevisar = false)
    {
        var conversacion = new Conversacion(asunto, clienteId, empresaId: empresaId);
        conversacion.AsociarConexion(_conexion, "hilo-" + Guid.NewGuid());
        var mensaje = new Mensaje(
            conversacion.Id, DireccionMensaje.Entrante, CanalConversacion.Correo, "remitente@example.test", "Cuerpo", DateTime.UtcNow);
        _propietario.Conversaciones.Add(conversacion);
        _propietario.Mensajes.Add(mensaje);
        await _propietario.SaveChangesAsync();

        var sugerencia = RespuestaSugerida.DeVega(conversacion.Id, mensaje.Id, texto, Guid.NewGuid(), DateTime.UtcNow);
        if (sinRevisar)
            sugerencia.Insertar();
        _propietario.RespuestasSugeridas.Add(sugerencia);
        await _propietario.SaveChangesAsync();
        return new Hilo(conversacion.Id, texto);
    }

    private CaeManagerDbContext CrearPropietarioLimpio()
    {
        var tenant = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion)
            .AddInterceptors(new TenantSelladoInterceptor(tenant))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private CaeManagerDbContext CrearRuntime(ICurrentUserService usuario)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new ConcurrenciaOptimistaInterceptor(),
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantDeLaPeticion);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
