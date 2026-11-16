using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Integraciones.Commands.ReconocerAvisoHueco;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Issue #2124, acción Confirmar de Conexiones: quién puede dar por revisado el aviso «posible correo no recuperado» (los
/// tres perfiles de la decisión de Chris del 2026-11-16), en qué estado, y que la recuperación del hueco que estuviera en
/// curso no reescriba nada después.
/// </summary>
public class ReconocerAvisoHuecoCommandHandlerTests
{
    private sealed class DescarteContado : IDescarteCambiosPendientes
    {
        public int Veces { get; private set; }

        public void DescartarCambiosPendientes() => Veces++;
    }

    /// <summary>Buzón compartido Habilitado y verificado con la marca del hueco puesta hace tres horas.</summary>
    private static (ConexionIntegracion Conexion, ConexionIntegracionRepositorioFalso Conexiones, DateTime Marca) ConMarca()
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido(
            "cae@operador.test", "Buzón CAE", Guid.NewGuid().ToString(), Guid.NewGuid()).Verificada();
        var marca = DateTime.UtcNow.AddHours(-3);
        conexion.RegistrarNotificacionDescartada(marca + ConexionIntegracion.RetrasoMaximoNotificacion).Should().BeTrue();
        conexion.HuecoPendienteDesdeUtc.Should().Be(marca, "control: la marca está puesta");
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        return (conexion, conexiones, marca);
    }

    private static ReconocerAvisoHuecoCommandHandler Handler(
        ConexionIntegracionRepositorioFalso conexiones, IUnitOfWork unitOfWork, string? rol = "Administrador",
        AlcanceDatosServiceFalso? alcance = null, CerrojoVerificacionBuzonFalso? cerrojo = null, DescarteContado? descarte = null) =>
        new(conexiones, alcance ?? new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), rol),
            cerrojo ?? new CerrojoVerificacionBuzonFalso(), unitOfWork, descarte ?? new DescarteContado());

    /// <summary>
    /// (a) Administrador del Tenant propietario y (b) Administrador del Operador CAE externo por Asignación de Operación:
    /// los dos llegan al handler con el rol efectivo Administrador. (c) Gestor CAE con el Tenant en su Asignación de
    /// Cartera: alcanza el Tenant entero sin acceso total.
    /// </summary>
    public static TheoryData<string, string, bool, bool?> PerfilesAutorizados => new()
    {
        { "Administrador del Tenant propietario", "Administrador", true, null },
        { "Administrador del Operador CAE externo", "Administrador", true, null },
        { "Gestor CAE con el Tenant en su cartera", "GestorCae", false, true },
    };

    [Theory]
    [MemberData(nameof(PerfilesAutorizados))]
    public async Task Cada_perfil_autorizado_confirma_y_la_marca_queda_limpia_con_su_accion_de_auditoria(
        string perfil, string rol, bool accesoTotal, bool? alcanzaElTenantEntero)
    {
        var (conexion, conexiones, marca) = ConMarca();
        conexion.IniciarRecuperacionHueco(DateTime.UtcNow);
        conexion.RecuperacionHuecoEnCurso.Should().BeTrue("control: hay una recuperación que anular");
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: accesoTotal, alcanzaElTenantEntero: alcanzaElTenantEntero);

        var resultado = await Handler(conexiones, unitOfWork, rol, alcance)
            .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, marca), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(perfil);
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.RecuperacionHuecoEnCurso.Should().BeFalse("la recuperación en curso queda anulada");
        conexion.RecuperacionHuecoId.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(1);
        ((Domain.Auditoria.IAccionAuditoriaPropia)conexion).ConsumirAccionAuditoria()
            .Should().Be(Domain.Auditoria.RegistroAuditoria.AccionHuecoReconocido, "la fila de auditoría lleva la acción propia");
    }

    /// <summary>
    /// Un Gestor CAE fuera de cartera (sin Asignación de Cartera sobre el Tenant) y cualquier otro rol reciben el rechazo y
    /// la marca se queda. Con una sola de las dos condiciones del Gestor CAE (rol o cartera), el rechazo se mantiene.
    /// </summary>
    [Theory]
    [InlineData("GestorCae", false)]
    [InlineData("CoordinadorCae", true)]
    [InlineData("DireccionCae", true)]
    [InlineData("Consulta", true)]
    [InlineData("Cliente", false)]
    [InlineData(null, true)]
    public async Task Un_gestor_fuera_de_cartera_y_cualquier_otro_rol_reciben_el_rechazo_y_la_marca_se_queda(string? rol, bool alcanzaElTenantEntero)
    {
        var (conexion, conexiones, marca) = ConMarca();
        var unitOfWork = new UnitOfWorkFalso();
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, alcanzaElTenantEntero: alcanzaElTenantEntero);

        var resultado = await Handler(conexiones, unitOfWork, rol, alcance, cerrojo)
            .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, marca), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReconocerAvisoHuecoCommandHandler.CodigoSinPermiso);
        conexion.HuecoPendienteDesdeUtc.Should().Be(marca);
        cerrojo.Pedidos.Should().BeEmpty("se corta antes de leer la conexión");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    private sealed class SesionPrivilegiadaActualFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    /// <summary>
    /// Soporte TALVEG en Sesión Privilegiada (también en impersonación de un Administrador) no confirma: la pipeline lo
    /// rechaza por ser de solo lectura antes de llegar al handler, y la marca se queda.
    /// </summary>
    [Theory]
    [InlineData(Domain.Plataforma.CapacidadPrivilegio.Impersonacion)]
    [InlineData(Domain.Plataforma.CapacidadPrivilegio.SoporteLectura)]
    public async Task Soporte_TALVEG_en_Sesion_Privilegiada_no_llega_al_handler(Domain.Plataforma.CapacidadPrivilegio capacidad)
    {
        var (conexion, conexiones, marca) = ConMarca();
        var tenant = Guid.NewGuid();
        var sesion = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), tenant, capacidad, null);
        var unitOfWork = new UnitOfWorkFalso();
        var behavior = new AutorizacionEscrituraBehavior<ReconocerAvisoHuecoCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"), new SesionPrivilegiadaActualFalsa(sesion), new TenantActualFalso(tenant));
        var comando = new ReconocerAvisoHuecoCommand(conexion.Id, marca);

        var resultado = await behavior.Handle(comando, ct => Handler(conexiones, unitOfWork).Handle(comando, ct), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Autorizacion.SesionPrivilegiadaSoloLectura");
        conexion.HuecoPendienteDesdeUtc.Should().Be(marca);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Una conexión de otro Tenant no se lee (filtro de Tenant y RLS: el repositorio no la devuelve), y una que el alcance
    /// no deja ver tampoco: las dos salen como no encontradas y la marca se queda.
    /// </summary>
    [Fact]
    public async Task Una_conexion_de_otro_Tenant_o_no_visible_no_se_reconoce()
    {
        var (conexion, conexiones, marca) = ConMarca();
        var deOtroTenant = new ConexionIntegracionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var otroTenant = await Handler(deOtroTenant, unitOfWork)
            .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, marca), CancellationToken.None);
        var noVisible = await Handler(conexiones, unitOfWork, alcance: new AlcanceDatosServiceFalso(conexionesIntegracionAjenas: [conexion.Id]))
            .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, marca), CancellationToken.None);

        otroTenant.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        noVisible.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        conexion.HuecoPendienteDesdeUtc.Should().Be(marca);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_conexion_de_un_cliente_fuera_de_la_cartera_visible_no_se_reconoce()
    {
        var conexion = new ConexionIntegracion("cae@cliente.test", "Buzón CAE", clienteId: Guid.NewGuid()).Verificada();
        var marca = DateTime.UtcNow.AddHours(-3);
        conexion.RegistrarNotificacionDescartada(marca + ConexionIntegracion.RetrasoMaximoNotificacion).Should().BeTrue("control");
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()], alcanzaElTenantEntero: true);

        var resultado = await Handler(conexiones, unitOfWork, "GestorCae", alcance)
            .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, marca), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        conexion.HuecoPendienteDesdeUtc.Should().Be(marca);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    public static TheoryData<string> EstadosNoReconocibles => new() { "Deshabilitada", "ConError", "MarcaPorRegistrar" };

    /// <summary>
    /// Con la conexión Deshabilitada o en error, o con una marca del hueco por registrar, el correo puede seguir sin llegar:
    /// rechazo con código propio, sin guardar. Lo decide la conexión releída bajo el cerrojo, no la que se cargó antes.
    /// </summary>
    [Theory]
    [MemberData(nameof(EstadosNoReconocibles))]
    public async Task Sin_la_conexion_Habilitada_o_con_marca_por_registrar_recibe_el_rechazo(string estado)
    {
        var (conexion, conexiones, marca) = ConMarca();
        // Con rastreo, ObtenerPorIdAsync devuelve la instancia cargada antes del cerrojo (Habilitada y reconocible): solo la
        // relectura con ObtenerActualPorIdAsync ve el estado de la base.
        conexiones.SimularRastreo = true;
        var unitOfWork = new UnitOfWorkFalso();
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        ConexionIntegracion? enBase = null;
        // Conexiones cargó la fila Habilitada; al tomar el cerrojo, la base ya tiene otro estado.
        cerrojo.AlTomar = id => enBase = conexiones.SustituirEnBase(id, c =>
        {
            c.MarcarIdentidadVerificada(DateTime.UtcNow);
            c.RegistrarNotificacionDescartada(marca + ConexionIntegracion.RetrasoMaximoNotificacion);
            switch (estado)
            {
                case "Deshabilitada": c.Deshabilitar(); break;
                case "ConError": c.MarcarConError("token caducado", DateTime.UtcNow); break;
                // Habilitada con un valor por registrar: el dominio no deja llegar ahí por sus transiciones (Rehabilitar
                // lo registra), pero el handler lo comprueba igual, por si la base lo trae.
                default: typeof(ConexionIntegracion).GetProperty(nameof(ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc))!
                    .SetValue(c, marca.AddHours(-1)); break;
            }
            c.HuecoPendienteDesdeUtc.Should().NotBeNull("control: la base tiene marca");
        });

        var resultado = await Handler(conexiones, unitOfWork, cerrojo: cerrojo)
            .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, marca), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReconocerAvisoHuecoCommandHandler.CodigoNoReconocible, estado);
        enBase!.HuecoPendienteDesdeUtc.Should().NotBeNull("la marca se queda");
        conexion.HuecoPendienteDesdeUtc.Should().Be(marca, "la instancia cargada antes del cerrojo tampoco se toca");
        conexiones.VecesLeidaActual.Should().Be(1, "la decisión sale de la relectura bajo el cerrojo");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_marca_devuelve_exito_sin_guardar()
    {
        var (conexion, conexiones, marca) = ConMarca();
        conexiones.SimularRastreo = true;
        // Otra persona confirmó antes, o la recuperación terminó: la base ya no tiene marca.
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        cerrojo.AlTomar = id => conexiones.SustituirEnBase(id, c => c.MarcarIdentidadVerificada(DateTime.UtcNow));
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await Handler(conexiones, unitOfWork, cerrojo: cerrojo)
            .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, marca), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
        conexion.HuecoPendienteDesdeUtc.Should().Be(marca, "la instancia cargada antes del cerrojo no se reconoce");
        conexiones.VecesLeidaActual.Should().Be(1);
    }

    [Fact]
    public async Task Una_marca_distinta_de_la_vista_no_se_reconoce()
    {
        var (conexion, conexiones, marca) = ConMarca();
        // Una pérdida anterior adelantó la marca después de que Conexiones cargara el aviso.
        var anterior = marca.AddHours(-2);
        conexion.RegistrarNotificacionDescartada(anterior + ConexionIntegracion.RetrasoMaximoNotificacion).Should().BeTrue();
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await Handler(conexiones, unitOfWork)
            .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, marca), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReconocerAvisoHuecoCommandHandler.CodigoAvisoCambiado);
        conexion.HuecoPendienteDesdeUtc.Should().Be(anterior);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_choque_de_concurrencia_al_guardar_descarta_lo_rastreado_y_no_reintenta()
    {
        var (conexion, conexiones, marca) = ConMarca();
        var unitOfWork = new UnitOfWorkFalso { ExcepcionAlGuardar = new DbUpdateConcurrencyException("la ingesta guardó antes") };
        var descarte = new DescarteContado();

        var resultado = await Handler(conexiones, unitOfWork, descarte: descarte)
            .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, marca), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReconocerAvisoHuecoCommandHandler.CodigoAvisoCambiado);
        descarte.Veces.Should().Be(1);
        unitOfWork.TokensDeGuardado.Should().ContainSingle("un solo intento: reintentar reconocería una pérdida que nadie ha visto");
    }

    [Fact]
    public async Task Con_el_cerrojo_ocupado_pide_volver_a_intentarlo_sin_guardar()
    {
        var (conexion, conexiones, marca) = ConMarca();
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        cerrojo.Ocupadas.Add(conexion.Id);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await Handler(conexiones, unitOfWork, cerrojo: cerrojo)
            .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, marca), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(DesconectarBuzonCommandHandler.CodigoVerificacionEnCurso);
        conexion.HuecoPendienteDesdeUtc.Should().Be(marca);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Una pasada de la recuperación que empezó antes de confirmar y espera a Graph mientras se confirma: al volver, su
    /// recuperación ya no coincide con la conexión y no guarda su página, ni el cursor, ni vuelve a poner nada.
    /// </summary>
    [Fact]
    public async Task Una_pasada_de_la_recuperacion_anterior_al_reconocimiento_no_reescribe_nada_despues()
    {
        var (conexion, conexiones, marca) = ConMarca();
        var eventos = new EventoWebhookRepositorioFalso();
        var suscripciones = new SuscripcionWebhookRepositorioFalso();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", DateTime.UtcNow.AddDays(2)));
        var graph = new Microsoft365GraphClientFalso();
        graph.PaginasRecibidos.Add(Result.Exito(new PaginaMensajesRecibidosGraphDto([new("m1", marca.AddMinutes(10))], false)));
        var unitOfWork = new UnitOfWorkFalso();
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        var guardadosTrasReconocer = -1;
        graph.AlListarRecibidos = indice =>
        {
            if (indice != 0)
                return;
            var reconocido = Handler(conexiones, unitOfWork, cerrojo: cerrojo)
                .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, marca), CancellationToken.None).GetAwaiter().GetResult();
            reconocido.EsExitoso.Should().BeTrue("control: se confirma mientras la pasada espera a Graph");
            guardadosTrasReconocer = unitOfWork.VecesGuardado;
        };
        var servicio = new RecuperacionHuecoCorreoService(conexiones, eventos, new ConversacionRepositorioFalso(), suscripciones, graph,
            new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), graph), unitOfWork, cerrojo, new DescarteContado(),
            NullLogger<RecuperacionHuecoCorreoService>.Instance);

        var resultado = await servicio.RecuperarConexionAsync(conexion.Id, CancellationToken.None);

        resultado.Should().Be(ResultadoRecuperacionHueco.Anulada);
        guardadosTrasReconocer.Should().BeGreaterThan(0, "control: el reconocimiento se guardó durante la pasada");
        unitOfWork.VecesGuardado.Should().Be(guardadosTrasReconocer, "la pasada vieja no guarda nada después");
        eventos.Eventos.Should().BeEmpty("la página leída para la recuperación anulada no se encola");
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.RecuperacionHuecoCursorUtc.Should().BeNull();
        conexion.RecuperacionHuecoId.Should().BeNull();
    }
}
