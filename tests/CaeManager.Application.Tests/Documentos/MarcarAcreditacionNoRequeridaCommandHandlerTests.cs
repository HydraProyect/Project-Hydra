using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionNoRequerida;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// «Esta plataforma no lo pide» de la ventana de una Gestión (DECISION_LOG 2026-11-08, punto 3): deja la acreditación
/// No requerida, nunca toca el Documento, y solo dentro de la Asignación de Cartera de quien lo pide. El rol lo decide
/// <see cref="AutorizacionEscrituraBehavior{TRequest, TResponse}"/>, no la pantalla.
/// </summary>
public class MarcarAcreditacionNoRequeridaCommandHandlerTests
{
    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir)]
    [InlineData(EstadoAcreditacion.Subida)]
    [InlineData(EstadoAcreditacion.Aceptada)]
    [InlineData(EstadoAcreditacion.Rechazada)]
    public async Task Desde_cualquier_estado_la_deja_no_requerida_y_guarda_una_vez(EstadoAcreditacion estado)
    {
        var escenario = Escenario.Nuevo();
        escenario.Poner(estado);

        var resultado = await escenario.Handler().Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Estado), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.NoRequerida);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
        escenario.Documento.SustituidoEnUtc.Should().BeNull("el Documento en TALVEG no cambia");
    }

    [Fact]
    public async Task Desde_validada_con_una_fecha_vencida_borra_la_vigencia_para_no_poner_el_Centro_en_rojo()
    {
        var escenario = Escenario.Nuevo();
        escenario.Acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2020, 1, 1)));

        var resultado = await escenario.Handler().Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Estado), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.NoRequerida);
        escenario.Acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue();
        escenario.Acreditacion.FechaVencimientoEnPlataforma.Should().BeNull();
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Si_el_paso_cambio_desde_que_se_vio_no_lo_sobrescribe_a_ciegas()
    {
        // Se abrió la Gestión con el paso Pendiente de subir; entretanto otro Gestor CAE lo marcó rechazado por el portal.
        var escenario = Escenario.Nuevo();
        escenario.Poner(EstadoAcreditacion.Rechazada);

        var resultado = await escenario.Handler().Handle(
            new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, EstadoAcreditacion.PendienteDeSubir), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.CambiadaDesdeQueSeAbrio");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Rechazada, "el rechazo no sale de la cola sin aviso");
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
        escenario.Acreditaciones.LecturasActualizadas.Should().Be(1,
            "compara contra la base releída: con una entidad rastreada obsoleta la guarda no vería el cambio");
    }

    [Fact]
    public async Task Ya_no_requerida_no_escribe_nada()
    {
        var escenario = Escenario.Nuevo();
        escenario.Acreditacion.MarcarNoRequerida();

        var resultado = await escenario.Handler().Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Estado), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Gestor_CAE_con_el_Centro_del_acceso_fuera_de_su_cartera_falla_como_si_no_existiera_y_no_escribe()
    {
        // La cartera tiene otro Centro, que también tiene acceso de plataforma: la guarda mira el Centro de ESTE acceso.
        var escenario = Escenario.Nuevo();
        escenario.Acreditacion.MarcarSubida();
        var otroCentroId = Guid.NewGuid();
        escenario.Centros.ListaCanalesGestionDocumental.Add(
            CanalGestionDocumental.DePlataforma(otroCentroId, "Portal", escenario.ProveedorId, null, null, null));
        var fueraDeCartera = new AlcanceDatosServiceFalso(centroIdsParaGestion: [otroCentroId]);

        var resultado = await escenario.Handler(fueraDeCartera)
            .Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Estado), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_el_Centro_en_cartera_pero_el_Documento_no_visible_tampoco_escribe()
    {
        var escenario = Escenario.Nuevo();
        var documentoFuera = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()], centroIdsParaGestion: [escenario.CentroId]);

        var resultado = await escenario.Handler(documentoFuera)
            .Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Estado), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_el_Centro_del_acceso_en_su_cartera_si_escribe()
    {
        // Control positivo del caso anterior: la misma cartera, con el Centro de este acceso.
        var escenario = Escenario.Nuevo();
        var enCartera = new AlcanceDatosServiceFalso(centroIdsParaGestion: [escenario.CentroId]);

        var resultado = await escenario.Handler(enCartera)
            .Handle(new MarcarAcreditacionNoRequeridaCommand(escenario.Acreditacion.Id, escenario.Acreditacion.Estado), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Acreditacion.Estado.Should().Be(EstadoAcreditacion.NoRequerida);
    }

    [Fact]
    public async Task Falla_cuando_la_acreditacion_no_existe()
    {
        var escenario = Escenario.Nuevo();

        var resultado = await escenario.Handler().Handle(new MarcarAcreditacionNoRequeridaCommand(Guid.NewGuid(), EstadoAcreditacion.PendienteDeSubir), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_exige_la_acreditacion()
    {
        var validador = new MarcarAcreditacionNoRequeridaCommandValidator();

        validador.Validate(new MarcarAcreditacionNoRequeridaCommand(Guid.Empty, EstadoAcreditacion.PendienteDeSubir)).IsValid.Should().BeFalse();
        validador.Validate(new MarcarAcreditacionNoRequeridaCommand(Guid.NewGuid(), EstadoAcreditacion.PendienteDeSubir)).IsValid.Should().BeTrue();
        validador.Validate(new MarcarAcreditacionNoRequeridaCommand(Guid.NewGuid(), (EstadoAcreditacion)99)).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Ni_Consulta_ni_Cliente_ni_un_rol_desconocido_llegan_al_handler(string? rol)
    {
        var (resultado, llego) = await EjecutarConRolAsync(rol);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        llego.Should().BeFalse();
    }

    [Fact]
    public async Task Un_Gestor_CAE_si_llega_al_handler()
    {
        var (resultado, llego) = await EjecutarConRolAsync("GestorCae");

        resultado.EsExitoso.Should().BeTrue();
        llego.Should().BeTrue();
    }

    private static async Task<(Result Resultado, bool Llego)> EjecutarConRolAsync(string? rol)
    {
        var behavior = new AutorizacionEscrituraBehavior<MarcarAcreditacionNoRequeridaCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), new SinSesionPrivilegiada(), new SinTenant());
        var llego = false;

        var resultado = await behavior.Handle(
            new MarcarAcreditacionNoRequeridaCommand(Guid.NewGuid(), EstadoAcreditacion.PendienteDeSubir),
            _ =>
            {
                llego = true;
                return Task.FromResult(Result.Exito());
            },
            CancellationToken.None);

        return (resultado, llego);
    }

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private sealed class Escenario
    {
        public required Documento Documento { get; init; }
        public required AcreditacionDocumentoPlataforma Acreditacion { get; init; }
        public required Guid CentroId { get; init; }
        public required Guid ProveedorId { get; init; }
        public CentrosQueryContextFalso Centros { get; } = new();
        public DocumentoRepositorioFalso Documentos { get; } = new();
        public AcreditacionDocumentoPlataformaRepositorioFalso Acreditaciones { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();

        public static Escenario Nuevo()
        {
            var centroId = Guid.NewGuid();
            var proveedor = new ProveedorPlataformaCae("nalanda", "Nalanda");
            var canal = CanalGestionDocumental.DePlataforma(centroId, "Portal", proveedor.Id, null, null, null);
            var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
            var escenario = new Escenario
            {
                Documento = documento,
                Acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id),
                CentroId = centroId,
                ProveedorId = proveedor.Id
            };
            escenario.Centros.ListaCanalesGestionDocumental.Add(canal);
            escenario.Documentos.Agregar(documento);
            escenario.Acreditaciones.Agregar(escenario.Acreditacion);
            return escenario;
        }

        public void Poner(EstadoAcreditacion estado)
        {
            switch (estado)
            {
                case EstadoAcreditacion.Subida: Acreditacion.MarcarSubida(); break;
                case EstadoAcreditacion.Aceptada: Acreditacion.MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui); break;
                case EstadoAcreditacion.Rechazada: Acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow); break;
            }

            Acreditacion.Estado.Should().Be(estado, "el escenario parte del estado pedido");
        }

        public MarcarAcreditacionNoRequeridaCommandHandler Handler(AlcanceDatosServiceFalso? alcance = null) =>
            new(Acreditaciones, Documentos, alcance ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), Centros, UnitOfWork);
    }
}
