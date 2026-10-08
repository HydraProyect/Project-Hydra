using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Operaciones.ApoyoCartera;
using CaeManager.Application.Operaciones.ApoyoCartera.Commands;
using CaeManager.Application.Operaciones.ApoyoCartera.Queries;
using CaeManager.Application.Tenants;
using CaeManager.Application.Usuarios.Commands.DesignarGestorCaePrincipal;
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
using CaeManager.Infrastructure.Persistence.Configurations;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.IntegrationTests.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// La propuesta de apoyo entre Gestores CAE de un mismo Operador CAE contra PostgreSQL real,
/// autenticando como <c>cae_app_runtime</c> (RLS siempre aplica), con la estrategia de reintento,
/// los interceptores, el catálogo, el candado de cartera y las lecturas de Identity de producción.
///
/// <para>
/// Lo que solo esta capa puede probar: que la política <c>operador_de_la_propuesta</c> aísla por
/// el Operador CAE de origen —y no por el Tenant activo, que al aceptar es el propietario—; que
/// la cartera que emite la aceptación se escribe en el Tenant propietario, nunca principal y
/// nunca con un rol de Propiedad, y le abre ese Tenant al destinatario con rol efectivo Gestor
/// CAE; que el índice único parcial deja proponer de nuevo tras un rechazo; y que aceptar a la
/// vez que cambia la marca del proponente, o que se desactiva al destinatario, por dos
/// conexiones, no concede nada sobre una decisión que ya no vale.
/// </para>
///
/// <para>
/// <b>No observa</b> la interfaz (bUnit) ni el POST de <c>/cuenta/cliente-activo</c>: «abre el
/// Tenant» se mide con el mismo predicado que ese endpoint aplica
/// (<see cref="TenantsBeneficiariosAutorizados"/>), no con el endpoint.
/// </para>
/// </summary>
public class PropuestaApoyoCarteraBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private readonly Tenant _operador = new("Operador CAE de prueba");
    private readonly Tenant _otroOperador = new("Otro Operador CAE de prueba");
    private readonly Tenant _beneficiario = new("Beneficiario con principal");
    private readonly Tenant _otroBeneficiario = new("Beneficiario en el que trabaja el destinatario");

    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _coordinador = Guid.NewGuid();
    private readonly Guid _gestorA = Guid.NewGuid(); // principal de _beneficiario: quien propone
    private readonly Guid _gestorB = Guid.NewGuid(); // sin cartera en _beneficiario: el destinatario
    private readonly Guid _gestorC = Guid.NewGuid(); // apoyo en _beneficiario
    private readonly Guid _gestorAjeno = Guid.NewGuid(); // Gestor CAE de _otroOperador

    private Guid _operacion;

    public async Task InitializeAsync()
    {
        await using var contexto = ContextoPropietario(_operador.Id);
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;
        contexto.Tenants.AddRange(_operador, _otroOperador, _beneficiario, _otroBeneficiario);
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
        Cuenta(_gestorA, _operador.Id, Roles.GestorCae, _coordinador);
        Cuenta(_gestorB, _operador.Id, Roles.GestorCae, _coordinador);
        Cuenta(_gestorC, _operador.Id, Roles.GestorCae, _coordinador);
        Cuenta(_gestorAjeno, _otroOperador.Id, Roles.GestorCae);

        void Cartera(AsignacionOperacion operacion, DelegacionTenant vinculo, Guid gestor, bool principal)
        {
            var cartera = AsignacionCartera.Externa(operacion, gestor, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora);
            if (principal) cartera.DesignarPrincipal();
            contexto.AsignacionesCartera.Add(cartera);
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(vinculo.Id, gestor, Roles.GestorCae));
        }

        (AsignacionOperacion, DelegacionTenant) Operacion(Tenant beneficiario)
        {
            var operacion = AsignacionOperacion.Externa(
                beneficiario.Id, _operador.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                vigenciaDesde: ahora.AddDays(-30), vigenciaHasta: null, ahora);
            var vinculo = new DelegacionTenant(_operador.Id, beneficiario.Id);
            contexto.AsignacionesOperacion.Add(operacion);
            contexto.DelegacionesTenant.Add(vinculo);
            return (operacion, vinculo);
        }

        var (conPrincipal, vinculoConPrincipal) = Operacion(_beneficiario);
        Cartera(conPrincipal, vinculoConPrincipal, _gestorA, principal: true);
        Cartera(conPrincipal, vinculoConPrincipal, _gestorC, principal: false);
        _operacion = conPrincipal.Id;

        // El Tenant en el que el destinatario está trabajando cuando le llega la propuesta.
        var (otra, vinculoOtra) = Operacion(_otroBeneficiario);
        Cartera(otra, vinculoOtra, _gestorB, principal: true);

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Aislamiento entre Operadores CAE ──────────────────────────────────

    [Fact]
    public async Task Un_Gestor_CAE_de_otro_Operador_CAE_no_lee_no_crea_ni_acepta_la_propuesta()
    {
        var propuestaId = await ProponerAsync(_gestorB);

        // Control positivo: el propio Operador CAE sí la ve por la misma vía.
        (await Leer(_gestorB, _operador.Id, propuestaId)).Should().NotBeNull();

        // La sesión es del otro Operador CAE aunque el parámetro diga el nuestro: filtra la
        // política operador_de_la_propuesta, no el WHERE del repositorio.
        (await Leer(_gestorAjeno, _otroOperador.Id, propuestaId)).Should().BeNull();
        (await EnArnes(_gestorAjeno, Roles.GestorCae, _otroOperador.Id, (_, contexto, _, _) =>
            contexto.PropuestasApoyoCartera.AsNoTracking().CountAsync())).Should().Be(0);

        // Escribir a nombre del Operador CAE ajeno: lo corta el WITH CHECK.
        await using (var propietario = ContextoPropietario(_operador.Id))
        {
            var operacion = await propietario.AsignacionesOperacion.AsNoTracking().SingleAsync(o => o.Id == _operacion);
            var colarse = () => EnArnes(_gestorAjeno, Roles.GestorCae, _otroOperador.Id, async (_, contexto, _, _) =>
            {
                contexto.PropuestasApoyoCartera.Add(PropuestaApoyoCartera.Crear(operacion, _gestorA, _gestorC, null, DateTime.UtcNow));
                return await contexto.SaveChangesAsync();
            });
            (await colarse.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        // Y aceptarla con su id: para él no existe.
        (await Aceptar(_gestorAjeno, _otroOperador.Id, propuestaId)).Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada);

        (await PropuestaAsync(propuestaId)).Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
        (await CarterasAsync()).Select(c => c.UsuarioId).Should().BeEquivalentTo([_gestorA, _gestorC]);
    }

    [Fact]
    public async Task No_se_puede_proponer_a_un_Gestor_CAE_de_otro_Operador_CAE()
    {
        var resultado = await Proponer(_gestorA, _gestorAjeno);

        resultado.Error.Should().Be(ErroresPropuestaApoyo.DestinatarioNoValido);
        await using var propietario = ContextoPropietario(_operador.Id);
        (await propietario.PropuestasApoyoCartera.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Solo_el_destinatario_acepta_ni_otro_Gestor_CAE_del_mismo_Operador_CAE_ni_su_Coordinador_CAE()
    {
        var propuestaId = await ProponerAsync(_gestorB);

        (await Aceptar(_gestorC, _operador.Id, propuestaId)).Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada);
        (await Aceptar(_gestorA, _operador.Id, propuestaId)).Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada);
        (await Aceptar(_coordinador, _operador.Id, propuestaId, Roles.CoordinadorCae)).Error.Should().Be(ErroresPropuestaApoyo.NoEncontrada);

        (await PropuestaAsync(propuestaId)).Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente);
        (await CarterasAsync()).Select(c => c.UsuarioId).Should().NotContain(_gestorB);
    }

    // ── Aceptar ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Aceptar_con_otro_Tenant_activo_emite_una_cartera_de_apoyo_Gestor_CAE_nunca_principal_que_abre_el_Tenant()
    {
        var propuestaId = await ProponerAsync(_gestorB);

        // Control: antes de aceptar, el destinatario no abre el Tenant.
        (await Abre(_gestorB, _beneficiario.Id)).Abre.Should().BeFalse();

        // El destinatario la ve y la acepta trabajando en otro Tenant: nada depende del activo.
        var recibida = (await Pendientes(_gestorB, tenantActivo: _otroBeneficiario.Id)).Recibidas.Should().ContainSingle().Subject;
        recibida.Id.Should().Be(propuestaId);
        recibida.NombreEmpresa.Should().Be(_beneficiario.Nombre, "el aviso nombra el Tenant aunque el destinatario aún no tenga cartera en él");
        var resultado = await Aceptar(_gestorB, _operador.Id, propuestaId, tenantActivo: _otroBeneficiario.Id);
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);

        var carteras = await CarterasAsync();
        var apoyo = carteras.Single(c => c.UsuarioId == _gestorB);
        apoyo.Should().Match<AsignacionCartera>(c =>
            c.Estado == EstadoAsignacion.Vigente && !c.EsPrincipal && c.Rol == Roles.GestorCae
            && c.PropietarioTenantId == _beneficiario.Id && c.OperadorTenantId == _operador.Id && c.VigenciaHasta == null,
            "una cartera de apoyo nunca nace principal ni con un rol de Propiedad, y se escribe en el Tenant propietario");
        carteras.Where(c => c.EsPrincipal).Select(c => c.UsuarioId).Should().Equal([_gestorA], "aceptar no mueve la marca");
        carteras.Should().HaveCount(3, "no se cierra ni se duplica ninguna cartera");

        await using (var propietario = ContextoPropietario(_operador.Id))
        {
            var propuesta = await propietario.PropuestasApoyoCartera.AsNoTracking().SingleAsync(p => p.Id == propuestaId);
            propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Aceptada);
            propuesta.AsignacionCarteraId.Should().Be(apoyo.Id);

            var heredada = await propietario.AsignacionesOperadorDelegadoConRevocadas.AsNoTracking()
                .SingleAsync(f => f.Id == propuesta.AsignacionOperadorDelegadoId);
            heredada.Should().Match<AsignacionOperadorDelegado>(f => f.UsuarioId == _gestorB && f.Rol == Roles.GestorCae);
        }

        (await Abre(_gestorB, _beneficiario.Id)).Should().Be((true, Roles.GestorCae), "el aceptado abre el Tenant con rol efectivo Gestor CAE");
        (await Pendientes(_gestorB)).Recibidas.Should().BeEmpty();

        // Aceptarla otra vez no emite nada más.
        (await Aceptar(_gestorB, _operador.Id, propuestaId)).Error.Should().Be(ErroresPropuestaApoyo.YaResuelta);
        (await CarterasAsync()).Should().HaveCount(3);
    }

    // ── Índice único parcial ──────────────────────────────────────────────

    [Fact]
    public async Task Una_sola_pendiente_por_operacion_y_destinatario_y_tras_rechazarla_se_puede_proponer_de_nuevo()
    {
        var primera = await ProponerAsync(_gestorB);
        (await Proponer(_gestorA, _gestorB)).Error.Should().Be(ErroresPropuestaApoyo.YaPendiente);

        // El índice, no la comprobación previa del handler: una segunda pendiente escrita a mano.
        await using (var propietario = ContextoPropietario(_operador.Id))
        {
            var operacion = await propietario.AsignacionesOperacion.AsNoTracking().SingleAsync(o => o.Id == _operacion);
            propietario.PropuestasApoyoCartera.Add(PropuestaApoyoCartera.Crear(operacion, _gestorA, _gestorB, null, DateTime.UtcNow));
            var duplicar = async () => await propietario.SaveChangesAsync();
            (await duplicar.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.ConstraintName.Should().Be(PropuestaApoyoCarteraConfiguration.IndicePendienteUnica);
        }

        (await Rechazar(_gestorB, primera)).EsExitoso.Should().BeTrue();

        // Solo las pendientes cuentan: la rechazada no impide volver a proponer.
        var segunda = await Proponer(_gestorA, _gestorB);
        segunda.EsExitoso.Should().BeTrue(segunda.EsFallido ? segunda.Error.Codigo : "el índice es parcial: solo las pendientes");
        segunda.Valor.Should().NotBe(primera);

        (await Retirar(_gestorA, segunda.Valor)).EsExitoso.Should().BeTrue();
        (await Proponer(_gestorA, _gestorB)).EsExitoso.Should().BeTrue("la retirada tampoco cuenta");
    }

    // ── Dos conexiones ────────────────────────────────────────────────────

    [Fact]
    public async Task Si_el_proponente_pierde_la_marca_antes_de_que_el_catalogo_decida_la_propuesta_se_anula_y_no_hay_cartera()
    {
        var propuestaId = await ProponerAsync(_gestorB);

        var enPausa = new Pausa("antes:incorporarApoyo");
        var aceptar = Aceptar(_gestorB, _operador.Id, propuestaId, pausa: enPausa);
        await enPausa.Alcanzada;

        // Por otra conexión: la marca pasa a otro Gestor CAE.
        (await Designar(_gestorC)).EsExitoso.Should().BeTrue();

        enPausa.Soltar();
        (await aceptar).Error.Should().Be(ErroresPropuestaApoyo.AnuladaProponenteYaNoEsPrincipal);

        var propuesta = await PropuestaAsync(propuestaId);
        propuesta.Estado.Should().Be(EstadoPropuestaApoyoCartera.Anulada);
        propuesta.MotivoAnulacion.Should().Be(MotivoAnulacionPropuestaApoyo.ProponenteYaNoEsPrincipal);
        var carteras = await CarterasAsync();
        carteras.Select(c => c.UsuarioId).Should().NotContain(_gestorB);
        carteras.Where(c => c.EsPrincipal).Select(c => c.UsuarioId).Should().Equal([_gestorC]);
        (await Abre(_gestorB, _beneficiario.Id)).Abre.Should().BeFalse();
    }

    [Fact]
    public async Task Si_el_proponente_pierde_la_marca_despues_de_que_el_catalogo_decidiera_el_guardado_pierde_la_carrera_y_no_hay_cartera()
    {
        var propuestaId = await ProponerAsync(_gestorB);

        // El catálogo ya leyó que el proponente era el principal y dejó la cartera sin guardar.
        var enPausa = new Pausa("despues:incorporarApoyo");
        var aceptar = Aceptar(_gestorB, _operador.Id, propuestaId, pausa: enPausa);
        await enPausa.Alcanzada;

        (await Designar(_gestorC)).EsExitoso.Should().BeTrue();

        enPausa.Soltar();
        (await aceptar).Error.Should().Be(ErroresPropuestaApoyo.CambioMientrasDecidias,
            "la aceptación renueva la versión de la cartera del principal: si cambió de manos a la vez, no guarda");

        (await PropuestaAsync(propuestaId)).Estado.Should().Be(EstadoPropuestaApoyoCartera.Pendiente, "la transacción se deshizo entera");
        var carteras = await CarterasAsync();
        carteras.Select(c => c.UsuarioId).Should().NotContain(_gestorB);
        carteras.Where(c => c.EsPrincipal).Select(c => c.UsuarioId).Should().Equal([_gestorC]);

        // Al reintentar, ya decide sobre el estado nuevo.
        (await Aceptar(_gestorB, _operador.Id, propuestaId)).Error.Should().Be(ErroresPropuestaApoyo.AnuladaProponenteYaNoEsPrincipal);
        (await CarterasAsync()).Select(c => c.UsuarioId).Should().NotContain(_gestorB);
    }

    [Fact]
    public async Task Aceptar_espera_a_la_desactivacion_en_curso_del_destinatario_y_no_le_concede_la_cartera()
    {
        var propuestaId = await ProponerAsync(_gestorB);

        // Otro circuito está desactivando al destinatario: tiene el candado exclusivo de cartera
        // de esa cuenta y aún no ha confirmado.
        await using var otro = ContextoPropietario(_operador.Id);
        await using var desactivacion = await otro.Database.BeginTransactionAsync();
        await new BloqueoCarteraUsuario(otro).BloquearExclusivoAsync(_gestorB);

        var aceptar = Aceptar(_gestorB, _operador.Id, propuestaId);
        (await Task.WhenAny(aceptar, Task.Delay(TimeSpan.FromSeconds(3)))).Should().NotBeSameAs(aceptar,
            "aceptar no decide sobre una cuenta que se está desactivando: espera a que termine");

        await otro.Users.Where(u => u.Id == _gestorB)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddYears(100)));
        await desactivacion.CommitAsync();

        (await aceptar).Error.Should().Be(ErroresPropuestaApoyo.AnuladaDestinatarioNoDisponible);
        (await PropuestaAsync(propuestaId)).Estado.Should().Be(EstadoPropuestaApoyoCartera.Anulada);
        (await CarterasAsync()).Select(c => c.UsuarioId).Should().NotContain(_gestorB,
            "una cuenta desactivada no recibe una cartera nueva");
    }

    // ── Arnés ─────────────────────────────────────────────────────────────

    private async Task<Guid> ProponerAsync(Guid destinatario)
    {
        var resultado = await Proponer(_gestorA, destinatario);
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        return resultado.Valor;
    }

    private Task<Result<Guid>> Proponer(Guid proponente, Guid destinatario) =>
        EnArnes(proponente, Roles.GestorCae, _operador.Id, (usuario, contexto, directorio, _) =>
            new ProponerApoyoCarteraCommandHandler(
                    usuario, directorio, new CatalogoIncorporacionCartera(contexto, usuario), new PropuestaApoyoCarteraRepository(contexto))
                .Handle(new ProponerApoyoCarteraCommand(_operacion, destinatario), CancellationToken.None));

    private Task<Result> Aceptar(
        Guid usuarioId, Guid origen, Guid propuestaId, string rolDeSesion = Roles.GestorCae,
        Guid? tenantActivo = null, Pausa? pausa = null) =>
        EnArnes(usuarioId, rolDeSesion, origen, (usuario, contexto, directorio, _) =>
        {
            ICatalogoIncorporacionCartera catalogo = new CatalogoIncorporacionCartera(contexto, usuario);
            return new AceptarPropuestaApoyoCarteraCommandHandler(
                    usuario, directorio, pausa is null ? catalogo : new CatalogoConPausa(catalogo, pausa),
                    new PropuestaApoyoCarteraRepository(contexto), new TransaccionDeComando(contexto), new BloqueoCarteraUsuario(contexto))
                .Handle(new AceptarPropuestaApoyoCarteraCommand(propuestaId), CancellationToken.None);
        }, tenantActivo);

    private Task<Result> Rechazar(Guid usuarioId, Guid propuestaId) =>
        EnArnes(usuarioId, Roles.GestorCae, _operador.Id, (usuario, contexto, directorio, _) =>
            new RechazarPropuestaApoyoCarteraCommandHandler(
                    usuario, directorio, new CatalogoIncorporacionCartera(contexto, usuario), new PropuestaApoyoCarteraRepository(contexto))
                .Handle(new RechazarPropuestaApoyoCarteraCommand(propuestaId), CancellationToken.None));

    private Task<Result> Retirar(Guid usuarioId, Guid propuestaId) =>
        EnArnes(usuarioId, Roles.GestorCae, _operador.Id, (usuario, contexto, directorio, _) =>
            new RetirarPropuestaApoyoCarteraCommandHandler(
                    usuario, directorio, new CatalogoIncorporacionCartera(contexto, usuario), new PropuestaApoyoCarteraRepository(contexto))
                .Handle(new RetirarPropuestaApoyoCarteraCommand(propuestaId), CancellationToken.None));

    private Task<PropuestasApoyoPendientesDto> Pendientes(Guid usuarioId, Guid? tenantActivo = null) =>
        EnArnes(usuarioId, Roles.GestorCae, _operador.Id, (usuario, contexto, directorio, _) =>
            new ObtenerPropuestasApoyoPendientesQueryHandler(usuario, directorio, new PropuestaApoyoCarteraRepository(contexto), contexto)
                .Handle(new ObtenerPropuestasApoyoPendientesQuery(), CancellationToken.None), tenantActivo);

    /// <summary>La propuesta leída bajo runtime con la sesión de ese usuario, pidiéndola siempre como del Operador CAE propio.</summary>
    private Task<PropuestaApoyoCartera?> Leer(Guid usuarioId, Guid origen, Guid propuestaId) =>
        EnArnes(usuarioId, Roles.GestorCae, origen, (_, contexto, _, _) =>
            new PropuestaApoyoCarteraRepository(contexto).ObtenerPorIdAsync(propuestaId, _operador.Id));

    private Task<Result> Designar(Guid destino) =>
        EnArnes(_administrador, Roles.Administrador, _operador.Id, (usuario, contexto, directorio, _) =>
            new DesignarGestorCaePrincipalCommandHandler(
                    usuario, directorio, directorio, new CatalogoIncorporacionCartera(contexto, usuario),
                    new TransaccionDeComando(contexto), new BloqueoCarteraUsuario(contexto))
                .Handle(new DesignarGestorCaePrincipalCommand(_operacion, destino), CancellationToken.None));

    /// <summary>Si la sesión de ese usuario puede abrir el Tenant, y con qué rol efectivo por la vía de Operación.</summary>
    private Task<(bool Abre, string? Rol)> Abre(Guid usuarioId, Guid tenantId) =>
        EnArnes(usuarioId, Roles.GestorCae, _operador.Id, async (_, contexto, _, _) =>
        {
            var ahora = DateTime.UtcNow;
            var operacion = await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                contexto, usuarioId, _operador.Id, tenantId, ahora, CancellationToken.None);
            if (operacion is null)
                return (await TenantsBeneficiariosAutorizados.EstaAutorizadoAsync(
                    contexto, contexto, usuarioId, _operador.Id, tenantId, ahora, CancellationToken.None), (string?)null);

            return (true, await TenantsBeneficiariosAutorizados.RolPorOperacionAsync(
                contexto, usuarioId, _operador.Id, tenantId, operacion.Value, ahora, CancellationToken.None));
        });

    private async Task<PropuestaApoyoCartera> PropuestaAsync(Guid propuestaId)
    {
        await using var propietario = ContextoPropietario(_operador.Id);
        return await propietario.PropuestasApoyoCartera.AsNoTracking().SingleAsync(p => p.Id == propuestaId);
    }

    /// <summary>Todas las carteras de la operación del Tenant con principal, vivas o no.</summary>
    private async Task<List<AsignacionCartera>> CarterasAsync()
    {
        await using var propietario = ContextoPropietario(_operador.Id);
        return await propietario.AsignacionesCartera.AsNoTracking()
            .Where(c => c.AsignacionOperacionId == _operacion).ToListAsync();
    }

    /// <param name="tenantActivo">El Tenant en el que la sesión está trabajando; sin valor, el de origen.</param>
    private async Task<T> EnArnes<T>(
        Guid usuarioId, string rolDeSesion, Guid origen,
        Func<AsignarCarteraGestorCaeBajoRuntimeTests.UsuarioDeSesion, CaeManagerDbContext, DirectorioUsuariosTenant, IServiceProvider, Task<T>> ejecutar,
        Guid? tenantActivo = null)
    {
        var usuario = new AsignarCarteraGestorCaeBajoRuntimeTests.UsuarioDeSesion(usuarioId, rolDeSesion, origen);
        var tenantActual = new TenantSegunAmbito(tenantActivo ?? origen);
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

    /// <summary>El catálogo real, que se detiene alrededor de la decisión de la vía de apoyo.</summary>
    private sealed class CatalogoConPausa(ICatalogoIncorporacionCartera real, Pausa pausa) : ICatalogoIncorporacionCartera
    {
        public async Task<ResultadoApoyoCartera> IncorporarApoyoAsync(PropuestaApoyoCartera p, CancellationToken c = default)
        {
            await pausa.EnAsync("antes:incorporarApoyo");
            var resultado = await real.IncorporarApoyoAsync(p, c);
            await pausa.EnAsync("despues:incorporarApoyo");
            return resultado;
        }

        public Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerCandidatosAsync(Guid o, Guid u, CancellationToken c = default) => real.ObtenerCandidatosAsync(o, u, c);
        public Task<IReadOnlyList<TenantCandidatoIncorporacion>> ObtenerAsignablesAsync(Guid o, CancellationToken c = default) => real.ObtenerAsignablesAsync(o, c);
        public Task<AsignacionOperacion?> ObtenerOperacionVigenteAsync(Guid id, CancellationToken c = default) => real.ObtenerOperacionVigenteAsync(id, c);
        public Task<ResultadoIncorporacionCartera> IncorporarAsync(SolicitudIncorporacionCartera s, CancellationToken c = default) => real.IncorporarAsync(s, c);
        public Task<ResultadoIncorporacionCartera> IncorporarAsync(Guid p, Guid o, Guid op, Guid u, CancellationToken c = default) => real.IncorporarAsync(p, o, op, u, c);
        public Task<IReadOnlyList<TenantEnCarteraDeGestor>> ObtenerCarteraUniversalAsync(Guid o, Guid u, CancellationToken c = default) => real.ObtenerCarteraUniversalAsync(o, u, c);
        public Task<bool> RetirarCarteraUniversalAsync(Guid p, Guid o, Guid u, Guid a, CancellationToken c = default) => real.RetirarCarteraUniversalAsync(p, o, u, a, c);
        public Task<IReadOnlyList<CarteraVivaDeOperacion>> ObtenerCarterasVivasAsync(Guid o, Guid? p, CancellationToken c = default) => real.ObtenerCarterasVivasAsync(o, p, c);
        public Task<IReadOnlyList<OperacionConPrincipal>> ObtenerOperacionesDondeEsPrincipalAsync(Guid o, Guid u, CancellationToken c = default) => real.ObtenerOperacionesDondeEsPrincipalAsync(o, u, c);
        public Task<bool> ApagarPrincipalAsync(Guid o, Guid op, Guid u, CancellationToken c = default) => real.ApagarPrincipalAsync(o, op, u, c);
        public Task<bool> EncenderPrincipalAsync(Guid o, Guid op, Guid u, CancellationToken c = default) => real.EncenderPrincipalAsync(o, op, u, c);
        public Task<ResultadoRelevoPrincipal> RelevarPrincipalAsync(Guid p, Guid o, Guid op, Guid u, CancellationToken c = default) => real.RelevarPrincipalAsync(p, o, op, u, c);
        public Task RetirarAsync(SolicitudIncorporacionCartera s, CancellationToken c = default) => real.RetirarAsync(s, c);
        public Task<bool> GuardarDetectandoCarreraAsync(CancellationToken c = default) => real.GuardarDetectandoCarreraAsync(c);
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
