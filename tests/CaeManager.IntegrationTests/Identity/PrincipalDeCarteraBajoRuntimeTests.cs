using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using CaeManager.Application.Usuarios.Commands.AsumirPrincipalDeOperacion;
using CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario;
using CaeManager.Application.Usuarios.Commands.DesignarGestorCaePrincipal;
using CaeManager.Application.Usuarios.Queries.ObtenerOperacionesSinPrincipal;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// Designar al principal de una Asignación de Operación y el relevo automático al Coordinador CAE
/// (ADR-011 § 2.7, enmienda 2026-10-08) contra PostgreSQL real, autenticando como
/// <c>cae_app_runtime</c> (RLS siempre aplica), con la estrategia de reintento, los interceptores,
/// el catálogo, el candado de cartera y las lecturas de Identity de producción.
///
/// <para>
/// Lo que solo esta capa puede probar: que apagar y encender la marca en dos guardados no viola
/// el índice único parcial, que no es diferible; que la cartera que el relevo emite al Coordinador
/// CAE se escribe con el Tenant propietario como ámbito y le abre ese Tenant —y a nadie más—;
/// que las carteras de apoyo no se tocan; y que dos cambios de marca a la vez, por dos
/// conexiones, no dejan dos principales ni ninguno por error. No hay candado por operación: lo
/// que serializa es la versión de la cartera del principal actual y el índice único.
/// </para>
///
/// <para>
/// También la alerta «sin principal», el escalado y «Asumir» (punto 4 de la misma enmienda): que la
/// alerta solo enseña operaciones del propio Operador CAE; que la cartera que emiten el escalado y
/// «Asumir» lleva rol Coordinador CAE y el Tenant propietario como ámbito, aunque quien la reciba
/// sea Dirección CAE o Administrador; que quien asume abre el Tenant y antes no podía; y que dos
/// «Asumir» a la vez, por dos conexiones, dejan un solo principal.
/// </para>
/// </summary>
public class PrincipalDeCarteraBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private readonly Tenant _operador = new("Operador CAE de prueba");
    private readonly Tenant _otroOperador = new("Otro Operador CAE de prueba");
    private readonly Tenant _beneficiario = new("Beneficiario con principal");
    private readonly Tenant _beneficiarioSinPrincipal = new("Beneficiario sin principal");
    private readonly Tenant _beneficiarioSinNadie = new("Beneficiario sin nadie asignado");
    private readonly Tenant _operadorUnipersonal = new("Operador CAE con un solo Administrador");
    private readonly Tenant _beneficiarioDelUnipersonal = new("Beneficiario del Operador CAE unipersonal");

    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _administradorAjeno = Guid.NewGuid();
    private readonly Guid _administradorDelPropietario = Guid.NewGuid(); // del Tenant propietario: desactiva y reactiva la delegación
    private readonly Guid _coordinador = Guid.NewGuid();
    private readonly Guid _otroCoordinador = Guid.NewGuid();
    private readonly Guid _coordinadorAjeno = Guid.NewGuid();
    private readonly Guid _gestorA = Guid.NewGuid(); // equipo de _coordinador; principal de _beneficiario
    private readonly Guid _gestorB = Guid.NewGuid(); // equipo de _coordinador; apoyo
    private readonly Guid _gestorC = Guid.NewGuid(); // equipo de _otroCoordinador; apoyo
    private readonly Guid _gestorSinCoordinador = Guid.NewGuid();
    private readonly Guid _direccion = Guid.NewGuid();
    private readonly Guid _administradorUnipersonal = Guid.NewGuid();
    private readonly Guid _gestorDelUnipersonal = Guid.NewGuid();
    private readonly Guid _administradorDelBeneficiario = Guid.NewGuid();

    private Guid _operacion;
    private Guid _operacionSinPrincipal;
    private Guid _delegacion;
    private Guid _operacionSinNadie;
    private Guid _operacionDelUnipersonal;

    public async Task InitializeAsync()
    {
        await using var contexto = ContextoPropietario(_operador.Id);
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;
        contexto.Tenants.AddRange(_operador, _otroOperador, _beneficiario, _beneficiarioSinPrincipal,
            _beneficiarioSinNadie, _operadorUnipersonal, _beneficiarioDelUnipersonal);
        contexto.AsignacionesOperacion.Add(AsignacionOperacion.Raiz(_operador.Id, ServicioCae.Outbound, ahora.AddDays(-30), ahora));

        var roles = await contexto.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name!, r => r.Id);

        void Cuenta(Guid id, Guid tenantId, string rol, Guid? coordinadorId = null)
        {
            contexto.Users.Add(new ApplicationUser
            {
                Id = id,
                TenantId = tenantId,
                UserName = $"{id:N}@caemanager.local",
                NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
                Email = $"{id:N}@caemanager.local",
                NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
                NombreCompleto = rol,
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
                CoordinadorUsuarioId = coordinadorId,
            });
            contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = roles[rol] });
        }

        Cuenta(_administrador, _operador.Id, Roles.Administrador);
        Cuenta(_coordinador, _operador.Id, Roles.CoordinadorCae);
        Cuenta(_otroCoordinador, _operador.Id, Roles.CoordinadorCae);
        Cuenta(_gestorA, _operador.Id, Roles.GestorCae, _coordinador);
        Cuenta(_gestorB, _operador.Id, Roles.GestorCae, _coordinador);
        Cuenta(_gestorC, _operador.Id, Roles.GestorCae, _otroCoordinador);
        Cuenta(_gestorSinCoordinador, _operador.Id, Roles.GestorCae);
        Cuenta(_administradorAjeno, _otroOperador.Id, Roles.Administrador);
        Cuenta(_coordinadorAjeno, _otroOperador.Id, Roles.CoordinadorCae);
        Cuenta(_direccion, _operador.Id, Roles.DireccionCae);
        Cuenta(_administradorUnipersonal, _operadorUnipersonal.Id, Roles.Administrador);
        Cuenta(_gestorDelUnipersonal, _operadorUnipersonal.Id, Roles.GestorCae);
        Cuenta(_administradorDelBeneficiario, _beneficiarioDelUnipersonal.Id, Roles.Administrador);
        Cuenta(_administradorDelPropietario, _beneficiario.Id, Roles.Administrador);

        void Cartera(AsignacionOperacion operacion, DelegacionTenant vinculo, Guid gestor, bool principal)
        {
            var cartera = AsignacionCartera.Externa(operacion, gestor, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora);
            if (principal) cartera.DesignarPrincipal();
            contexto.AsignacionesCartera.Add(cartera);
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(vinculo.Id, gestor, Roles.GestorCae));
        }

        (AsignacionOperacion, DelegacionTenant) Operacion(Tenant beneficiario, Tenant? operador = null)
        {
            var operacion = AsignacionOperacion.Externa(
                beneficiario.Id, (operador ?? _operador).Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                vigenciaDesde: ahora.AddDays(-30), vigenciaHasta: null, ahora);
            var vinculo = new DelegacionTenant((operador ?? _operador).Id, beneficiario.Id);
            contexto.AsignacionesOperacion.Add(operacion);
            contexto.DelegacionesTenant.Add(vinculo);
            return (operacion, vinculo);
        }

        var (conPrincipal, vinculoConPrincipal) = Operacion(_beneficiario);
        Cartera(conPrincipal, vinculoConPrincipal, _gestorA, principal: true);
        Cartera(conPrincipal, vinculoConPrincipal, _gestorB, principal: false);
        Cartera(conPrincipal, vinculoConPrincipal, _gestorC, principal: false);
        _operacion = conPrincipal.Id;
        _delegacion = vinculoConPrincipal.Id;

        // Estado que deja una retirada sin Coordinador CAE de relevo: carteras vivas y nadie marcado.
        var (sinPrincipal, vinculoSinPrincipal) = Operacion(_beneficiarioSinPrincipal);
        Cartera(sinPrincipal, vinculoSinPrincipal, _gestorB, principal: false);
        Cartera(sinPrincipal, vinculoSinPrincipal, _gestorSinCoordinador, principal: false);
        _operacionSinPrincipal = sinPrincipal.Id;

        // Operación recién abierta: vigente y sin ninguna cartera. En _operador hay dos Coordinadores
        // CAE (nadie la recibe sola); en el unipersonal, un único Administrador y ningún otro elegible.
        _operacionSinNadie = Operacion(_beneficiarioSinNadie).Item1.Id;
        _operacionDelUnipersonal = Operacion(_beneficiarioDelUnipersonal, _operadorUnipersonal).Item1.Id;

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Designar ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Designar_pasa_la_marca_en_dos_guardados_sin_violar_el_indice_unico_y_nadie_pierde_su_cartera()
    {
        (await Designar(_administrador, Roles.Administrador, _operador.Id, _operacion, _gestorB)).EsExitoso.Should().BeTrue();
        (await PrincipalesAsync(_operacion)).Should().Equal(_gestorB);

        // Y de vuelta, por un Coordinador CAE sobre su propio equipo: el cambio es repetible.
        var vuelta = await Designar(_coordinador, Roles.CoordinadorCae, _operador.Id, _operacion, _gestorA);
        vuelta.EsExitoso.Should().BeTrue(vuelta.EsFallido ? vuelta.Error.Codigo : null);
        (await PrincipalesAsync(_operacion)).Should().Equal(_gestorA);

        var vivas = await CarterasAsync(_operacion);
        vivas.Where(c => c.Estado == EstadoAsignacion.Vigente).Select(c => c.UsuarioId)
            .Should().BeEquivalentTo([_gestorA, _gestorB, _gestorC], "la marca no cambia el ámbito efectivo de nadie");
    }

    [Fact]
    public async Task El_rol_se_lee_en_Identity_y_la_operacion_de_otro_Operador_CAE_no_se_toca()
    {
        // El claim de la sesión dice Administrador; en Identity, sobre su Tenant de origen, es Gestor CAE.
        (await Designar(_gestorB, Roles.Administrador, _operador.Id, _operacion, _gestorB))
            .Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.SinAutoridad);

        // Un Coordinador CAE que no lleva la marca no se la quita al equipo de otro.
        (await Designar(_otroCoordinador, Roles.CoordinadorCae, _operador.Id, _operacion, _gestorC))
            .Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.FueraDeTuEquipo);

        // El Administrador de otro Operador CAE: RLS no le deja leer esas carteras.
        (await Designar(_administradorAjeno, Roles.Administrador, _otroOperador.Id, _operacion, _gestorB))
            .Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.DestinoSinCartera);

        (await PrincipalesAsync(_operacion)).Should().Equal(_gestorA);
    }

    [Fact]
    public async Task La_lectura_de_personas_con_cartera_es_solo_del_propio_Operador_CAE()
    {
        var propias = await Personas(_gestorB, Roles.GestorCae, _operador.Id, _beneficiario.Id);
        var operacion = propias.Should().ContainSingle().Subject;
        operacion.AsignacionOperacionId.Should().Be(_operacion);
        operacion.Principal!.UsuarioId.Should().Be(_gestorA);
        operacion.Apoyos.Select(a => a.UsuarioId).Should().BeEquivalentTo([_gestorB, _gestorC]);

        (await Personas(_administradorAjeno, Roles.Administrador, _otroOperador.Id, null)).Should().BeEmpty();
    }

    // ── Relevo ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retirar_la_cartera_principal_emite_al_Coordinador_CAE_una_cartera_que_le_abre_el_Tenant_y_no_toca_los_apoyos()
    {
        var apoyosAntes = (await CarterasAsync(_operacion)).Where(c => c.UsuarioId != _gestorA).ToDictionary(c => c.Id, c => c.Version);

        var resultado = await Retirar(_gestorA, _beneficiario);
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);

        var carteras = await CarterasAsync(_operacion);
        carteras.Single(c => c.UsuarioId == _gestorA).Should().Match<AsignacionCartera>(
            c => c.Estado == EstadoAsignacion.Cerrada && !c.EsPrincipal);
        var emitida = carteras.Single(c => c.UsuarioId == _coordinador);
        emitida.EsPrincipal.Should().BeTrue();
        emitida.Rol.Should().Be(Roles.CoordinadorCae, "la Operación nunca concede roles de Propiedad");
        emitida.PropietarioTenantId.Should().Be(_beneficiario.Id, "se escribe con el Tenant propietario como ámbito");
        emitida.OperadorTenantId.Should().Be(_operador.Id);
        emitida.Estado.Should().Be(EstadoAsignacion.Vigente);
        emitida.AmbitoRelacionClienteId.Should().BeNull("cartera del Tenant entero");
        (await PrincipalesAsync(_operacion)).Should().Equal(_coordinador);

        carteras.Where(c => apoyosAntes.ContainsKey(c.Id)).ToDictionary(c => c.Id, c => c.Version)
            .Should().BeEquivalentTo(apoyosAntes, "las carteras de apoyo no se escriben");

        await using (var propietario = ContextoPropietario(_operador.Id))
        {
            (await propietario.AsignacionesOperadorDelegado.Where(a => a.UsuarioId == _coordinador).Select(a => a.Rol).ToListAsync())
                .Should().Equal(Roles.CoordinadorCae);
            (await RolesDeAsync(propietario, _coordinador)).Should().Equal(Roles.CoordinadorCae);
        }

        // El Coordinador CAE relevado abre el Tenant con rol de Coordinador CAE…
        (await Abre(_coordinador, Roles.CoordinadorCae, _operador.Id, _beneficiario.Id)).Should().Be((true, Roles.CoordinadorCae));
        // …y no lo abren ni otro Coordinador CAE del mismo Operador CAE ni el de otro Operador CAE.
        (await Abre(_otroCoordinador, Roles.CoordinadorCae, _operador.Id, _beneficiario.Id)).Should().Be((false, null));
        (await Abre(_coordinadorAjeno, Roles.CoordinadorCae, _otroOperador.Id, _beneficiario.Id)).Should().Be((false, null));
        // Ni él mismo el Tenant en el que no hubo relevo.
        (await Abre(_coordinador, Roles.CoordinadorCae, _operador.Id, _beneficiarioSinPrincipal.Id)).Should().Be((false, null));
    }

    [Fact]
    public async Task Retirar_al_principal_sin_Coordinador_CAE_deja_la_operacion_sin_principal_y_sin_carteras_nuevas()
    {
        (await Designar(_administrador, Roles.Administrador, _operador.Id, _operacionSinPrincipal, _gestorSinCoordinador))
            .EsExitoso.Should().BeTrue("premisa: el principal es alguien sin Coordinador CAE");

        (await Retirar(_gestorSinCoordinador, _beneficiarioSinPrincipal)).EsExitoso.Should().BeTrue();

        (await PrincipalesAsync(_operacionSinPrincipal)).Should().BeEmpty();
        (await CarterasAsync(_operacionSinPrincipal)).Where(c => c.Estado == EstadoAsignacion.Vigente).Select(c => c.UsuarioId)
            .Should().Equal(_gestorB);
    }

    [Fact]
    public async Task Desactivar_al_principal_le_quita_la_marca_pero_no_la_cartera_y_releva_a_su_Coordinador_CAE()
    {
        var resultado = await EnArnes(_administrador, Roles.Administrador, _operador.Id, (usuario, contexto, directorio, sp) =>
        {
            var cuentas = new GestionCuentasUsuarioIdentity(
                sp.GetRequiredService<UserManager<ApplicationUser>>(), sp.GetRequiredService<PuertaAccesoDatos>(), directorio, contexto);
            return new CambiarActivacionUsuarioCommandHandler(
                    cuentas, usuario, new TransaccionDeComando(contexto), new BloqueoCarteraUsuario(contexto),
                    new CatalogoIncorporacionCartera(contexto, usuario), directorio, directorio)
                .Handle(new CambiarActivacionUsuarioCommand(_gestorA, Activar: false), CancellationToken.None);
        });
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);

        var carteras = await CarterasAsync(_operacion);
        carteras.Single(c => c.UsuarioId == _gestorA).Should().Match<AsignacionCartera>(
            c => c.Estado == EstadoAsignacion.Vigente && !c.EsPrincipal, "opción C: pierde la marca, no el acceso");
        carteras.Single(c => c.UsuarioId == _coordinador).Should().Match<AsignacionCartera>(
            c => c.EsPrincipal && c.Rol == Roles.CoordinadorCae && c.PropietarioTenantId == _beneficiario.Id);
        (await PrincipalesAsync(_operacion)).Should().Equal(_coordinador);
        (await PrincipalesAsync(_operacionSinPrincipal)).Should().BeEmpty("donde no era el principal no hay nada que relevar");
    }

    // ── Alerta, escalado y «Asumir» (I5) ────────────────────────────────────

    [Fact]
    public async Task La_alerta_sin_principal_solo_ensena_operaciones_del_propio_Operador_CAE()
    {
        var alerta = await Alerta(_direccion, Roles.DireccionCae, _operador.Id);

        alerta.LaVe.Should().BeTrue();
        alerta.Operaciones.Select(o => (o.AsignacionOperacionId, o.Situacion, o.PersonasAsignadas)).Should().BeEquivalentTo(new[]
        {
            (_operacionSinNadie, SituacionDePrincipal.SinNadieAsignado, 0),
            (_operacionSinPrincipal, SituacionDePrincipal.ConPersonasSinPrincipal, 2),
        }, "la operación con un Gestor CAE principal no está en la alerta, ni la del otro Operador CAE");

        // El Administrador de otro Operador CAE la ve, vacía: RLS no le enseña estas operaciones.
        var ajena = await Alerta(_administradorAjeno, Roles.Administrador, _otroOperador.Id);
        ajena.LaVe.Should().BeTrue();
        ajena.Operaciones.Should().BeEmpty();

        // El claim dice Administrador; en Identity es Gestor CAE: no la ve.
        (await Alerta(_gestorB, Roles.Administrador, _operador.Id)).LaVe.Should().BeFalse();
    }

    [Fact]
    public async Task Quien_asume_recibe_una_cartera_de_Coordinador_CAE_con_el_Tenant_propietario_como_ambito_y_abre_el_Tenant()
    {
        (await Abre(_direccion, Roles.DireccionCae, _operador.Id, _beneficiarioSinNadie.Id)).Should().Be((false, null),
            "premisa: sin cartera vigente, alcance cero, aunque sea Dirección CAE de su Operador CAE");

        var resultado = await Asumir(_direccion, Roles.DireccionCae, _operador.Id, _operacionSinNadie);
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);

        var emitida = (await CarterasAsync(_operacionSinNadie)).Should().ContainSingle().Subject;
        emitida.UsuarioId.Should().Be(_direccion);
        emitida.EsPrincipal.Should().BeTrue();
        emitida.Rol.Should().Be(Roles.CoordinadorCae, "la Operación nunca concede roles de Propiedad, aunque quien asume sea Dirección CAE");
        emitida.PropietarioTenantId.Should().Be(_beneficiarioSinNadie.Id, "se escribe con el Tenant propietario como ámbito");
        emitida.OperadorTenantId.Should().Be(_operador.Id);
        emitida.Estado.Should().Be(EstadoAsignacion.Vigente);
        emitida.AmbitoRelacionClienteId.Should().BeNull("cartera del Tenant entero");

        await using (var propietario = ContextoPropietario(_operador.Id))
        {
            (await propietario.AsignacionesOperadorDelegado.Where(a => a.UsuarioId == _direccion).Select(a => a.Rol).ToListAsync())
                .Should().Equal(Roles.CoordinadorCae);
            (await RolesDeAsync(propietario, _direccion)).Should().Equal([Roles.DireccionCae], "su rol en su organización no cambia");
        }

        (await Abre(_direccion, Roles.DireccionCae, _operador.Id, _beneficiarioSinNadie.Id)).Should().Be((true, Roles.CoordinadorCae));
        // No abre nada más, ni lo abre nadie más.
        (await Abre(_direccion, Roles.DireccionCae, _operador.Id, _beneficiarioSinPrincipal.Id)).Should().Be((false, null));
        (await Abre(_coordinador, Roles.CoordinadorCae, _operador.Id, _beneficiarioSinNadie.Id)).Should().Be((false, null));
        (await Abre(_administradorAjeno, Roles.Administrador, _otroOperador.Id, _beneficiarioSinNadie.Id)).Should().Be((false, null));

        (await Alerta(_direccion, Roles.DireccionCae, _operador.Id)).Operaciones
            .Single(o => o.AsignacionOperacionId == _operacionSinNadie)
            .Should().Match<OperacionEnAlertaDePrincipal>(o => o.Situacion == SituacionDePrincipal.CoordinadorCaePrincipal
                                                               && o.EsDeQuienConsulta && !o.SePuedeAsumir);
    }

    [Fact]
    public async Task Asumir_con_carteras_de_apoyo_vivas_no_las_toca()
    {
        var apoyosAntes = (await CarterasAsync(_operacionSinPrincipal)).ToDictionary(c => c.Id, c => c.Version);

        (await Asumir(_coordinador, Roles.CoordinadorCae, _operador.Id, _operacionSinPrincipal)).EsExitoso.Should().BeTrue();

        (await PrincipalesAsync(_operacionSinPrincipal)).Should().Equal(_coordinador);
        (await CarterasAsync(_operacionSinPrincipal)).Where(c => apoyosAntes.ContainsKey(c.Id)).ToDictionary(c => c.Id, c => c.Version)
            .Should().BeEquivalentTo(apoyosAntes, "las carteras de apoyo no se escriben");
    }

    [Fact]
    public async Task No_asumen_un_Gestor_CAE_ni_otro_Operador_CAE_ni_nadie_sobre_una_operacion_con_principal()
    {
        // El claim dice Administrador; en Identity, sobre su Tenant de origen, es Gestor CAE.
        (await Asumir(_gestorB, Roles.Administrador, _operador.Id, _operacionSinNadie))
            .Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.SinAutoridad);

        // El Administrador de otro Operador CAE: esa operación no está entre las suyas.
        (await Asumir(_administradorAjeno, Roles.Administrador, _otroOperador.Id, _operacionSinNadie))
            .Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.OperacionNoEncontrada);

        (await Asumir(_administrador, Roles.Administrador, _operador.Id, _operacion))
            .Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.YaTienePrincipal);

        (await CarterasAsync(_operacionSinNadie)).Should().BeEmpty();
        (await PrincipalesAsync(_operacion)).Should().Equal(_gestorA);
    }

    [Fact]
    public async Task Dos_Asumir_a_la_vez_por_dos_conexiones_dejan_un_solo_principal()
    {
        // La primera ya guardó su cartera principal y aún no ha confirmado.
        var enPausa = new Pausa("despues:guardar");
        var primera = Asumir(_coordinador, Roles.CoordinadorCae, _operador.Id, _operacionSinNadie, enPausa);
        await enPausa.Alcanzada;

        // La segunda no ve esa cartera sin confirmar: solo el índice único la detiene.
        var segunda = Asumir(_direccion, Roles.DireccionCae, _operador.Id, _operacionSinNadie);
        (await Task.WhenAny(segunda, Task.Delay(TimeSpan.FromSeconds(3)))).Should().NotBeSameAs(segunda,
            "la segunda escribe el mismo hueco del índice único y tiene que esperar a la primera");

        enPausa.Soltar();
        (await primera).EsExitoso.Should().BeTrue();
        (await segunda).Error.Should().Be(AsumirPrincipalDeOperacionCommandHandler.CambioMientrasDecidias);
        (await PrincipalesAsync(_operacionSinNadie)).Should().Equal(_coordinador);
        (await CarterasAsync(_operacionSinNadie)).Select(c => c.UsuarioId).Should().Equal([_coordinador], "quien pierde no deja cartera");
    }

    [Fact]
    public async Task Asumir_contra_el_escalado_por_dos_conexiones_deja_un_solo_principal()
    {
        // «Asumir» ya guardó la cartera principal y aún no ha confirmado.
        var enPausa = new Pausa("despues:guardar");
        var asumir = Asumir(_administradorUnipersonal, Roles.Administrador, _operadorUnipersonal.Id, _operacionDelUnipersonal, enPausa);
        await enPausa.Alcanzada;

        // El escalado, por otra conexión, no ve esa cartera sin confirmar y emite la suya.
        var escalado = EnArnes(_administradorUnipersonal, Roles.Administrador, _operadorUnipersonal.Id,
            (usuario, contexto, directorio, _) => new TransaccionDeComando(contexto).EjecutarAsync(async ct =>
            {
                var operacion = await contexto.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionDelUnipersonal, ct);
                var automatica = new AsignacionAutomaticaDePrincipal(
                    new CatalogoIncorporacionCartera(contexto, usuario), directorio, new BloqueoCarteraUsuario(contexto));
                return await automatica.AlAbrirOperacionAsync(operacion, ct) ? Result.Exito() : Result.Fallo(Error.Crear("Test.Carrera", "carrera"));
            }));
        (await Task.WhenAny(escalado, Task.Delay(TimeSpan.FromSeconds(3)))).Should().NotBeSameAs(escalado,
            "el escalado escribe el mismo hueco del índice único y tiene que esperar a «Asumir»");

        enPausa.Soltar();
        (await asumir).EsExitoso.Should().BeTrue();
        (await escalado).Error.Codigo.Should().Be("Test.Carrera", "el hecho que disparó el escalado falla entero");
        (await CarterasAsync(_operacionDelUnipersonal)).Should().ContainSingle()
            .Which.Should().Match<AsignacionCartera>(c => c.UsuarioId == _administradorUnipersonal && c.EsPrincipal);
    }

    [Fact]
    public async Task Al_abrirse_la_operacion_desde_la_sesion_del_Operador_CAE_la_recibe_su_unico_elegible()
    {
        // Como CrearTenantPropietarioDeOperadorCaeExternoCommand: quien ejecuta es el Administrador
        // del Operador CAE, con su Tenant como activo y como origen.
        var asignada = await EnArnes(_administradorUnipersonal, Roles.Administrador, _operadorUnipersonal.Id,
            (usuario, contexto, directorio, _) => new TransaccionDeComando(contexto).EjecutarAsync(async ct =>
            {
                var operacion = await contexto.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionDelUnipersonal, ct);
                var automatica = new AsignacionAutomaticaDePrincipal(
                    new CatalogoIncorporacionCartera(contexto, usuario), directorio, new BloqueoCarteraUsuario(contexto));
                return await automatica.AlAbrirOperacionAsync(operacion, ct) ? Result.Exito() : Result.Fallo(Error.Crear("Test.Carrera", "carrera"));
            }));
        asignada.EsExitoso.Should().BeTrue(asignada.EsFallido ? asignada.Error.Codigo : null);

        var emitida = (await CarterasAsync(_operacionDelUnipersonal)).Should().ContainSingle().Subject;
        emitida.Should().Match<AsignacionCartera>(c => c.UsuarioId == _administradorUnipersonal && c.EsPrincipal
            && c.Rol == Roles.CoordinadorCae && c.PropietarioTenantId == _beneficiarioDelUnipersonal.Id
            && c.OperadorTenantId == _operadorUnipersonal.Id && c.Estado == EstadoAsignacion.Vigente);
        (await Abre(_administradorUnipersonal, Roles.Administrador, _operadorUnipersonal.Id, _beneficiarioDelUnipersonal.Id))
            .Should().Be((true, Roles.CoordinadorCae));
    }

    [Fact]
    public async Task Desde_la_sesion_del_Tenant_propietario_las_cuentas_del_Operador_CAE_no_se_leen_y_la_operacion_abierta_queda_sin_asignar()
    {
        // Como CrearDelegacionTenantCommand: quien ejecuta es el Administrador del Tenant propietario,
        // con su Tenant como activo y como origen; el Operador CAE es otro Tenant. La RLS de cuentas
        // no le deja ver las del Operador CAE, así que el escalado no encuentra a nadie y no asigna:
        // la operación queda en la alerta del Operador CAE, cuyo único elegible la toma con «Asumir».
        IReadOnlyList<Guid> cuentasVistas = [];
        var asignada = await EnArnes(_administradorDelBeneficiario, Roles.Administrador, _beneficiarioDelUnipersonal.Id,
            (usuario, contexto, directorio, _) => new TransaccionDeComando(contexto).EjecutarAsync(async ct =>
            {
                var operacion = await contexto.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionDelUnipersonal, ct);
                using (AmbitoTenantExplicito.Establecer(_operadorUnipersonal.Id))
                    cuentasVistas = await directorio.ObtenerCuentasActivasConRolAsync(_operadorUnipersonal.Id, Roles.Administrador, ct);
                var automatica = new AsignacionAutomaticaDePrincipal(
                    new CatalogoIncorporacionCartera(contexto, usuario), directorio, new BloqueoCarteraUsuario(contexto));
                return await automatica.AlAbrirOperacionAsync(operacion, ct) ? Result.Exito() : Result.Fallo(Error.Crear("Test.Carrera", "carrera"));
            }));

        asignada.EsExitoso.Should().BeTrue(asignada.EsFallido ? asignada.Error.Codigo : null);
        cuentasVistas.Should().BeEmpty("el Tenant propietario no lee las cuentas de su Operador CAE");
        (await CarterasAsync(_operacionDelUnipersonal)).Should().BeEmpty();

        // El único elegible del Operador CAE la ve en su alerta y la asume.
        (await Alerta(_administradorUnipersonal, Roles.Administrador, _operadorUnipersonal.Id)).SinPrincipal
            .Should().Contain(o => o.AsignacionOperacionId == _operacionDelUnipersonal);
        (await Asumir(_administradorUnipersonal, Roles.Administrador, _operadorUnipersonal.Id, _operacionDelUnipersonal))
            .EsExitoso.Should().BeTrue();
        (await PrincipalesAsync(_operacionDelUnipersonal)).Should().Equal(_administradorUnipersonal);
    }

    [Fact]
    public async Task Con_varios_Coordinadores_CAE_la_operacion_abierta_no_se_asigna_a_nadie()
    {
        var asignada = await EnArnes(_administrador, Roles.Administrador, _operador.Id,
            (usuario, contexto, directorio, _) => new TransaccionDeComando(contexto).EjecutarAsync(async ct =>
            {
                var operacion = await contexto.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionSinNadie, ct);
                var automatica = new AsignacionAutomaticaDePrincipal(
                    new CatalogoIncorporacionCartera(contexto, usuario), directorio, new BloqueoCarteraUsuario(contexto));
                return await automatica.AlAbrirOperacionAsync(operacion, ct) ? Result.Exito() : Result.Fallo(Error.Crear("Test.Carrera", "carrera"));
            }));

        asignada.EsExitoso.Should().BeTrue();
        (await CarterasAsync(_operacionSinNadie)).Should().BeEmpty("con varias personas en el primer perfil decide una con «Asumir»");
    }

    [Fact]
    public async Task El_primer_usuario_elegible_recibe_las_operaciones_sin_principal_de_su_Operador_CAE_y_un_Gestor_CAE_no()
    {
        Task<Result> AlPrimerElegible(Guid cuenta) =>
            EnArnes(_administradorUnipersonal, Roles.Administrador, _operadorUnipersonal.Id,
                (usuario, contexto, directorio, _) => new TransaccionDeComando(contexto).EjecutarAsync(async ct =>
                {
                    var automatica = new AsignacionAutomaticaDePrincipal(
                        new CatalogoIncorporacionCartera(contexto, usuario), directorio, new BloqueoCarteraUsuario(contexto));
                    return await automatica.AlPrimerElegibleAsync(cuenta, _operadorUnipersonal.Id, ct)
                        ? Result.Exito()
                        : Result.Fallo(Error.Crear("Test.Carrera", "carrera"));
                }));

        (await AlPrimerElegible(_gestorDelUnipersonal)).EsExitoso.Should().BeTrue();
        (await CarterasAsync(_operacionDelUnipersonal)).Should().BeEmpty("un Gestor CAE no es elegible");

        (await AlPrimerElegible(_administradorUnipersonal)).EsExitoso.Should().BeTrue();
        (await CarterasAsync(_operacionDelUnipersonal)).Should().ContainSingle()
            .Which.Should().Match<AsignacionCartera>(c => c.UsuarioId == _administradorUnipersonal && c.EsPrincipal
                && c.Rol == Roles.CoordinadorCae && c.PropietarioTenantId == _beneficiarioDelUnipersonal.Id);
    }

    // ── Dos conexiones ────────────────────────────────────────────────────

    [Fact]
    public async Task El_relevo_espera_a_la_desactivacion_en_curso_del_Coordinador_CAE_y_no_le_pasa_la_marca()
    {
        // Revisión Codex de I2. Otro circuito está desactivando al Coordinador CAE: tiene el candado
        // exclusivo de cartera de esa cuenta y aún no ha confirmado.
        await using var otro = ContextoPropietario(_operador.Id);
        await using var desactivacion = await otro.Database.BeginTransactionAsync();
        await new BloqueoCarteraUsuario(otro).BloquearExclusivoAsync(_coordinador);

        var retirar = Retirar(_gestorA, _beneficiario);
        (await Task.WhenAny(retirar, Task.Delay(TimeSpan.FromSeconds(3)))).Should().NotBeSameAs(retirar,
            "el relevo no decide sobre una cuenta que se está desactivando: espera a que termine");

        await otro.Users.Where(u => u.Id == _coordinador)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddYears(100)));
        await desactivacion.CommitAsync();

        (await retirar).EsExitoso.Should().BeTrue();
        // La marca no va a una cuenta desactivada. Sin relevo se escala (D-7): queda un solo Coordinador CAE activo.
        (await PrincipalesAsync(_operacion)).Should().Equal(_otroCoordinador);
        (await CarterasAsync(_operacion)).Select(c => c.UsuarioId).Should().NotContain(_coordinador);
    }

    [Fact]
    public async Task Una_designacion_que_decidio_antes_de_un_relevo_falla_y_queda_el_principal_del_relevo()
    {
        var enPausa = new Pausa("antes:apagar");
        var designar = Designar(_administrador, Roles.Administrador, _operador.Id, _operacion, _gestorB, enPausa);
        await enPausa.Alcanzada;

        // Por otra conexión: se retira la cartera del principal y el relevo marca a su Coordinador CAE.
        (await Retirar(_gestorA, _beneficiario)).EsExitoso.Should().BeTrue();

        enPausa.Soltar();
        (await designar).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.CambioMientrasDecidias);
        (await PrincipalesAsync(_operacion)).Should().Equal(_coordinador);
    }

    [Fact]
    public async Task Dos_designaciones_a_la_vez_se_serializan_por_la_cartera_del_principal_actual_y_gana_una_sola()
    {
        // La primera ya apagó y guardó la marca del principal actual; aún no ha encendido la nueva.
        var enPausa = new Pausa("antes:encender");
        var primera = Designar(_administrador, Roles.Administrador, _operador.Id, _operacion, _gestorB, enPausa);
        await enPausa.Alcanzada;

        var segunda = Designar(_administrador, Roles.Administrador, _operador.Id, _operacion, _gestorC);
        (await Task.WhenAny(segunda, Task.Delay(TimeSpan.FromSeconds(3)))).Should().NotBeSameAs(segunda,
            "la segunda escribe la misma cartera y tiene que esperar a la primera");

        enPausa.Soltar();
        (await primera).EsExitoso.Should().BeTrue();
        (await segunda).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.CambioMientrasDecidias);
        (await PrincipalesAsync(_operacion)).Should().Equal(_gestorB);
    }

    [Fact]
    public async Task Un_relevo_espera_a_la_designacion_en_curso_y_no_deja_dos_principales_ni_ninguno()
    {
        var enPausa = new Pausa("antes:encender");
        var designar = Designar(_administrador, Roles.Administrador, _operador.Id, _operacion, _gestorB, enPausa);
        await enPausa.Alcanzada;

        // Retirar la cartera de quien era el principal al decidir: escribe la misma fila.
        var retirar = Retirar(_gestorA, _beneficiario);
        (await Task.WhenAny(retirar, Task.Delay(TimeSpan.FromSeconds(3)))).Should().NotBeSameAs(retirar);

        enPausa.Soltar();
        (await designar).EsExitoso.Should().BeTrue();
        await retirar; // gane o pierda la retirada, la marca es una y está viva
        var principal = (await PrincipalesAsync(_operacion)).Should().ContainSingle().Subject;
        principal.Should().Be(_gestorB, "la retirada llegó después: su cartera ya no era la principal y no hay relevo");
    }

    [Fact]
    public async Task Una_designacion_contra_una_emision_sin_confirmar_choca_con_el_indice_unico_y_queda_un_solo_principal()
    {
        // Emisión en curso: la cartera nueva nace principal (no había ninguno) y aún no se ha confirmado.
        var enPausa = new Pausa("despues:guardar");
        var emitir = EnArnes(_administrador, Roles.Administrador, _operador.Id, (usuario, contexto, directorio, _) =>
            new AsignarCarteraGestorCaeCommandHandler(
                    usuario, directorio, directorio, new CatalogoConPausa(new CatalogoIncorporacionCartera(contexto, usuario), enPausa),
                    new TransaccionDeComando(contexto), new BloqueoCarteraUsuario(contexto))
                .Handle(new AsignarCarteraGestorCaeCommand(_gestorA, [_beneficiarioSinPrincipal.Id], null), CancellationToken.None));
        await enPausa.Alcanzada;

        // La designación no ve esa cartera sin confirmar: solo el índice único la detiene.
        var designar = Designar(_administrador, Roles.Administrador, _operador.Id, _operacionSinPrincipal, _gestorB);
        (await Task.WhenAny(designar, Task.Delay(TimeSpan.FromSeconds(3)))).Should().NotBeSameAs(designar);

        enPausa.Soltar();
        (await emitir).EsExitoso.Should().BeTrue();
        (await designar).Error.Should().Be(DesignarGestorCaePrincipalCommandHandler.CambioMientrasDecidias);
        (await PrincipalesAsync(_operacionSinPrincipal)).Should().Equal(_gestorA);
    }

    // ── D-9: reactivar la delegación devuelve la marca a quien la llevaba ──

    [Fact]
    public async Task Desactivar_y_reactivar_la_delegacion_devuelve_la_marca_al_mismo_Gestor_CAE_y_repone_los_apoyos_sin_ella()
    {
        (await DesactivarDelegacion()).EsExitoso.Should().BeTrue();

        var cerradas = await CarterasAsync(_operacion);
        cerradas.Should().OnlyContain(c => c.Estado == EstadoAsignacion.Cerrada && !c.EsPrincipal,
            "control: la cascada cierra las tres carteras y una cartera cerrada no es principal de nada");
        cerradas.Where(c => c.EraPrincipalAlCerrarsePorCascada).Select(c => c.UsuarioId).Should().Equal([_gestorA],
            "control: la cascada deja escrito quién era el principal, y solo él");

        var reactivar = await ReactivarDelegacion();
        reactivar.EsExitoso.Should().BeTrue(reactivar.EsFallido ? reactivar.Error.Codigo : null);

        var nueva = await OperacionVigenteAsync();
        nueva.Should().NotBe(_operacion, "control: reactivar abre una operación nueva, no reabre la cerrada");
        var repuestas = await CarterasAsync(nueva);
        repuestas.Should().OnlyContain(c => c.Estado == EstadoAsignacion.Vigente && c.Rol == Roles.GestorCae && !c.EraPrincipalAlCerrarsePorCascada);
        repuestas.Select(c => c.UsuarioId).Should().BeEquivalentTo([_gestorA, _gestorB, _gestorC]);
        (await PrincipalesAsync(nueva)).Should().Equal([_gestorA], "vuelve a ser principal quien lo era antes; los apoyos vuelven sin marca");

        (await Abre(_gestorA, Roles.GestorCae, _operador.Id, _beneficiario.Id)).Should().Be((true, Roles.GestorCae));
    }

    [Fact]
    public async Task Un_Coordinador_CAE_principal_por_relevo_recupera_su_cartera_y_la_marca_al_reactivar()
    {
        // El relevo de I2: se retira la cartera del principal y su Coordinador CAE recibe una propia, marcada.
        (await Retirar(_gestorA, _beneficiario)).EsExitoso.Should().BeTrue();
        (await PrincipalesAsync(_operacion)).Should().Equal([_coordinador], "control");

        (await DesactivarDelegacion()).EsExitoso.Should().BeTrue();
        (await CarterasAsync(_operacion)).Where(c => c.EraPrincipalAlCerrarsePorCascada).Select(c => c.UsuarioId)
            .Should().Equal([_coordinador], "control: la cascada trata la cartera del Coordinador CAE como a las demás");

        var reactivar = await ReactivarDelegacion();
        reactivar.EsExitoso.Should().BeTrue(reactivar.EsFallido ? reactivar.Error.Codigo : null);

        var nueva = await OperacionVigenteAsync();
        var repuestas = await CarterasAsync(nueva);
        repuestas.Select(c => (c.UsuarioId, c.Rol)).Should().BeEquivalentTo(
            [(_coordinador, (string?)Roles.CoordinadorCae), (_gestorB, Roles.GestorCae), (_gestorC, Roles.GestorCae)],
            "la cartera propia del Coordinador CAE se repone con su rol; la que se retiró a _gestorA antes de desactivar, no");
        (await PrincipalesAsync(nueva)).Should().Equal([_coordinador]);
        (await Abre(_coordinador, Roles.CoordinadorCae, _operador.Id, _beneficiario.Id)).Should().Be((true, Roles.CoordinadorCae));
    }

    [Fact]
    public async Task Si_el_principal_se_desactivo_entre_el_cierre_y_la_reactivacion_la_marca_va_a_su_Coordinador_CAE()
    {
        // El Coordinador CAE ya operaba sobre este Tenant: conserva su fila de operador delegado, que es
        // lo que deja al Administrador del Tenant propietario leer su cuenta (política cuentas_lectura).
        await using (var propietario = ContextoPropietario(_operador.Id))
        {
            propietario.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(_delegacion, _coordinador, Roles.CoordinadorCae));
            await propietario.SaveChangesAsync();
        }

        (await DesactivarDelegacion()).EsExitoso.Should().BeTrue();

        // Con la delegación cerrada no hay marca viva que relevar: desactivar la cuenta no deja rastro en carteras.
        await using (var propietario = ContextoPropietario(_operador.Id))
            await propietario.Users.Where(u => u.Id == _gestorA)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddYears(100)));

        var reactivar = await ReactivarDelegacion();
        reactivar.EsExitoso.Should().BeTrue(reactivar.EsFallido ? reactivar.Error.Codigo : null);

        var nueva = await OperacionVigenteAsync();
        var repuestas = await CarterasAsync(nueva);
        repuestas.Single(c => c.UsuarioId == _gestorA).Should().Match<AsignacionCartera>(
            c => c.Estado == EstadoAsignacion.Vigente && !c.EsPrincipal, "opción C: la cuenta desactivada conserva la cartera, no la marca");
        repuestas.Single(c => c.UsuarioId == _coordinador).Should().Match<AsignacionCartera>(
            c => c.EsPrincipal && c.Rol == Roles.CoordinadorCae && c.PropietarioTenantId == _beneficiario.Id);
        (await PrincipalesAsync(nueva)).Should().Equal([_coordinador], "relevo de D-3: nunca a un Gestor CAE de apoyo");
        repuestas.Where(c => c.UsuarioId == _gestorB || c.UsuarioId == _gestorC).Should().OnlyContain(c => !c.EsPrincipal);
    }

    /// <summary>
    /// Quien reactiva es el Administrador del Tenant propietario, y RLS no le enseña el organigrama del
    /// Operador CAE: de sus cuentas solo ve las que ya tienen un vínculo con su Tenant. Un Coordinador CAE
    /// que nunca operó sobre él no se puede comprobar, así que no recibe la marca: la operación queda sin
    /// principal, que es el desenlace cerrado, y nunca pasa a un Gestor CAE de apoyo.
    /// </summary>
    [Fact]
    public async Task Si_el_principal_se_desactivo_y_su_Coordinador_CAE_no_es_visible_desde_el_Tenant_propietario_queda_sin_principal()
    {
        (await DesactivarDelegacion()).EsExitoso.Should().BeTrue();

        await using (var propietario = ContextoPropietario(_operador.Id))
            await propietario.Users.Where(u => u.Id == _gestorA)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddYears(100)));

        (await ReactivarDelegacion()).EsExitoso.Should().BeTrue("quedar sin principal no impide reactivar");

        var nueva = await OperacionVigenteAsync();
        (await CarterasAsync(nueva)).Select(c => c.UsuarioId).Should().BeEquivalentTo([_gestorA, _gestorB, _gestorC],
            "control: se reponen las tres carteras y no se emite ninguna para el Coordinador CAE");
        (await PrincipalesAsync(nueva)).Should().BeEmpty();
    }

    [Fact]
    public async Task Una_delegacion_cerrada_antes_de_que_existiera_el_dato_se_reactiva_como_antes_sin_principal()
    {
        (await DesactivarDelegacion()).EsExitoso.Should().BeTrue();

        // Lo que dejó una desactivación anterior a la columna: carteras cerradas que no dicen quién era el principal.
        await using (var propietario = ContextoPropietario(_operador.Id))
            (await propietario.AsignacionesCartera.Where(c => c.AsignacionOperacionId == _operacion && c.EraPrincipalAlCerrarsePorCascada)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.EraPrincipalAlCerrarsePorCascada, false)))
                .Should().Be(1, "control: había un dato que borrar");

        (await ReactivarDelegacion()).EsExitoso.Should().BeTrue();

        var nueva = await OperacionVigenteAsync();
        (await CarterasAsync(nueva)).Select(c => c.UsuarioId).Should().BeEquivalentTo([_gestorA, _gestorB, _gestorC]);
        (await PrincipalesAsync(nueva)).Should().BeEmpty("con varias carteras repuestas y sin el dato no se inventa un responsable");
    }

    [Fact]
    public async Task Dos_reactivaciones_a_la_vez_no_dejan_dos_principales_ni_dos_operaciones()
    {
        (await DesactivarDelegacion()).EsExitoso.Should().BeTrue();

        // La primera ya repuso las carteras y encendió la marca; aún no ha confirmado.
        var enPausa = new Pausa("despues:guardar");
        var primera = ReactivarDelegacion(enPausa);
        await enPausa.Alcanzada;

        // La segunda decide sobre la delegación todavía desactivada y abre otra operación sobre el mismo par.
        var segunda = ReactivarDelegacion();
        (await Task.WhenAny(segunda, Task.Delay(TimeSpan.FromSeconds(3)))).Should().NotBeSameAs(segunda,
            "la segunda choca con lo que la primera aún no ha confirmado y tiene que esperarla");

        enPausa.Soltar();
        (await primera).EsExitoso.Should().BeTrue();
        // Pierde: como fallo del comando o como excepción de la base, pero no escribe nada.
        var segundaGano = false;
        try { segundaGano = (await segunda).EsExitoso; }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException) { }
        segundaGano.Should().BeFalse();

        await using var propietario = ContextoPropietario(_operador.Id);
        var vivas = await propietario.AsignacionesCartera.AsNoTracking()
            .Where(c => c.PropietarioTenantId == _beneficiario.Id && c.Estado != EstadoAsignacion.Cerrada).ToListAsync();
        vivas.Select(c => c.AsignacionOperacionId).Distinct().Should().ContainSingle("una sola operación reabierta");
        vivas.Where(c => c.EsPrincipal).Select(c => c.UsuarioId).Should().Equal([_gestorA]);
        vivas.Should().HaveCount(3);
    }

    // ── Arnés ─────────────────────────────────────────────────────────────

    private Task<Result> DesactivarDelegacion() =>
        EnArnes(_administradorDelPropietario, Roles.Administrador, _beneficiario.Id, (usuario, contexto, _, sp) =>
            new DesactivarDelegacionTenantCommandHandler(
                    new DelegacionTenantRepository(contexto), usuario,
                    new AsignacionesOperativasWriter(contexto, sp.GetRequiredService<ITenantActual>(), usuario), contexto)
                .Handle(new DesactivarDelegacionTenantCommand(_delegacion), CancellationToken.None));

    /// <summary>
    /// Como la ejecuta el Administrador del Tenant propietario, que es quien puede reactivar: su Tenant de
    /// origen es el propietario, no el del Operador CAE cuyas cuentas y carteras toca la restauración.
    /// </summary>
    private Task<Result> ReactivarDelegacion(Pausa? pausa = null) =>
        EnArnes(_administradorDelPropietario, Roles.Administrador, _beneficiario.Id, (usuario, contexto, directorio, sp) =>
        {
            ICatalogoIncorporacionCartera catalogo = new CatalogoIncorporacionCartera(contexto, usuario);
            return new ReactivarDelegacionTenantCommandHandler(
                    new DelegacionTenantRepository(contexto), new AutorizaAlAdministradorDe(_beneficiario.Id), usuario,
                    new AsignacionesOperativasWriter(contexto, sp.GetRequiredService<ITenantActual>(), usuario), contexto, contexto,
                    new TransaccionDeComando(contexto), pausa is null ? catalogo : new CatalogoConPausa(catalogo, pausa),
                    directorio, directorio, new BloqueoCarteraUsuario(contexto))
                .Handle(new ReactivarDelegacionTenantCommand(_delegacion), CancellationToken.None);
        });

    /// <summary>La operación externa vigente del Operador CAE sobre el beneficiario con principal.</summary>
    private async Task<Guid> OperacionVigenteAsync()
    {
        await using var propietario = ContextoPropietario(_operador.Id);
        return await propietario.AsignacionesOperacion.AsNoTracking()
            .Where(o => !o.EsRaiz && o.PropietarioTenantId == _beneficiario.Id && o.OperadorTenantId == _operador.Id
                        && o.Estado == EstadoAsignacion.Vigente)
            .Select(o => o.Id).SingleAsync();
    }

    /// <summary>La autoridad para reactivar no es lo que se mide aquí (lo hace <c>AutorizacionDeDelegacionTests</c>).</summary>
    private sealed class AutorizaAlAdministradorDe(Guid tenant) : IAutorizacionDelegacionTenant
    {
        public Task<bool> PuedeGestionarDelegacionesAsync(
            Guid usuarioId, Guid tenantClienteDeleganteId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantClienteDeleganteId == tenant);
    }

    private async Task<List<Guid>> PrincipalesAsync(Guid operacionId)
    {
        await using var propietario = ContextoPropietario(_operador.Id);
        return await propietario.AsignacionesCartera.AsNoTracking()
            .Where(c => c.AsignacionOperacionId == operacionId && c.EsPrincipal)
            .Select(c => c.UsuarioId).ToListAsync();
    }

    private async Task<List<AsignacionCartera>> CarterasAsync(Guid operacionId)
    {
        await using var propietario = ContextoPropietario(_operador.Id);
        return await propietario.AsignacionesCartera.AsNoTracking()
            .Where(c => c.AsignacionOperacionId == operacionId).ToListAsync();
    }

    private static async Task<List<string>> RolesDeAsync(CaeManagerDbContext contexto, Guid usuarioId) =>
        await (from ur in contexto.UserRoles
               join r in contexto.Roles on ur.RoleId equals r.Id
               where ur.UserId == usuarioId
               select r.Name!).ToListAsync();

    private Task<Result> Designar(
        Guid usuarioId, string rolDeSesion, Guid origen, Guid operacionId, Guid destino, Pausa? pausa = null) =>
        EnArnes(usuarioId, rolDeSesion, origen, (usuario, contexto, directorio, _) =>
        {
            ICatalogoIncorporacionCartera catalogo = new CatalogoIncorporacionCartera(contexto, usuario);
            return new DesignarGestorCaePrincipalCommandHandler(
                    usuario, directorio, directorio, pausa is null ? catalogo : new CatalogoConPausa(catalogo, pausa),
                    new TransaccionDeComando(contexto), new BloqueoCarteraUsuario(contexto))
                .Handle(new DesignarGestorCaePrincipalCommand(operacionId, destino), CancellationToken.None);
        });

    private Task<Result> Retirar(Guid gestor, Tenant beneficiario) =>
        EnArnes(_administrador, Roles.Administrador, _operador.Id, (usuario, contexto, directorio, _) =>
            new AsignarCarteraGestorCaeCommandHandler(
                    usuario, directorio, directorio, new CatalogoIncorporacionCartera(contexto, usuario),
                    new TransaccionDeComando(contexto), new BloqueoCarteraUsuario(contexto))
                .Handle(new AsignarCarteraGestorCaeCommand(gestor, null, [beneficiario.Id]), CancellationToken.None));

    private Task<Result> Asumir(Guid usuarioId, string rolDeSesion, Guid origen, Guid operacionId, Pausa? pausa = null) =>
        EnArnes(usuarioId, rolDeSesion, origen, (usuario, contexto, directorio, _) =>
        {
            ICatalogoIncorporacionCartera catalogo = new CatalogoIncorporacionCartera(contexto, usuario);
            return new AsumirPrincipalDeOperacionCommandHandler(
                    usuario, directorio, pausa is null ? catalogo : new CatalogoConPausa(catalogo, pausa),
                    new TransaccionDeComando(contexto), new BloqueoCarteraUsuario(contexto))
                .Handle(new AsumirPrincipalDeOperacionCommand(operacionId), CancellationToken.None);
        });

    private Task<AlertaDePrincipal> Alerta(Guid usuarioId, string rolDeSesion, Guid origen) =>
        EnArnes(usuarioId, rolDeSesion, origen, (usuario, contexto, directorio, _) =>
            new ObtenerOperacionesSinPrincipalQueryHandler(usuario, directorio, new CatalogoIncorporacionCartera(contexto, usuario))
                .Handle(new ObtenerOperacionesSinPrincipalQuery(), CancellationToken.None));

    private Task<IReadOnlyList<CarterasDeOperacion>> Personas(Guid usuarioId, string rolDeSesion, Guid origen, Guid? tenantId) =>
        EnArnes(usuarioId, rolDeSesion, origen, (usuario, contexto, directorio, _) =>
            new ObtenerPersonasConCarteraQueryHandler(usuario, directorio, new CatalogoIncorporacionCartera(contexto, usuario))
                .Handle(new ObtenerPersonasConCarteraQuery(tenantId), CancellationToken.None));

    /// <summary>Si la sesión de ese usuario puede abrir el Tenant, y con qué rol efectivo por la vía de Operación.</summary>
    private Task<(bool Abre, string? Rol)> Abre(Guid usuarioId, string rolDeSesion, Guid origen, Guid tenantId) =>
        EnArnes(usuarioId, rolDeSesion, origen, async (_, contexto, _, _) =>
        {
            var ahora = DateTime.UtcNow;
            var operacion = await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                contexto, usuarioId, origen, tenantId, ahora, CancellationToken.None);
            if (operacion is null)
                return (await TenantsBeneficiariosAutorizados.EstaAutorizadoAsync(
                    contexto, contexto, usuarioId, origen, tenantId, ahora, CancellationToken.None), (string?)null);

            return (true, await TenantsBeneficiariosAutorizados.RolPorOperacionAsync(
                contexto, usuarioId, origen, tenantId, operacion.Value, ahora, CancellationToken.None));
        });

    private async Task<T> EnArnes<T>(
        Guid usuarioId, string rolDeSesion, Guid origen,
        Func<AsignarCarteraGestorCaeBajoRuntimeTests.UsuarioDeSesion, CaeManagerDbContext, DirectorioUsuariosTenant, IServiceProvider, Task<T>> ejecutar)
    {
        var usuario = new AsignarCarteraGestorCaeBajoRuntimeTests.UsuarioDeSesion(usuarioId, rolDeSesion, origen);
        var tenantActual = new TenantSegunAmbito(origen);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion), npgsql =>
            {
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");
                // Como producción: la transacción explícita del Command convive con la estrategia de reintento.
                npgsql.EnableRetryOnFailure(maxRetryCount: 2, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null);
            })
            .AddInterceptors(
                new AuditoriaInterceptor(new ActorFijo(ActorAuditoria.Normal(usuarioId))),
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(tenantActual, new SinTenantSeleccionado(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor())
            .Options;

        await using var contexto = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddScoped<PuertaAccesoDatos>();
        servicios.AddSingleton(contexto);
        servicios.AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddScoped<DirectorioUsuariosTenant>();
        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>()
            .AddDefaultTokenProviders();
        await using var proveedor = servicios.BuildServiceProvider();
        using var ambito = proveedor.CreateScope();
        var directorio = ambito.ServiceProvider.GetRequiredService<DirectorioUsuariosTenant>();

        // Cada llamada en su propio flujo asíncrono: el ámbito de Tenant explícito de un comando
        // en pausa no se filtra al que corre a la vez por la otra conexión.
        return await Task.Run(() => ejecutar(usuario, contexto, directorio, ambito.ServiceProvider));
    }

    /// <summary>Punto en el que un comando se detiene hasta que el test lo suelta.</summary>
    private sealed class Pausa(string paso)
    {
        private readonly TaskCompletionSource _alcanzada = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _soltada = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Alcanzada => _alcanzada.Task.WaitAsync(TimeSpan.FromSeconds(60));
        public void Soltar() => _soltada.TrySetResult();

        public Task EnAsync(string pasoActual)
        {
            if (pasoActual != paso || _alcanzada.Task.IsCompleted)
                return Task.CompletedTask;
            _alcanzada.TrySetResult();
            return _soltada.Task.WaitAsync(TimeSpan.FromSeconds(120));
        }
    }

    private sealed class CatalogoConPausa(ICatalogoIncorporacionCartera real, Pausa pausa) : ICatalogoIncorporacionCartera
    {
        public Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerCandidatosAsync(Guid o, Guid u, CancellationToken c = default) => real.ObtenerCandidatosAsync(o, u, c);
        public Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerAsignablesAsync(Guid o, CancellationToken c = default) => real.ObtenerAsignablesAsync(o, c);
        public Task<AsignacionOperacion?> ObtenerOperacionVigenteAsync(Guid id, CancellationToken c = default) => real.ObtenerOperacionVigenteAsync(id, c);
        public Task<ResultadoIncorporacionCartera> IncorporarAsync(SolicitudIncorporacionCartera s, CancellationToken c = default) => real.IncorporarAsync(s, c);
        public Task<ResultadoApoyoCartera> IncorporarApoyoAsync(PropuestaApoyoCartera p, CancellationToken c = default) => real.IncorporarApoyoAsync(p, c);
        public Task<ResultadoIncorporacionCartera> IncorporarAsync(Guid p, Guid o, Guid op, Guid u, CancellationToken c = default) => real.IncorporarAsync(p, o, op, u, c);
        public Task<IReadOnlyList<TenantEnCarteraDeGestor>> ObtenerCarteraUniversalAsync(Guid o, Guid u, CancellationToken c = default) => real.ObtenerCarteraUniversalAsync(o, u, c);
        public Task<bool> RetirarCarteraUniversalAsync(Guid p, Guid o, Guid u, Guid a, CancellationToken c = default) => real.RetirarCarteraUniversalAsync(p, o, u, a, c);
        public Task<IReadOnlyList<CarteraVivaDeOperacion>> ObtenerCarterasVivasAsync(Guid o, Guid? p, CancellationToken c = default) => real.ObtenerCarterasVivasAsync(o, p, c);
        public Task<IReadOnlyList<OperacionConPrincipal>> ObtenerOperacionesDondeEsPrincipalAsync(Guid o, Guid u, CancellationToken c = default) => real.ObtenerOperacionesDondeEsPrincipalAsync(o, u, c);

        public async Task<bool> ApagarPrincipalAsync(Guid o, Guid op, Guid u, CancellationToken c = default)
        {
            await pausa.EnAsync("antes:apagar");
            return await real.ApagarPrincipalAsync(o, op, u, c);
        }

        public async Task<bool> EncenderPrincipalAsync(Guid o, Guid op, Guid u, CancellationToken c = default)
        {
            await pausa.EnAsync("antes:encender");
            return await real.EncenderPrincipalAsync(o, op, u, c);
        }

        public Task<ResultadoRelevoPrincipal> RelevarPrincipalAsync(Guid p, Guid o, Guid op, Guid u, CancellationToken c = default) => real.RelevarPrincipalAsync(p, o, op, u, c);
        public Task RetirarAsync(SolicitudIncorporacionCartera s, CancellationToken c = default) => real.RetirarAsync(s, c);

        public async Task<bool> GuardarDetectandoCarreraAsync(CancellationToken c = default)
        {
            var guardado = await real.GuardarDetectandoCarreraAsync(c);
            await pausa.EnAsync("despues:guardar");
            return guardado;
        }

        public void DescartarPendientes() => real.DescartarPendientes();
        public Task<IReadOnlySet<Guid>> FiltrarCarterasVigentesAsync(IReadOnlyCollection<Guid> ids, CancellationToken c = default) => real.FiltrarCarterasVigentesAsync(ids, c);
    }

    private CaeManagerDbContext ContextoPropietario(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>Como el <c>TenantActual</c> de la web: el ámbito explícito manda sobre el de la sesión.</summary>
    private sealed class TenantSegunAmbito(Guid tenantDeSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeSesion;
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
