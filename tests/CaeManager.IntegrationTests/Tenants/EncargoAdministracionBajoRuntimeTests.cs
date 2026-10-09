using System.Runtime.CompilerServices;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.RegistrarEncargoAdministracion;
using CaeManager.Application.Tenants.Commands.RetirarEncargoAdministracion;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Tenants.Queries.ObtenerEncargosAdministracion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.IntegrationTests.Arranque;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// El Encargo de administración (decisión D-8, 2026-10-08) contra PostgreSQL
/// real, autenticando como <c>cae_app_runtime</c> y con Identity de verdad
/// (<see cref="ArnesDeArranqueRuntime"/>). Las reglas de quién puede y qué rol
/// resulta están probadas en Application con dobles; lo que solo esta capa puede
/// probar es:
///
/// <list type="bullet">
/// <item>que la política <c>posicion_en_el_encargo</c> deja leer al Operador CAE
/// externo su encargo y no le deja insertarlo ni retirarlo, aunque Application
/// fallara, y que un tercer Tenant no ve nada;</item>
/// <item>que el registro solo añade también en los privilegios: nadie borra ni
/// reescribe la cláusula con el rol de runtime;</item>
/// <item>que el techo se calcula bien leyendo el perfil de Propiedad de la
/// cuenta en Identity desde el Tenant propietario, donde esa cuenta no es
/// miembro (RLS de las cuentas);</item>
/// <item>que retirar el encargo baja el techo en la siguiente resolución de un
/// circuito ya abierto, y corta su selección.</item>
/// </list>
///
/// <para>
/// Las organizaciones son las de un caso sin nombre propio: un Operador CAE
/// externo, el Tenant propietario que le encarga su administración, otro Tenant
/// propietario que opera sin encargo, y otro Operador CAE externo con su propio
/// Tenant propietario encargado.
/// </para>
/// </summary>
public class EncargoAdministracionBajoRuntimeTests : IAsyncLifetime
{
    private const string Contrasena = "Arnes#2026Seguro";
    private const string Clausula = "Cláusula 7.ª del contrato de servicio";

    private readonly ActorMutable _actor = new();
    private readonly CurrentUserServiceMutable _sesion = new();
    private readonly TenantDelArnes _tenantActual = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private Guid _propietario;
    private Guid _propietarioSinEncargo;
    private Guid _propietarioRelevado;
    private Guid _propietarioDelOtro;
    private Guid _operador;
    private Guid _otroOperador;
    private Guid _tercero;

    private Guid _operacion;
    private Guid _operacionSinEncargo;
    private Guid _operacionRelevada;
    private Guid _operacionDelOtro;

    private Guid _encargo;
    private Guid _encargoDelOtro;
    private Guid _clienteEmpresarial;

    private ApplicationUser _adminOperador = null!;
    private ApplicationUser _direccionOperador = null!;
    private ApplicationUser _gestorOperador = null!;
    private ApplicationUser _adminOperadorSinCartera = null!;
    private ApplicationUser _adminOperadorCarteraParcial = null!;
    private ApplicationUser _adminOtroOperador = null!;
    private ApplicationUser _adminPropietario = null!;
    private ApplicationUser _adminTercero = null!;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: _tenantActual,
            actorAuditoriaPersonalizado: _actor,
            currentUserServicePersonalizado: _sesion);

        await using (var siembra = ContextoPropietarioDeLaBase())
        {
            _propietario = SembrarTenant(siembra, "Tenant propietario con encargo");
            _propietarioSinEncargo = SembrarTenant(siembra, "Tenant propietario sin encargo");
            _propietarioRelevado = SembrarTenant(siembra, "Tenant propietario que cambió de Operador CAE");
            _propietarioDelOtro = SembrarTenant(siembra, "Tenant propietario del otro Operador CAE");
            _operador = SembrarTenant(siembra, "Operador CAE externo", operadorCaeExterno: true);
            _otroOperador = SembrarTenant(siembra, "Otro Operador CAE externo", operadorCaeExterno: true);
            _tercero = SembrarTenant(siembra, "Tercer Tenant sin relación");
            await siembra.SaveChangesAsync();
        }

        _adminOperador = await SembrarUsuarioAsync("admin-operador", _operador, Roles.Administrador);
        _direccionOperador = await SembrarUsuarioAsync("direccion-operador", _operador, Roles.DireccionCae);
        _gestorOperador = await SembrarUsuarioAsync("gestor-operador", _operador, Roles.GestorCae);
        _adminOperadorSinCartera = await SembrarUsuarioAsync("admin-sin-cartera", _operador, Roles.Administrador);
        _adminOperadorCarteraParcial = await SembrarUsuarioAsync("admin-cartera-parcial", _operador, Roles.Administrador);
        _adminOtroOperador = await SembrarUsuarioAsync("admin-otro-operador", _otroOperador, Roles.Administrador);
        _adminPropietario = await SembrarUsuarioAsync("admin-propietario", _propietario, Roles.Administrador);
        _adminTercero = await SembrarUsuarioAsync("admin-tercero", _tercero, Roles.Administrador);

        await using (var siembra = ContextoPropietarioDeLaBase())
        {
            var ahora = DateTime.UtcNow;
            var desde = ahora.AddDays(-30);

            var operacion = Externa(_propietario, _operador, desde, ahora);
            var operacionSinEncargo = Externa(_propietarioSinEncargo, _operador, desde, ahora);
            var operacionDelOtro = Externa(_propietarioDelOtro, _otroOperador, desde, ahora);
            // El Tenant relevado: lo operó el otro Operador CAE, con encargo; esa operación se cerró
            // sin retirar el encargo y ahora lo opera este Operador CAE, sin encargo.
            var operacionAnterior = Externa(_propietarioRelevado, _otroOperador, desde, ahora);
            siembra.AsignacionesOperacion.AddRange(operacion, operacionSinEncargo, operacionDelOtro, operacionAnterior);

            var encargo = Encargar(operacion, _adminPropietario.Id, ahora.AddDays(-10));
            var encargoDelOtro = Encargar(operacionDelOtro, Guid.NewGuid(), ahora.AddDays(-10));
            var encargoAnterior = Encargar(operacionAnterior, Guid.NewGuid(), ahora.AddDays(-10));
            siembra.EncargosAdministracion.AddRange(encargo, encargoDelOtro, encargoAnterior);
            await siembra.SaveChangesAsync();

            operacionAnterior.Cerrar(MotivoCierreAsignacion.Transferida, ahora.AddDays(-5));
            await siembra.SaveChangesAsync();
            var operacionRelevada = Externa(_propietarioRelevado, _operador, ahora.AddDays(-5), ahora);
            siembra.AsignacionesOperacion.Add(operacionRelevada);

            siembra.AsignacionesCartera.AddRange(
                Cartera(operacion, _adminOperador.Id, Roles.GestorCae, ahora),
                Cartera(operacion, _direccionOperador.Id, Roles.CoordinadorCae, ahora),
                Cartera(operacion, _gestorOperador.Id, Roles.GestorCae, ahora),
                Cartera(operacionSinEncargo, _adminOperador.Id, Roles.GestorCae, ahora),
                Cartera(operacionRelevada, _adminOperador.Id, Roles.GestorCae, ahora),
                Cartera(operacionDelOtro, _adminOtroOperador.Id, Roles.GestorCae, ahora));
            await siembra.SaveChangesAsync();

            _operacion = operacion.Id;
            _operacionSinEncargo = operacionSinEncargo.Id;
            _operacionRelevada = operacionRelevada.Id;
            _operacionDelOtro = operacionDelOtro.Id;
            _encargo = encargo.Id;
            _encargoDelOtro = encargoDelOtro.Id;
        }

        // Un Cliente empresarial en el Tenant propietario: distingue el alcance total (sin lista)
        // del alcance por cartera (la lista que lo contiene) y del alcance cero (lista vacía).
        await using (var siembra = ContextoPropietarioDeLaBase(_propietario))
        {
            var cliente = Empresa.CrearComoCliente("Cliente de prueba", "B10380186", false, null, null);
            siembra.Empresas.Add(cliente);
            // Una cartera PARCIAL (acotada a un Centro) bajo la operación que tiene encargo: es la
            // de un Administrador del Operador CAE al que el encargo no debe ampliarle el ámbito.
            var centro = new Centro(cliente.Id, cliente.Id, "Centro de la cartera parcial");
            siembra.Centros.Add(centro);
            await siembra.SaveChangesAsync();
            _clienteEmpresarial = cliente.Id;

            var ahora = DateTime.UtcNow;
            var operacion = await siembra.AsignacionesOperacion.SingleAsync(o => o.Id == _operacion);
            siembra.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _adminOperadorCarteraParcial.Id, Roles.GestorCae,
                new AmbitoAsignacion(CentroId: centro.Id), ahora.AddDays(-1), null, ahora));
            await siembra.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    // ── posicion_en_el_encargo: quién lee ─────────────────────────────────

    [Fact]
    public async Task El_encargo_lo_leen_el_Tenant_propietario_y_su_Operador_CAE_y_nadie_mas()
    {
        (await EncargosVisiblesAsync(_adminPropietario, _propietario)).Should().Equal([_encargo],
            "el Tenant propietario ve el encargo que ha dado");
        (await EncargosVisiblesAsync(_adminOperador, _propietario)).Should().Equal([_encargo],
            "el Operador CAE externo lo lee dentro del Tenant propietario para calcular su techo");
        (await EncargosVisiblesAsync(_adminOperador, _operador)).Should().Equal([_encargo],
            "y también desde su propia organización, por su Tenant de origen; no ve los de otros Operadores CAE");

        (await EncargosVisiblesAsync(_adminTercero, _tercero)).Should().BeEmpty(
            "un tercer Tenant no está en ninguna de las dos posiciones");
        (await EncargosVisiblesAsync(_adminOtroOperador, _otroOperador)).Should().NotContain(_encargo,
            "otro Operador CAE externo no lee un encargo que no es suyo")
            .And.Contain(_encargoDelOtro, "control: el suyo sí lo lee");
    }

    // ── posicion_en_el_encargo: quién escribe ─────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_Operador_CAE_no_retira_su_propio_encargo_con_el_rol_de_runtime(bool dentroDelTenantPropietario)
    {
        // Sin pasar por Application: la entidad se retira y se guarda directamente. Lo que lo impide
        // es el WITH CHECK, que es justo lo que quedaría si AutoridadSobreElEncargo tuviera un defecto.
        using (var ambito = Sesion(_adminOperador, dentroDelTenantPropietario ? _propietario : _operador))
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var encargo = await contexto.EncargosAdministracion.SingleAsync(e => e.Id == _encargo);
            encargo.Retirar(_adminOperador.Id, DateTime.UtcNow);

            var guardar = async () => await contexto.SaveChangesAsync();

            (await guardar.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var comprobacion = ContextoPropietarioDeLaBase();
        (await comprobacion.EncargosAdministracion.SingleAsync(e => e.Id == _encargo)).RetiradoEnUtc.Should().BeNull();
    }

    [Fact]
    public async Task El_Operador_CAE_no_registra_un_encargo_a_su_favor_con_el_rol_de_runtime()
    {
        AsignacionOperacion operacion;
        await using (var lectura = ContextoPropietarioDeLaBase())
            operacion = await lectura.AsignacionesOperacion.AsNoTracking().SingleAsync(o => o.Id == _operacionSinEncargo);

        using (var ambito = Sesion(_adminOperador, _propietarioSinEncargo))
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            contexto.EncargosAdministracion.Add(Encargar(operacion, _adminOperador.Id, DateTime.UtcNow));

            var guardar = async () => await contexto.SaveChangesAsync();

            (await guardar.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var comprobacion = ContextoPropietarioDeLaBase();
        (await comprobacion.EncargosAdministracion.AnyAsync(e => e.AsignacionOperacionId == _operacionSinEncargo))
            .Should().BeFalse();
    }

    [Fact]
    public async Task El_Administrador_propio_retira_y_vuelve_a_registrar_pero_nadie_borra_ni_reescribe_la_clausula()
    {
        using (var ambito = Sesion(_adminPropietario, _propietario))
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

            // Solo añade: ni DELETE ni UPDATE de lo que no sea la retirada, tampoco para quien sí
            // puede retirarlo. Va antes de retirar para que la fila siga siendo la vigente.
            (await SqlStateDeAsync(contexto, $"""DELETE FROM "EncargosAdministracion" WHERE "Id" = '{_encargo}';"""))
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege, "un encargo no se borra: se retira");
            (await SqlStateDeAsync(contexto,
                    $"""UPDATE "EncargosAdministracion" SET "ClausulaContrato" = 'Otra' WHERE "Id" = '{_encargo}';"""))
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la cláusula no se reescribe");
            (await SqlStateDeAsync(contexto,
                    $"""UPDATE "EncargosAdministracion" SET "VigenciaHasta" = now() + interval '10 years' WHERE "Id" = '{_encargo}';"""))
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la vigencia no se alarga");

            // Control positivo, con los handlers de producción: el Administrador propio sí retira...
            var retirada = await Retirar(ambito).Handle(new RetirarEncargoAdministracionCommand(_encargo), CancellationToken.None);
            retirada.EsExitoso.Should().BeTrue(retirada.EsFallido ? retirada.Error.Codigo : string.Empty);
        }

        using (var ambito = Sesion(_adminPropietario, _propietario))
        {
            // ...y registra otro, que es la única forma de «modificarlo».
            var registro = await Registrar(ambito).Handle(
                new RegistrarEncargoAdministracionCommand(_operacion, "Cláusula 9.ª, tras la novación", null),
                CancellationToken.None);
            registro.EsExitoso.Should().BeTrue(registro.EsFallido ? registro.Error.Codigo : string.Empty);
        }

        await using var comprobacion = ContextoPropietarioDeLaBase();
        var encargos = await comprobacion.EncargosAdministracion
            .Where(e => e.AsignacionOperacionId == _operacion).OrderBy(e => e.RegistradoEnUtc).ToListAsync();
        encargos.Should().HaveCount(2);
        encargos[0].ClausulaContrato.Should().Be(Clausula);
        encargos[0].RetiradoPorUsuarioId.Should().Be(_adminPropietario.Id);
        encargos[1].RetiradoEnUtc.Should().BeNull();
        encargos[1].Origen.Should().Be(OrigenEncargoAdministracion.AdministradorPropio);
        encargos[1].RegistradoPorUsuarioId.Should().Be(_adminPropietario.Id);
    }

    // ── Los disparadores: lo que los privilegios por columna no cierran ───

    /// <summary>
    /// Corrección C4 (2026-10-09). El rol de runtime puede actualizar las dos columnas de la
    /// retirada, así que sin el disparador podía también deshacerla (y devolver el techo de
    /// Propiedad al Operador CAE) o cambiarle el autor. Por eso lo que se espera es
    /// <c>check_violation</c> y no <c>insufficient_privilege</c>: el privilegio lo tiene.
    /// </summary>
    [Fact]
    public async Task Con_el_rol_de_runtime_una_retirada_ni_se_escribe_a_medias_ni_se_deshace_ni_se_reescribe()
    {
        using (var ambito = Sesion(_adminPropietario, _propietario))
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

            // A medias, sobre el encargo vigente: un autor sin instante lo dejaría vigente con el
            // autor ya escrito, y un instante sin autor lo retiraría sin decir quién.
            (await SqlStateDeAsync(contexto,
                    $"""UPDATE "EncargosAdministracion" SET "RetiradoPorUsuarioId" = '{_adminTercero.Id}' WHERE "Id" = '{_encargo}';"""))
                .Should().Be(PostgresErrorCodes.CheckViolation, "la retirada se escribe entera: autor e instante a la vez");
            (await SqlStateDeAsync(contexto,
                    $"""UPDATE "EncargosAdministracion" SET "RetiradoEnUtc" = now() WHERE "Id" = '{_encargo}';"""))
                .Should().Be(PostgresErrorCodes.CheckViolation, "la retirada se escribe entera: autor e instante a la vez");

            // Control positivo: la retirada entera, con el handler de producción y este mismo rol.
            var retirada = await Retirar(ambito).Handle(new RetirarEncargoAdministracionCommand(_encargo), CancellationToken.None);
            retirada.EsExitoso.Should().BeTrue(retirada.EsFallido ? retirada.Error.Codigo : string.Empty);

            (await SqlStateDeAsync(contexto,
                    $"""UPDATE "EncargosAdministracion" SET "RetiradoEnUtc" = NULL, "RetiradoPorUsuarioId" = NULL WHERE "Id" = '{_encargo}';"""))
                .Should().Be(PostgresErrorCodes.CheckViolation, "una retirada no se deshace: para volver a encargar se registra otro");
            (await SqlStateDeAsync(contexto,
                    $"""UPDATE "EncargosAdministracion" SET "RetiradoPorUsuarioId" = '{_adminTercero.Id}' WHERE "Id" = '{_encargo}';"""))
                .Should().Be(PostgresErrorCodes.CheckViolation, "el autor de la retirada no se reescribe");
            (await SqlStateDeAsync(contexto,
                    $"""UPDATE "EncargosAdministracion" SET "RetiradoEnUtc" = now() + interval '1 day' WHERE "Id" = '{_encargo}';"""))
                .Should().Be(PostgresErrorCodes.CheckViolation, "el instante de la retirada no se reescribe");
        }

        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        (await SqlStateDeAsync(propietarioDeLaBase,
                $"""UPDATE "EncargosAdministracion" SET "RetiradoEnUtc" = NULL, "RetiradoPorUsuarioId" = NULL WHERE "Id" = '{_encargo}';"""))
            .Should().Be(PostgresErrorCodes.CheckViolation, "el disparador vale para cualquier rol, también para el propietario de la tabla");

        var fila = await propietarioDeLaBase.EncargosAdministracion.AsNoTracking().SingleAsync(e => e.Id == _encargo);
        fila.RetiradoPorUsuarioId.Should().Be(_adminPropietario.Id);
        fila.RetiradoEnUtc.Should().NotBeNull();
    }

    /// <summary>
    /// Corrección C4 (2026-10-09). La clave foránea ata (operación, Tenant propietario), no el
    /// Operador CAE: sin el disparador, una fila podía nombrar como beneficiario a un Operador CAE
    /// que no es el de esa operación. La política no lo impide (quien inserta es el Administrador
    /// propio, desde su Tenant): tiene que salir <c>check_violation</c>, del disparador.
    /// </summary>
    [Fact]
    public async Task Con_el_rol_de_runtime_no_se_registra_un_encargo_a_nombre_de_un_Operador_CAE_que_no_es_el_de_la_operacion()
    {
        var adminPropio = await SembrarUsuarioAsync("admin-propietario-sin-encargo", _propietarioSinEncargo, Roles.Administrador);

        AsignacionOperacion operacion;
        await using (var lectura = ContextoPropietarioDeLaBase())
            operacion = await lectura.AsignacionesOperacion.AsNoTracking().SingleAsync(o => o.Id == _operacionSinEncargo);

        using (var ambito = Sesion(adminPropio, _propietarioSinEncargo))
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            // Fila que el dominio no deja construir: la operación es de _operador y el encargo nombra a otro.
            var aNombreDeOtro = Encargar(operacion, adminPropio.Id, DateTime.UtcNow);
            typeof(EncargoAdministracion).GetProperty(nameof(EncargoAdministracion.OperadorTenantId))!
                .SetValue(aNombreDeOtro, _otroOperador);
            contexto.EncargosAdministracion.Add(aNombreDeOtro);

            var guardar = async () => await contexto.SaveChangesAsync();

            (await guardar.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        }

        // Control positivo: la misma inserción, por el mismo rol y desde la misma posición, entra
        // cuando nombra al Operador CAE de la operación. Sin él, el rojo de arriba podría ser de
        // un disparador que no ve la operación bajo RLS y lo rechaza todo.
        using (var ambito = Sesion(adminPropio, _propietarioSinEncargo))
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            contexto.EncargosAdministracion.Add(Encargar(operacion, adminPropio.Id, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await using var comprobacion = ContextoPropietarioDeLaBase();
        (await comprobacion.EncargosAdministracion.AsNoTracking()
                .Where(e => e.AsignacionOperacionId == _operacionSinEncargo).Select(e => e.OperadorTenantId).ToListAsync())
            .Should().Equal(_operador);
    }

    // ── El tercer acto excluido, con Identity y la base de verdad ─────────

    [Fact]
    public async Task Quien_administra_por_encargo_no_es_Administrador_propio_del_Tenant_propietario()
    {
        // La pertenencia es la mitad del predicado que distingue a quien encarga de quien recibe:
        // dentro del Tenant propietario, el Administrador del Operador CAE externo tiene rol
        // Administrador en Identity, rol efectivo Administrador y su cuenta es visible para sí
        // mismo. Lo único que lo separa del Administrador propio es de qué Tenant es miembro.
        using (var ambito = Sesion(_adminOperador, _propietario))
        {
            var predicado = new AdministradorDelTenantPropietarioEnBase(
                ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());

            (await predicado.EsAdministradorEnBaseAsync(_adminOperador.Id, _propietario)).Should().BeFalse(
                "es Administrador de su organización, no miembro del Tenant propietario");
            (await predicado.EsAdministradorEnBaseAsync(_adminOperador.Id, _operador)).Should().BeTrue(
                "control: en su Tenant de origen sí lo es, así que la cuenta y su rol se leen bien desde aquí");
        }

        using (var ambito = Sesion(_adminPropietario, _propietario))
        {
            var predicado = new AdministradorDelTenantPropietarioEnBase(
                ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
            (await predicado.EsAdministradorEnBaseAsync(_adminPropietario.Id, _propietario)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Quien_administra_por_encargo_ni_retira_ni_registra_ni_lista_el_encargo_por_los_handlers()
    {
        using (var ambito = Sesion(_adminOperador, _propietario))
        {
            // Precondición: de verdad administra por encargo (si no, el rechazo no probaría nada).
            (await Techo(ambito).ResolverAsync(
                    _adminOperador.Id, _operador, _propietario, _operacion, Roles.Administrador, DateTime.UtcNow, CancellationToken.None))
                .Should().Be(new RolEfectivoPorOperacion(Roles.Administrador, _encargo));

            var retirada = await Retirar(ambito).Handle(new RetirarEncargoAdministracionCommand(_encargo), CancellationToken.None);
            retirada.Error.Codigo.Should().Be(AutoridadSobreElEncargo.NoAutorizado.Codigo);

            (await Listar(ambito).Handle(new ObtenerEncargosAdministracionQuery(), CancellationToken.None))
                .Should().BeEmpty("la pantalla del encargo no es de quien lo recibe");
        }

        using (var ambito = Sesion(_adminOperador, _propietarioSinEncargo))
        {
            var registro = await Registrar(ambito).Handle(
                new RegistrarEncargoAdministracionCommand(_operacionSinEncargo, Clausula, null), CancellationToken.None);
            registro.Error.Codigo.Should().Be(AutoridadSobreElEncargo.NoAutorizado.Codigo);
        }

        using (var ambito = Sesion(_adminPropietario, _propietario))
        {
            var lista = await Listar(ambito).Handle(new ObtenerEncargosAdministracionQuery(), CancellationToken.None);
            var fila = lista.Should().ContainSingle("control: el Administrador propio sí lo ve").Subject;
            fila.Id.Should().Be(_encargo);
            fila.OperadorNombre.Should().StartWith("Operador CAE externo");
            fila.Estado.Should().Be(EstadoEncargoAdministracion.Vigente);
        }
    }

    // ── El techo, leyendo Identity bajo RLS desde el Tenant propietario ───

    [Fact]
    public async Task Con_cartera_y_encargo_el_perfil_de_Propiedad_en_origen_es_el_rol_efectivo()
    {
        (await RolEnAsync(_adminOperador, _propietario, _operacion, Roles.Administrador))
            .Should().Be(new RolEfectivoPorOperacion(Roles.Administrador, _encargo));
        (await RolEnAsync(_direccionOperador, _propietario, _operacion, Roles.DireccionCae))
            .Should().Be(new RolEfectivoPorOperacion(Roles.DireccionCae, _encargo));
        (await RolEnAsync(_gestorOperador, _propietario, _operacion, Roles.GestorCae))
            .Should().Be(new RolEfectivoPorOperacion(Roles.GestorCae, null),
                "quien no es Administrador ni Dirección CAE en su organización entra con el rol de su cartera");
    }

    [Fact]
    public async Task Sin_cartera_no_hay_rol_ni_alcance_aunque_haya_encargo_vigente()
    {
        (await RolEnAsync(_adminOperadorSinCartera, _propietario, _operacion, Roles.Administrador))
            .Should().Be(RolEfectivoPorOperacion.Ninguno, "el encargo sube el techo de quien ya tiene acceso; no lo concede");

        using var ambito = Sesion(_adminOperadorSinCartera, _propietario);
        var circuito = CircuitoDe(ambito, _adminOperadorSinCartera, _propietario, _operacion, Roles.Administrador, encargoEnElClaim: null);
        (await circuito.Usuario.ObtenerRolEfectivoAsync()).Should().BeNull();
        (await circuito.Usuario.EncargoQueElevaAsync()).Should().BeNull();
        (await circuito.Alcance.ObtenerClienteIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty(
            "alcance cero: ni total ni el del Tenant entero");
    }

    /// <summary>
    /// Corrección C1 (2026-10-09). El rol de Propiedad tiene alcance total sin consultar carteras,
    /// así que elevar a quien tiene una cartera parcial le daba el Tenant entero. El control
    /// positivo es <see cref="Con_cartera_y_encargo_el_perfil_de_Propiedad_en_origen_es_el_rol_efectivo"/>:
    /// misma operación, mismo encargo y mismo perfil en origen, con cartera universal.
    /// </summary>
    [Fact]
    public async Task Con_cartera_parcial_el_encargo_no_eleva_y_el_alcance_sigue_siendo_el_de_la_cartera()
    {
        (await RolEnAsync(_adminOperadorCarteraParcial, _propietario, _operacion, Roles.Administrador))
            .Should().Be(new RolEfectivoPorOperacion(Roles.GestorCae, null),
                "el encargo sube el techo de quien ya tiene el Tenant entero; a una cartera parcial le ampliaría el ámbito");

        using var ambito = Sesion(_adminOperadorCarteraParcial, _propietario);
        var circuito = CircuitoDe(
            ambito, _adminOperadorCarteraParcial, _propietario, _operacion, Roles.Administrador, encargoEnElClaim: null);
        (await circuito.Usuario.ObtenerRolEfectivoAsync()).Should().Be(Roles.GestorCae);
        (await circuito.Usuario.EncargoQueElevaAsync()).Should().BeNull();
        // Una lista (aunque vacía) es alcance por cartera; null sería el alcance total del rol de
        // Propiedad. Hoy una cartera acotada a un Centro no concede ningún Cliente empresarial
        // (dimensión diferida, falla cerrado): lo que importa aquí es que no se vuelve total.
        (await circuito.Alcance.ObtenerClienteIdsVisiblesAsync()).Should().NotBeNull(
            "con cartera parcial el alcance es el de la cartera, nunca el total del Tenant propietario")
            .And.NotContain(_clienteEmpresarial);
    }

    [Fact]
    public async Task El_encargo_de_otro_Tenant_propietario_o_de_otro_Operador_CAE_no_eleva()
    {
        (await RolEnAsync(_adminOperador, _propietarioSinEncargo, _operacionSinEncargo, Roles.Administrador))
            .Should().Be(new RolEfectivoPorOperacion(Roles.GestorCae, null),
                "el mismo Operador CAE tiene encargo de otro Tenant propietario, no de este");

        // El Tenant relevado conserva sin retirar el encargo que dio al Operador CAE anterior.
        // Quien lo opera ahora no hereda ese techo: el encargo es de otra operación y de otro Operador CAE.
        (await RolEnAsync(_adminOperador, _propietarioRelevado, _operacionRelevada, Roles.Administrador))
            .Should().Be(new RolEfectivoPorOperacion(Roles.GestorCae, null),
                "el encargo dado a otro Operador CAE externo no sube el techo de este");

        // Y el del otro Operador CAE sobre su propio Tenant propietario tampoco sirve aquí.
        (await RolEnAsync(_adminOperador, _propietarioDelOtro, _operacionDelOtro, Roles.Administrador))
            .Should().Be(RolEfectivoPorOperacion.Ninguno, "ahí no tiene cartera ni posición");

        (await RolEnAsync(_adminOtroOperador, _propietarioDelOtro, _operacionDelOtro, Roles.Administrador))
            .Should().Be(new RolEfectivoPorOperacion(Roles.Administrador, _encargoDelOtro),
                "control: el otro Operador CAE sí administra por encargo el Tenant que se lo dio");
    }

    // ── Retirada con el circuito abierto ──────────────────────────────────

    [Fact]
    public async Task Retirado_el_encargo_la_siguiente_resolucion_del_mismo_circuito_devuelve_el_rol_de_cartera()
    {
        using var ambito = Sesion(_adminOperador, _propietario);
        var reloj = new RelojManual();
        var circuito = CircuitoDe(ambito, _adminOperador, _propietario, _operacion, Roles.Administrador, _encargo, reloj);

        (await circuito.Usuario.ObtenerRolEfectivoAsync()).Should().Be(Roles.Administrador);
        (await circuito.Usuario.EncargoQueElevaAsync()).Should().Be(_encargo);
        circuito.Usuario.EncargoDeLaUltimaResolucion(_operacion).Should().Be(_encargo);
        (await circuito.Alcance.ObtenerClienteIdsVisiblesAsync()).Should().BeNull(
            "con el rol elevado el alcance es el de toda la organización");

        await RetirarComoAdministradorPropioAsync();

        // El rol no se memoiza: la misma instancia (= el mismo circuito) baja en la siguiente llamada.
        (await circuito.Usuario.ObtenerRolEfectivoAsync()).Should().Be(Roles.GestorCae,
            "el techo vuelve al rol de la cartera en cuanto el encargo se retira");
        (await circuito.Usuario.EncargoQueElevaAsync()).Should().BeNull();
        circuito.Usuario.EncargoDeLaUltimaResolucion(_operacion).Should().BeNull(
            "la auditoría de la siguiente escritura ya no la marca como hecha por encargo");

        // El alcance sí se memoiza, con cota: dentro de la ventana sigue siendo el total...
        reloj.Avanzar(CaducidadDelAlcance - TimeSpan.FromSeconds(1));
        (await circuito.Alcance.ObtenerClienteIdsVisiblesAsync()).Should().BeNull(
            "dentro de la cota declarada la memoización del alcance se conserva");

        // ...y al caducar pasa a ser el de la cartera (el Tenant entero, ya como lista).
        reloj.Avanzar(TimeSpan.FromSeconds(1));
        (await circuito.Alcance.ObtenerClienteIdsVisiblesAsync()).Should().NotBeNull()
            .And.Contain(_clienteEmpresarial, "la cartera sigue vigente: retirar el encargo baja el techo, no quita acceso");
    }

    [Fact]
    public async Task Retirado_el_encargo_el_circuito_que_se_abrio_con_el_rol_elevado_pierde_su_seleccion()
    {
        using var ambito = Sesion(_adminOperador, _propietario);
        var circuito = CircuitoDe(ambito, _adminOperador, _propietario, _operacion, Roles.Administrador, _encargo);
        circuito.Seleccion.TenantIdSeleccionado.Should().Be(_propietario);

        var handler = circuito.Revalidacion(intervaloSegundos: 1);
        var blazor = (Circuit)RuntimeHelpers.GetUninitializedObject(typeof(Circuit));
        await handler.OnCircuitOpenedAsync(blazor, CancellationToken.None);
        try
        {
            // Control negativo: con el encargo vigente, varios ciclos del temporizador no cortan nada.
            await Task.Delay(TimeSpan.FromSeconds(3));
            circuito.Seleccion.TenantIdSeleccionado.Should().Be(_propietario);

            await RetirarComoAdministradorPropioAsync();

            (await EsperarHastaAsync(() => circuito.Seleccion.TenantIdSeleccionado is null, TimeSpan.FromSeconds(10)))
                .Should().BeTrue("las puertas de página del circuito vivían del claim congelado; la revalidación lo corta");
        }
        finally
        {
            await handler.OnCircuitClosedAsync(blazor, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Un_circuito_sin_el_claim_del_encargo_no_se_corta_al_retirarlo()
    {
        // El Gestor CAE del mismo Operador CAE nunca estuvo elevado: retirar el encargo no le afecta.
        using var ambito = Sesion(_gestorOperador, _propietario);
        var circuito = CircuitoDe(ambito, _gestorOperador, _propietario, _operacion, Roles.GestorCae, encargoEnElClaim: null);

        var handler = circuito.Revalidacion(intervaloSegundos: 1);
        var blazor = (Circuit)RuntimeHelpers.GetUninitializedObject(typeof(Circuit));
        await handler.OnCircuitOpenedAsync(blazor, CancellationToken.None);
        try
        {
            await RetirarComoAdministradorPropioAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));

            circuito.Seleccion.TenantIdSeleccionado.Should().Be(_propietario,
                "retirar el encargo baja el techo de quien estaba elevado; no quita la cartera de nadie");
        }
        finally
        {
            await handler.OnCircuitClosedAsync(blazor, CancellationToken.None);
        }
    }

    // ── Montaje ───────────────────────────────────────────────────────────

    private static readonly TimeSpan CaducidadDelAlcance = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Abre un ámbito de DI con las coordenadas de sesión de <paramref name="usuario"/>
    /// operando <paramref name="tenantActivo"/>: el interceptor de RLS fija con ellas
    /// <c>app.tenant_id</c>, <c>app.tenant_origen_id</c> y <c>app.usuario_id</c> al abrir la conexión.
    /// </summary>
    private IServiceScope Sesion(ApplicationUser usuario, Guid tenantActivo)
    {
        _sesion.UsuarioId = usuario.Id;
        _sesion.TenantOrigenId = usuario.TenantId;
        _actor.Actual = usuario.Id;
        _tenantActual.Fijado = tenantActivo;
        return _arnes.Servicios.CreateScope();
    }

    private async Task<List<Guid>> EncargosVisiblesAsync(ApplicationUser usuario, Guid tenantActivo)
    {
        using var ambito = Sesion(usuario, tenantActivo);
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return await contexto.EncargosAdministracion.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync();
    }

    private async Task<RolEfectivoPorOperacion> RolEnAsync(
        ApplicationUser usuario, Guid tenantActivo, Guid operacionId, string rolDeSesionEnOrigen)
    {
        using var ambito = Sesion(usuario, tenantActivo);
        return await Techo(ambito).ResolverAsync(
            usuario.Id, usuario.TenantId, tenantActivo, operacionId, rolDeSesionEnOrigen, DateTime.UtcNow, CancellationToken.None);
    }

    /// <summary>
    /// La retirada la hace otro circuito, otra persona. Aquí va con el rol propietario de la base
    /// y no con el handler: las coordenadas de sesión del arnés son compartidas, y cambiarlas
    /// mientras el circuito medido sigue abriendo conexiones le prestaría las de otra persona. Que
    /// el Administrador propio retira con el handler y como runtime lo prueba otro test.
    /// </summary>
    private async Task RetirarComoAdministradorPropioAsync()
    {
        await using var otroCircuito = ContextoPropietarioDeLaBase();
        var encargo = await otroCircuito.EncargosAdministracion.SingleAsync(e => e.Id == _encargo);
        encargo.Retirar(_adminPropietario.Id, DateTime.UtcNow);
        await otroCircuito.SaveChangesAsync();
    }

    private static TechoDeRolPorEncargo Techo(IServiceScope ambito)
    {
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return new TechoDeRolPorEncargo(contexto, contexto, new PerfilDePropiedadEnOrigenEnBase(
            ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()));
    }

    private AutoridadSobreElEncargo Autoridad(IServiceScope ambito) =>
        new(new SesionPrivilegiadaAusente(), _sesion, _actor, new AdministradorDelTenantPropietarioEnBase(
            ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()));

    private RegistrarEncargoAdministracionCommandHandler Registrar(IServiceScope ambito) =>
        new(_tenantActual, Autoridad(ambito),
            new EncargoAdministracionRepository(ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>()),
            TimeProvider.System);

    private RetirarEncargoAdministracionCommandHandler Retirar(IServiceScope ambito) =>
        new(_tenantActual, Autoridad(ambito),
            new EncargoAdministracionRepository(ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>()),
            TimeProvider.System);

    private ObtenerEncargosAdministracionQueryHandler Listar(IServiceScope ambito)
    {
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return new ObtenerEncargosAdministracionQueryHandler(_tenantActual, Autoridad(ambito), contexto, contexto, TimeProvider.System);
    }

    /// <summary>
    /// Las piezas de producción de un circuito de Blazor de <paramref name="usuario"/> con el
    /// Tenant propietario seleccionado: el <see cref="CurrentUserService"/> real (que es también
    /// la señal «actúa por encargo»), el servicio de alcance con su memoización, la selección
    /// con un token emitido por la propia clase de producción y el principal congelado.
    /// </summary>
    private CircuitoDePrueba CircuitoDe(
        IServiceScope ambito, ApplicationUser usuario, Guid tenantSeleccionado, Guid operacionId,
        string rolDeSesionEnOrigen, Guid? encargoEnElClaim, TimeProvider? reloj = null)
    {
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var proteccion = new EphemeralDataProtectionProvider();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
            new(TenantClaimsPrincipalFactory.TipoClaimTenantId, usuario.TenantId.ToString()),
            // Como lo deja RolEfectivoDelWorkspaceMiddleware: el rol ya reescrito y, aparte, el de la sesión en origen.
            new(ClaimTypes.Role, encargoEnElClaim is null ? Roles.GestorCae : rolDeSesionEnOrigen),
            new(RolEfectivoDelWorkspaceMiddleware.TipoClaimRolDeSesionOrigen, rolDeSesionEnOrigen),
        };
        if (encargoEnElClaim is { } encargoId)
            claims.Add(new Claim(RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion, encargoId.ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "prueba"));

        var http = new DefaultHttpContext { User = principal };
        http.Request.Headers.Cookie =
            $"{ClienteActivoSeleccionado.NombreCookie}={ClienteActivoSeleccionado.Proteger(proteccion, usuario.Id, tenantSeleccionado, operacionId)}";
        var accesor = new HttpContextAccessor { HttpContext = http };
        var seleccion = new ClienteActivoSeleccionado(accesor, proteccion);
        var estado = new PrincipalDelCircuito(principal);

        var dependencias = new ServiceCollection()
            .AddSingleton<CaeManager.Application.Operaciones.IOperacionesQueryContext>(contexto)
            .AddSingleton<ITenantsQueryContext>(contexto)
            .AddSingleton(Techo(ambito))
            .BuildServiceProvider();

        var usuarioActual = new CurrentUserService(estado, accesor, seleccion, dependencias);
        var alcance = new AlcanceDatosService(
            contexto, usuarioActual, _tenantActual, new SesionPrivilegiadaAusente(), vistaDemo: null,
            reloj ?? TimeProvider.System, Options.Create(new CaducidadAlcanceOptions { Caducidad = CaducidadDelAlcance }));

        return new CircuitoDePrueba(usuarioActual, alcance, seleccion, estado, contexto);
    }

    private sealed record CircuitoDePrueba(
        CurrentUserService Usuario, AlcanceDatosService Alcance, ClienteActivoSeleccionado Seleccion,
        AuthenticationStateProvider Estado, CaeManagerDbContext Contexto)
    {
        public RevalidacionCircuitoActivoHandler Revalidacion(int intervaloSegundos) =>
            new(Seleccion, Usuario, Usuario, Estado, Contexto, Contexto, new SesionPrivilegiadaAusente(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Circuit:RevalidacionIntervaloSegundos"] = intervaloSegundos.ToString(),
                    })
                    .Build(),
                NullLogger<RevalidacionCircuitoActivoHandler>.Instance);
    }

    private static async Task<string?> SqlStateDeAsync(CaeManagerDbContext contexto, string sql)
    {
        try
        {
            await contexto.Database.ExecuteSqlRawAsync(sql);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private static async Task<bool> EsperarHastaAsync(Func<bool> condicion, TimeSpan timeout)
    {
        var cronometro = System.Diagnostics.Stopwatch.StartNew();
        while (cronometro.Elapsed < timeout)
        {
            if (condicion()) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(150));
        }

        return condicion();
    }

    private static AsignacionOperacion Externa(Guid propietario, Guid operador, DateTime desde, DateTime ahora) =>
        AsignacionOperacion.Externa(
            propietario, operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, vigenciaHasta: null, ahora);

    private static AsignacionCartera Cartera(AsignacionOperacion operacion, Guid usuarioId, string rol, DateTime ahora) =>
        AsignacionCartera.Externa(operacion, usuarioId, rol, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);

    private static EncargoAdministracion Encargar(AsignacionOperacion operacion, Guid quien, DateTime ahora) =>
        EncargoAdministracion.Registrar(
            operacion, Clausula, EncargoAdministracion.VersionTextoVigente,
            OrigenEncargoAdministracion.AdministradorPropio, quien, ahora, vigenciaHasta: null);

    private static Guid SembrarTenant(CaeManagerDbContext contexto, string nombre, bool operadorCaeExterno = false)
    {
        var tenant = new Tenant(
            $"{nombre} {Guid.NewGuid():N}",
            operadorCaeExterno ? PerfilVocabularioTenant.Consultora : PerfilVocabularioTenant.ClienteDirecto);
        if (operadorCaeExterno)
            tenant.HabilitarComoOperadorCaeExterno();
        contexto.Tenants.Add(tenant);
        return tenant.Id;
    }

    private async Task<ApplicationUser> SembrarUsuarioAsync(string alias, Guid tenant, string rol)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{alias}-{Guid.NewGuid():N}@caemanager.local";
        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = alias,
            EmailConfirmed = true,
            TenantId = tenant,
        };

        using (AmbitoTenantExplicito.Establecer(tenant))
        {
            (await userManager.CreateAsync(usuario, Contrasena)).Succeeded.Should().BeTrue();
            (await userManager.AddToRoleAsync(usuario, rol)).Succeeded.Should().BeTrue();
        }

        return usuario;
    }

    /// <summary>
    /// Rol propietario de la base: solo siembra y comprueba. Lo que se prueba
    /// corre siempre por el arnés, como <c>cae_app_runtime</c>.
    /// </summary>
    private CaeManagerDbContext ContextoPropietarioDeLaBase(Guid? tenant = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario)
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// Como el <c>TenantActual</c> de la web: el ámbito explícito manda (la
    /// siembra de cuentas) y, sin él, el Tenant de la selección. Las pruebas de
    /// circuito no abren ámbito explícito a propósito: con él, el
    /// <see cref="CurrentUserService"/> real resolvería por la rama del fan-out.
    /// </summary>
    private sealed class TenantDelArnes : ITenantActual
    {
        public Guid? Fijado { get; set; }
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? Fijado;
    }

    private sealed class ActorMutable : IActorAuditoria
    {
        public Guid? Actual { get; set; }
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(Actual));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(Actual);
    }

    private sealed class PrincipalDelCircuito(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    /// <summary>La caducidad del alcance se mide con el reloj monotónico, no con el de pared.</summary>
    private sealed class RelojManual : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Avanzar(TimeSpan intervalo) => _ticks += intervalo.Ticks;
        public override long GetTimestamp() => _ticks;
    }
}
