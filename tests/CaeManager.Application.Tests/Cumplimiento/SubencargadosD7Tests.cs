using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Cumplimiento.Subencargados;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Cumplimiento.Subencargados;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Cumplimiento;

/// <summary>
/// #2010, D-7: aviso de un subencargado nuevo a todos los Tenants propietarios activos, activación solo con aviso
/// Enviado de hace al menos 30 días en cada uno, y objeción solo del Administrador del Tenant propietario, con su
/// efecto en la IA. El aislamiento real por Tenant (RLS, WITH CHECK, sin UPDATE ni DELETE) lo prueba la suite de
/// integración en CI (<c>SubencargadosBajoRlsTests</c>).
/// </summary>
public class SubencargadosD7Tests
{
    private static readonly DateTime Inicio = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ActorPlataforma = Guid.NewGuid();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private readonly SubencargadoRepositoryFalso _repositorio = new();
    private readonly RelojManual _reloj = new(Inicio);

    public SubencargadosD7Tests()
    {
        _repositorio.TenantsActivos.AddRange([TenantA, TenantB]);
        _repositorio.Administradores[TenantA] = [new(Guid.NewGuid(), "admin.a@example.test")];
        _repositorio.Administradores[TenantB] = [new(Guid.NewGuid(), "admin.b1@example.test"), new(Guid.NewGuid(), "admin.b2@example.test")];
    }

    private Subencargado Avisado(bool admiteExclusion = true)
    {
        var subencargado = new Subencargado(
            "Proveedor de IA", "Lectura asistida de documentos", "UE; sin transferencia", admiteExclusion,
            admiteExclusion ? ConmutadorExclusionSubencargado.Ia : null, ActorPlataforma, Inicio);
        subencargado.MarcarAvisado(Inicio);
        _repositorio.Subencargados[subencargado.Id] = subencargado;
        return subencargado;
    }

    private static CurrentUserServiceFalso ActorConDobleFactor() =>
        new(ActorPlataforma, "Administrador", Guid.NewGuid(), tieneDobleFactorActivo: true, superoTotp: true);

    // ---------- Activar ----------

    private ActivarSubencargadoCommandHandler Activador(
        ICurrentUserService? usuario = null, IAutorizacionAdminPlataforma? autorizacion = null, SesionPrivilegiadaActiva? sesion = null) =>
        new(usuario ?? ActorConDobleFactor(), new SesionFalsa(sesion), autorizacion ?? AutorizacionAdminPlataformaFalsa.Global(),
            _repositorio, _repositorio, _reloj);

    [Fact]
    public async Task Con_el_plazo_vencido_y_un_aviso_Enviado_en_cada_Tenant_se_activa()
    {
        var subencargado = Avisado();
        _repositorio.AvisoGuardado(TenantA, subencargado.Id, FaseAvisoCambioSubencargado.Enviado, Inicio);
        _repositorio.AvisoGuardado(TenantB, subencargado.Id, FaseAvisoCambioSubencargado.Enviado, Inicio);
        _reloj.Ahora = Inicio.AddDays(30);

        var resultado = await Activador().Handle(new ActivarSubencargadoCommand(subencargado.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : null);
        subencargado.Estado.Should().Be(EstadoSubencargado.Activo);
        subencargado.ActivadoPorUsuarioId.Should().Be(ActorPlataforma);
        _repositorio.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData(FaseAvisoCambioSubencargado.Preparado)]
    [InlineData(FaseAvisoCambioSubencargado.NoConfirmado)]
    public async Task Un_Tenant_cuyo_aviso_no_consta_como_Enviado_impide_activar(FaseAvisoCambioSubencargado fase)
    {
        var subencargado = Avisado();
        _repositorio.AvisoGuardado(TenantA, subencargado.Id, FaseAvisoCambioSubencargado.Enviado, Inicio);
        _repositorio.AvisoGuardado(TenantB, subencargado.Id, fase, Inicio);
        _reloj.Ahora = Inicio.AddDays(45);

        var resultado = await Activador().Handle(new ActivarSubencargadoCommand(subencargado.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Subencargado.NoActivable");
        resultado.Error.Mensaje.Should().Contain("Un Tenant propietario activo no tiene un aviso enviado");
        subencargado.Estado.Should().Be(EstadoSubencargado.Avisado);
        _repositorio.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Tenant_avisado_hace_menos_de_treinta_dias_impide_activar_aunque_el_resto_haya_vencido()
    {
        var subencargado = Avisado();
        _repositorio.AvisoGuardado(TenantA, subencargado.Id, FaseAvisoCambioSubencargado.Enviado, Inicio);
        _repositorio.AvisoGuardado(TenantB, subencargado.Id, FaseAvisoCambioSubencargado.Enviado, Inicio.AddDays(20));
        _reloj.Ahora = Inicio.AddDays(35);

        var resultado = await Activador().Handle(new ActivarSubencargadoCommand(subencargado.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Subencargado.NoActivable");
        subencargado.Estado.Should().Be(EstadoSubencargado.Avisado);
    }

    [Fact]
    public async Task Antes_de_treinta_dias_desde_el_aviso_no_se_activa_y_el_error_dice_la_fecha()
    {
        var subencargado = Avisado();
        _repositorio.AvisoGuardado(TenantA, subencargado.Id, FaseAvisoCambioSubencargado.Enviado, Inicio);
        _repositorio.AvisoGuardado(TenantB, subencargado.Id, FaseAvisoCambioSubencargado.Enviado, Inicio);
        _reloj.Ahora = Inicio.AddDays(29);

        var resultado = await Activador().Handle(new ActivarSubencargadoCommand(subencargado.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Subencargado.NoActivable");
        resultado.Error.Mensaje.Should().Contain("30 días").And.Contain(Inicio.AddDays(30).ToString("dd/MM/yyyy"));
        subencargado.Estado.Should().Be(EstadoSubencargado.Avisado);
    }

    [Fact]
    public async Task Sin_concesion_global_en_Sesion_Privilegiada_o_sin_segundo_factor_no_se_activa()
    {
        var subencargado = Avisado();
        _repositorio.AvisoGuardado(TenantA, subencargado.Id, FaseAvisoCambioSubencargado.Enviado, Inicio);
        _repositorio.AvisoGuardado(TenantB, subencargado.Id, FaseAvisoCambioSubencargado.Enviado, Inicio);
        _reloj.Ahora = Inicio.AddDays(31);
        var comando = new ActivarSubencargadoCommand(subencargado.Id);

        (await Activador(autorizacion: AutorizacionAdminPlataformaFalsa.AcotadaA(TenantA, TenantB)).Handle(comando, CancellationToken.None))
            .Error.Codigo.Should().Be("Subencargado.SoloActorDePlataforma", "una concesión acotada no es la global");
        (await Activador(sesion: Sesion(CapacidadPrivilegio.AdminPlataforma)).Handle(comando, CancellationToken.None))
            .Error.Codigo.Should().Be("Subencargado.SoloActorDePlataforma");
        (await Activador(usuario: new CurrentUserServiceFalso(ActorPlataforma, "Administrador", tieneDobleFactorActivo: false)).Handle(comando, CancellationToken.None))
            .Error.Codigo.Should().Be("Subencargado.SinDobleFactor");
        (await Activador(usuario: new CurrentUserServiceFalso(ActorPlataforma, "Administrador", superoTotp: false)).Handle(comando, CancellationToken.None))
            .Error.Codigo.Should().Be("Subencargado.TotpNoVerificado");

        subencargado.Estado.Should().Be(EstadoSubencargado.Avisado);
    }

    // ---------- Avisar ----------

    private AvisarCambioSubencargadoCommandHandler Avisador(CorreoFalso correo) =>
        new(ActorConDobleFactor(), new SesionFalsa(null), AutorizacionAdminPlataformaFalsa.Global(), _repositorio, correo, _repositorio, _reloj);

    private Subencargado Propuesto()
    {
        var subencargado = new Subencargado(
            "Proveedor de IA", "Lectura asistida de documentos", "UE; sin transferencia", true,
            ConmutadorExclusionSubencargado.Ia, ActorPlataforma, Inicio);
        _repositorio.Subencargados[subencargado.Id] = subencargado;
        return subencargado;
    }

    [Fact]
    public async Task Avisar_guarda_cada_envio_en_el_Tenant_del_destinatario_antes_del_correo_y_despues_su_resultado()
    {
        var subencargado = Propuesto();
        var correo = new CorreoFalso(_repositorio);

        var resultado = await Avisador(correo).Handle(new AvisarCambioSubencargadoCommand(subencargado.Id), CancellationToken.None);

        resultado.Valor.Should().Be(new ResultadoAvisoSubencargado(2, 0, 0, 0));
        subencargado.Estado.Should().Be(EstadoSubencargado.Avisado);
        subencargado.FechaMinimaActivacionUtc.Should().Be(Inicio.AddDays(30));

        correo.Enviados.Select(e => e.Destinatario).Should().BeEquivalentTo(
            ["admin.a@example.test", "admin.b1@example.test", "admin.b2@example.test"]);
        correo.Enviados.Should().OnlyContain(e => e.PreparadoGuardadoAlEnviar,
            "ningún correo sale sin su fila Preparado ya guardada");

        _repositorio.Avisos.Where(a => a.TenantId == TenantA).Select(a => a.Aviso.Fase).Should().Equal(
            FaseAvisoCambioSubencargado.Preparado, FaseAvisoCambioSubencargado.Enviado);
        _repositorio.Avisos.Count(a => a.TenantId == TenantB).Should().Be(4, "dos Administradores, dos fases cada uno");
        _repositorio.Avisos.Should().OnlyContain(a => a.Aviso.AvisadoPorUsuarioId == ActorPlataforma);
        correo.Enviados.Should().OnlyContain(e => e.Cuerpo.Contains("Proveedor de IA") && e.Cuerpo.Contains("16/12/2026"));
    }

    [Fact]
    public async Task Un_correo_no_confirmado_queda_NoConfirmado_y_no_cuenta_como_aviso_para_activar()
    {
        var subencargado = Propuesto();
        var correo = new CorreoFalso(_repositorio, fallan: ["admin.a@example.test"]);

        var resultado = await Avisador(correo).Handle(new AvisarCambioSubencargadoCommand(subencargado.Id), CancellationToken.None);

        resultado.Valor.Should().Be(new ResultadoAvisoSubencargado(1, 0, 1, 0));
        _repositorio.Avisos.Where(a => a.TenantId == TenantA).Select(a => a.Aviso.Fase).Should().Equal(
            FaseAvisoCambioSubencargado.Preparado, FaseAvisoCambioSubencargado.NoConfirmado);

        _reloj.Ahora = Inicio.AddDays(31);
        var activacion = await Activador().Handle(new ActivarSubencargadoCommand(subencargado.Id), CancellationToken.None);
        activacion.Error.Codigo.Should().Be("Subencargado.NoActivable");

        // El reintento solo vuelve a avisar al Tenant sin aviso Enviado, y su plazo cuenta desde este envío.
        var reintento = await Avisador(new CorreoFalso(_repositorio)).Handle(new AvisarCambioSubencargadoCommand(subencargado.Id), CancellationToken.None);
        reintento.Valor.Should().Be(new ResultadoAvisoSubencargado(1, 1, 0, 0));
        subencargado.AvisadoEnUtc.Should().Be(Inicio, "un reenvío no mueve la fecha mínima del subencargado");

        (await Activador().Handle(new ActivarSubencargadoCommand(subencargado.Id), CancellationToken.None))
            .Error.Codigo.Should().Be("Subencargado.NoActivable", "el aviso del reintento tiene menos de 30 días");

        _reloj.Ahora = Inicio.AddDays(61);
        (await Activador().Handle(new ActivarSubencargadoCommand(subencargado.Id), CancellationToken.None)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Un_Tenant_sin_Administrador_con_correo_queda_sin_destinatario_y_sin_aviso()
    {
        var subencargado = Propuesto();
        _repositorio.Administradores[TenantB] = [];

        var resultado = await Avisador(new CorreoFalso(_repositorio)).Handle(new AvisarCambioSubencargadoCommand(subencargado.Id), CancellationToken.None);

        resultado.Valor.Should().Be(new ResultadoAvisoSubencargado(1, 0, 0, 1));
        _repositorio.Avisos.Should().NotContain(a => a.TenantId == TenantB);
    }

    [Fact]
    public async Task Con_una_transaccion_abierta_o_cambios_ajenos_no_sale_ningun_correo()
    {
        var subencargado = Propuesto();
        var correo = new CorreoFalso(_repositorio);

        _repositorio.HayTransaccionAbierta = true;
        (await Avisador(correo).Handle(new AvisarCambioSubencargadoCommand(subencargado.Id), CancellationToken.None))
            .Error.Codigo.Should().Be("Subencargado.EnvioNoAislado");

        _repositorio.HayTransaccionAbierta = false;
        _repositorio.CambioAjenoPendiente = true;
        (await Avisador(correo).Handle(new AvisarCambioSubencargadoCommand(subencargado.Id), CancellationToken.None))
            .Error.Codigo.Should().Be("Subencargado.EnvioNoAislado");

        correo.Enviados.Should().BeEmpty();
        subencargado.Estado.Should().Be(EstadoSubencargado.Propuesto);
    }

    [Fact]
    public async Task Avisar_lo_niega_a_quien_no_es_Actor_de_Plataforma_TALVEG_con_concesion_global()
    {
        var subencargado = Propuesto();
        var correo = new CorreoFalso(_repositorio);
        var handler = new AvisarCambioSubencargadoCommandHandler(
            ActorConDobleFactor(), new SesionFalsa(null), AutorizacionAdminPlataformaFalsa.SinNada(), _repositorio, correo, _repositorio, _reloj);

        (await handler.Handle(new AvisarCambioSubencargadoCommand(subencargado.Id), CancellationToken.None))
            .Error.Codigo.Should().Be("Subencargado.SoloActorDePlataforma");
        correo.Enviados.Should().BeEmpty();
    }

    // ---------- Objetar ----------

    private readonly TenantsQueryContextFalso _tenants = new();

    private ObjetarSubencargadoCommandHandler Objetor(
        ICurrentUserService usuario, Guid tenantActual, SesionPrivilegiadaActiva? sesion = null, IAutorizacionAdminPlataforma? autorizacion = null)
    {
        _repositorio.TenantDeLaSesion = tenantActual;
        return new(usuario, new TenantActualFalso(tenantActual), new SesionFalsa(sesion),
            autorizacion ?? AutorizacionAdminPlataformaFalsa.SinNada(), _tenants, _repositorio, _repositorio, _reloj);
    }

    private Guid TenantPropietario(bool esPlataforma = false)
    {
        var tenant = new Tenant(esPlataforma ? "TALVEG" : "Tenant propietario");
        if (esPlataforma)
            tenant.MarcarComoPlataforma();
        _tenants.ListaTenants.Add(tenant);
        return tenant.Id;
    }

    [Fact]
    public async Task La_objecion_del_Administrador_a_un_subencargado_excluible_apaga_la_IA_solo_para_su_Tenant()
    {
        var subencargado = Avisado(admiteExclusion: true);
        var tenant = TenantPropietario();
        var otroTenant = TenantPropietario();
        var administrador = Guid.NewGuid();

        var resultado = await Objetor(new CurrentUserServiceFalso(administrador, "Administrador", tenant), tenant)
            .Handle(new ObjetarSubencargadoCommand(subencargado.Id, "No queremos tratamiento con IA"), CancellationToken.None);

        resultado.Valor.Should().Be(EfectoObjecionSubencargado.ExcluidoParaElTenant);
        var (tenantDeLaFila, objecion) = _repositorio.Objeciones.Should().ContainSingle().Subject;
        tenantDeLaFila.Should().Be(tenant);
        objecion.UsuarioId.Should().Be(administrador);
        objecion.Conmutador.Should().Be(ConmutadorExclusionSubencargado.Ia);

        var instrucciones = new InstruccionTratamientoIaTenantPropietarioRepositoryFalso();
        instrucciones.Agregar(new InstruccionTratamientoIaTenantPropietario(
            "v1", "v1", Inicio, OrigenInstruccionTratamientoIa.AltaManualPlataforma, ActorPlataforma));
        var gate = new InstruccionTratamientoIaService(instrucciones, _repositorio);

        (await gate.EstaHabilitadaAsync(tenant)).Should().BeFalse("el Tenant que objetó deja de usar la IA");
        (await gate.EstaHabilitadaAsync(otroTenant)).Should().BeTrue("la objeción no frena el subencargado para los demás");
        instrucciones.Filas.Should().OnlyContain(i => i.RevocadaEnUtc == null, "la objeción no revoca la instrucción concedida");

        subencargado.Retirar("Se sustituye el proveedor", Inicio.AddDays(40));
        (await gate.EstaHabilitadaAsync(tenant)).Should().BeTrue("retirado el subencargado, su exclusión ya no aplica");
    }

    [Fact]
    public async Task La_objecion_a_un_subencargado_no_excluible_registra_la_resolucion_sin_penalizacion_y_no_toca_la_IA()
    {
        var subencargado = Avisado(admiteExclusion: false);
        var tenant = TenantPropietario();

        var resultado = await Objetor(new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", tenant), tenant)
            .Handle(new ObjetarSubencargadoCommand(subencargado.Id, "Ubicación no aceptable"), CancellationToken.None);

        resultado.Valor.Should().Be(EfectoObjecionSubencargado.ResolucionSinPenalizacionDisponible);
        _repositorio.Objeciones.Should().ContainSingle().Which.Objecion.Conmutador.Should().BeNull();
        (await _repositorio.ExcluyeConmutadorAsync(tenant, ConmutadorExclusionSubencargado.Ia)).Should().BeFalse();
    }

    public static TheoryData<string, string> RolesQueNoObjetan() => new()
    {
        { "GestorCae", "un Gestor CAE del Tenant propietario" },
        { "DireccionCae", "Dirección CAE" },
        { "CoordinadorCae", "un Coordinador CAE" },
        { "Consulta", "Consulta" },
        { "Cliente", "el rol Cliente" },
    };

    [Theory]
    [MemberData(nameof(RolesQueNoObjetan))]
    public async Task Solo_el_Administrador_objeta(string rol, string quien)
    {
        var subencargado = Avisado();
        var tenant = TenantPropietario();

        var resultado = await Objetor(new CurrentUserServiceFalso(Guid.NewGuid(), rol, tenant), tenant)
            .Handle(new ObjetarSubencargadoCommand(subencargado.Id, "Motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Subencargado.SoloAdministradorDelTenantPropietario", quien + " no objeta");
        _repositorio.Objeciones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Administrador", "el Administrador de un Operador CAE externo que opera el Tenant")]
    [InlineData("GestorCae", "el Gestor CAE de un Operador CAE externo, con Asignación de Cartera en el Tenant")]
    public async Task Quien_opera_el_Tenant_desde_un_Operador_CAE_externo_no_objeta(string rol, string quien)
    {
        var subencargado = Avisado();
        var tenant = TenantPropietario();
        var operadorCaeExterno = TenantPropietario();

        var resultado = await Objetor(new CurrentUserServiceFalso(Guid.NewGuid(), rol, operadorCaeExterno), tenant)
            .Handle(new ObjetarSubencargadoCommand(subencargado.Id, "Motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Subencargado.SoloAdministradorDelTenantPropietario", quien + " no objeta");
        _repositorio.Objeciones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    public async Task En_una_Sesion_Privilegiada_nadie_objeta_aunque_el_Usuario_simulado_sea_el_Administrador(CapacidadPrivilegio capacidad)
    {
        var subencargado = Avisado();
        var tenant = TenantPropietario();
        var sesion = Sesion(capacidad) with { TenantObjetivoId = tenant };

        var resultado = await Objetor(new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", tenant), tenant, sesion)
            .Handle(new ObjetarSubencargadoCommand(subencargado.Id, "Motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Subencargado.SoloAdministradorDelTenantPropietario");
        _repositorio.Objeciones.Should().BeEmpty();
    }

    [Fact]
    public async Task El_Actor_de_Plataforma_TALVEG_no_objeta_ni_desde_su_Tenant_ni_con_rol_de_Administrador()
    {
        var subencargado = Avisado();
        var plataforma = TenantPropietario(esPlataforma: true);
        var tenant = TenantPropietario();

        var desdePlataforma = await Objetor(new CurrentUserServiceFalso(ActorPlataforma, "Administrador", plataforma), plataforma)
            .Handle(new ObjetarSubencargadoCommand(subencargado.Id, "Motivo"), CancellationToken.None);
        desdePlataforma.Error.Codigo.Should().Be("Subencargado.SoloAdministradorDelTenantPropietario", "el Tenant de Plataforma no objeta");

        var conConcesionGlobal = await Objetor(
                new CurrentUserServiceFalso(ActorPlataforma, "Administrador", tenant), tenant,
                autorizacion: AutorizacionAdminPlataformaFalsa.Global())
            .Handle(new ObjetarSubencargadoCommand(subencargado.Id, "Motivo"), CancellationToken.None);
        conConcesionGlobal.Error.Codigo.Should().Be("Subencargado.SoloAdministradorDelTenantPropietario",
            "quien propone el subencargado no objeta en nombre de un Tenant propietario");

        _repositorio.Objeciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_segunda_objecion_del_mismo_Tenant_se_rechaza_y_no_se_objeta_lo_no_avisado()
    {
        var subencargado = Avisado();
        var tenant = TenantPropietario();
        var administrador = new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", tenant);

        (await Objetor(administrador, tenant).Handle(new ObjetarSubencargadoCommand(subencargado.Id, "Motivo"), CancellationToken.None))
            .EsExitoso.Should().BeTrue();
        (await Objetor(administrador, tenant).Handle(new ObjetarSubencargadoCommand(subencargado.Id, "Otra vez"), CancellationToken.None))
            .Error.Codigo.Should().Be("Subencargado.YaObjetado");

        var propuesto = Propuesto();
        (await Objetor(administrador, tenant).Handle(new ObjetarSubencargadoCommand(propuesto.Id, "Motivo"), CancellationToken.None))
            .Error.Codigo.Should().Be("Subencargado.NoAdmiteObjecion");

        _repositorio.Objeciones.Should().ContainSingle();
    }

    // ---------- Proponer y retirar ----------

    [Fact]
    public async Task Proponer_da_de_alta_el_subencargado_propuesto_atribuido_al_Actor_de_Plataforma_TALVEG()
    {
        var handler = new ProponerSubencargadoCommandHandler(
            ActorConDobleFactor(), new SesionFalsa(null), AutorizacionAdminPlataformaFalsa.Global(), _repositorio, _repositorio, _reloj);

        var resultado = await handler.Handle(
            new ProponerSubencargadoCommand("Proveedor", "Finalidad", "UE", true, ConmutadorExclusionSubencargado.Ia), CancellationToken.None);

        var subencargado = _repositorio.Subencargados[resultado.Valor];
        subencargado.Estado.Should().Be(EstadoSubencargado.Propuesto);
        subencargado.PropuestoPorUsuarioId.Should().Be(ActorPlataforma);
    }

    [Fact]
    public async Task Retirar_deja_el_subencargado_retirado_y_una_segunda_vez_falla()
    {
        var subencargado = Avisado();
        var handler = new RetirarSubencargadoCommandHandler(
            ActorConDobleFactor(), new SesionFalsa(null), AutorizacionAdminPlataformaFalsa.Global(), _repositorio, _repositorio, _reloj);

        (await handler.Handle(new RetirarSubencargadoCommand(subencargado.Id, "Sustituido"), CancellationToken.None)).EsExitoso.Should().BeTrue();
        subencargado.Estado.Should().Be(EstadoSubencargado.Retirado);
        (await handler.Handle(new RetirarSubencargadoCommand(subencargado.Id, "Otra vez"), CancellationToken.None))
            .Error.Codigo.Should().Be("Subencargado.YaRetirado");
    }

    // ---------- Dobles ----------

    private static SesionPrivilegiadaActiva Sesion(CapacidadPrivilegio capacidad) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), capacidad,
            capacidad == CapacidadPrivilegio.Impersonacion ? Guid.NewGuid() : null);

    private sealed class SesionFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class RelojManual(DateTime inicio) : TimeProvider
    {
        public DateTime Ahora { get; set; } = inicio;
        public override DateTimeOffset GetUtcNow() => new(Ahora, TimeSpan.Zero);
    }

    private sealed class CorreoFalso(SubencargadoRepositoryFalso repositorio, string[]? fallan = null) : IEmailService
    {
        public List<(string Destinatario, string Cuerpo, bool PreparadoGuardadoAlEnviar)> Enviados { get; } = [];

        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo, string? responderA = null,
            CancellationToken cancellationToken = default)
        {
            var preparadoGuardado = !repositorio.HayCambiosPendientes
                && repositorio.Avisos.Any(a => a.Aviso.Fase == FaseAvisoCambioSubencargado.Preparado
                                               && repositorio.Avisos.Count(b => b.Aviso.EnvioId == a.Aviso.EnvioId) == 1);
            Enviados.Add((destinatarioEmail, cuerpoHtml, preparadoGuardado));
            return Task.FromResult(fallan?.Contains(destinatarioEmail) == true
                ? Result.Fallo(Error.Crear("Correo.Fallo", "No se pudo enviar."))
                : Result.Exito());
        }
    }
}
