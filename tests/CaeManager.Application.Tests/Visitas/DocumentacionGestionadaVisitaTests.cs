using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Visitas.Commands.EnviarPaqueteAcreditacionVisita;
using CaeManager.Application.Visitas.Commands.MarcarDocumentacionGestionada;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// Las dos salidas de «Por gestionar» (decisión del propietario, 2026-10-09): la marca manual y
/// el envío del paquete de acreditación desde la Visita. Lo que se prueba aquí es la regla de
/// Application: quién puede, sobre qué Visita, con qué versión, y que el envío fallido o
/// rechazado no deja nada marcado. Que añadir o quitar un Trabajador borra la marca está en
/// <c>TrabajadoresDeVisitaCommandHandlerTests</c>; el orden y la columna, contra PostgreSQL, en
/// Integration.
/// </summary>
public class DocumentacionGestionadaVisitaTests
{
    private static readonly DateOnly Fecha = new(2026, 1, 1);

    private sealed class Escenario
    {
        public Centro Centro { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "Nave Norte");
        public Visita Visita { get; }
        public VisitaRepositorioFalso Visitas { get; } = new();
        public CentrosQueryContextFalso Centros { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        public AlcanceDatosServiceFalso Alcance { get; set; } = new();
        public EmisorDeMensajes Emisor { get; } = new();

        public Escenario()
        {
            Visita = new Visita(Centro.Id, Fecha, Fecha.AddDays(1), null);
            Visitas.Agregar(Visita);
            Centros.ListaCentros.Add(Centro);
        }

        public Task<Result> MarcarAsync(Guid version = default) =>
            new MarcarDocumentacionGestionadaCommandHandler(Visitas, Centros, UnitOfWork, Alcance)
                .Handle(new MarcarDocumentacionGestionadaCommand(Visita.Id, version), CancellationToken.None);

        public Task<Result<Guid>> EnviarPaqueteAsync(Guid? version = null) =>
            new EnviarPaqueteAcreditacionVisitaCommandHandler(
                    Visitas, Centros, Alcance, Emisor, UnitOfWork, NullLogger<EnviarPaqueteAcreditacionVisitaCommandHandler>.Instance)
                .Handle(
                    new EnviarPaqueteAcreditacionVisitaCommand(
                        Visita.Id, version ?? Visita.Version, Guid.NewGuid(), ["titular@example.test"], "Acceso", "<p>Adjunto</p>",
                        [new AdjuntoParaEnviarDto("paquete.zip", "application/zip", [1, 2, 3])]),
                    CancellationToken.None);

        public static AlcanceDatosServiceFalso FueraDeAlcance() => new(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]);
    }

    /// <summary>ISender mínimo: solo sabe «enviar» el mensaje, y apunta lo que le llega.</summary>
    private sealed class EmisorDeMensajes : ISender
    {
        public List<EnviarMensajeNuevoCommand> Enviados { get; } = [];
        public Result<Guid> Respuesta { get; set; } = Result.Exito(Guid.NewGuid());

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not EnviarMensajeNuevoCommand mensaje)
                throw new NotSupportedException(request.GetType().Name);

            Enviados.Add(mensaje);
            return Task.FromResult((TResponse)(object)Respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // ---------------------------------------------------------------- Marca manual

    [Fact]
    public async Task Marcar_deja_la_documentacion_gestionada_con_fecha_y_guarda()
    {
        var escenario = new Escenario();
        var antes = DateTime.UtcNow;

        var resultado = await escenario.MarcarAsync(escenario.Visita.Version);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Visita.DocumentacionGestionada.Should().BeTrue();
        escenario.Visita.DocumentacionGestionadaEnUtc.Should().BeOnOrAfter(antes);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Marcar_fuera_del_alcance_de_gestion_responde_no_encontrada_y_no_marca()
    {
        var escenario = new Escenario { Alcance = Escenario.FueraDeAlcance() };

        // Con una versión desfasada: fuera de alcance no se revela ni el conflicto.
        var resultado = await escenario.MarcarAsync(Guid.NewGuid());

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        escenario.Visita.DocumentacionGestionada.Should().BeFalse();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Marcar_con_alcance_de_lectura_sin_alcance_de_gestion_no_marca()
    {
        var escenario = new Escenario();
        escenario.Alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [escenario.Centro.Id], centroIdsParaGestion: []);

        var resultado = await escenario.MarcarAsync();

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        escenario.Visita.DocumentacionGestionada.Should().BeFalse();
    }

    [Fact]
    public async Task Marcar_una_visita_cancelada_se_rechaza()
    {
        var escenario = new Escenario();
        escenario.Visita.Cancelar(DateTime.UtcNow, null);

        var resultado = await escenario.MarcarAsync();

        resultado.Error.Codigo.Should().Be("Visita.Cancelada");
        escenario.Visita.DocumentacionGestionada.Should().BeFalse();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Alguien añadió o quitó un Trabajador (versión nueva) mientras el panel seguía abierto:
    /// no se da por gestionado lo que no se vio.
    /// </summary>
    [Fact]
    public async Task Marcar_con_una_version_desfasada_se_rechaza_por_concurrencia()
    {
        var escenario = new Escenario();
        var versionVista = escenario.Visita.Version;
        escenario.Visita.RegistrarCambioDeTrabajadores();

        var resultado = await escenario.MarcarAsync(versionVista);

        resultado.Error.Codigo.Should().Be(Application.Common.ConcurrenciaOptimista.CodigoConflicto);
        escenario.Visita.DocumentacionGestionada.Should().BeFalse();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Marcar_en_un_Centro_sin_gestion_CAE_se_rechaza()
    {
        var escenario = new Escenario();
        escenario.Centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        var resultado = await escenario.MarcarAsync();

        resultado.Error.Should().Be(MarcarDocumentacionGestionadaCommandHandler.CentroSinGestionCae);
        escenario.Visita.DocumentacionGestionada.Should().BeFalse();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    // ---------------------------------------------------------------- Envío del paquete

    [Fact]
    public async Task Enviar_el_paquete_envia_el_mensaje_con_su_adjunto_y_marca_la_visita()
    {
        var escenario = new Escenario();

        var resultado = await escenario.EnviarPaqueteAsync(escenario.Visita.Version);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(escenario.Emisor.Respuesta.Valor, "devuelve la conversación del envío");
        escenario.Emisor.Enviados.Should().ContainSingle()
            .Which.Adjuntos.Should().ContainSingle().Which.NombreArchivo.Should().Be("paquete.zip");
        escenario.Visita.DocumentacionGestionada.Should().BeTrue();
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    /// <summary>La Visita es una coordenada del cliente, no autoridad: sin alcance no sale el correo.</summary>
    [Fact]
    public async Task Enviar_el_paquete_de_una_visita_fuera_del_alcance_de_gestion_no_envia_ni_marca()
    {
        var escenario = new Escenario { Alcance = Escenario.FueraDeAlcance() };

        var resultado = await escenario.EnviarPaqueteAsync();

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        escenario.Emisor.Enviados.Should().BeEmpty();
        escenario.Visita.DocumentacionGestionada.Should().BeFalse();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Enviar_el_paquete_de_una_visita_cancelada_no_envia()
    {
        var escenario = new Escenario();
        escenario.Visita.Cancelar(DateTime.UtcNow, null);

        var resultado = await escenario.EnviarPaqueteAsync();

        resultado.Error.Codigo.Should().Be("Visita.Cancelada");
        escenario.Emisor.Enviados.Should().BeEmpty();
    }

    /// <summary>
    /// El paquete se preparó para quienes entraban entonces. Si la Visita cambió, el adjunto
    /// saldría sin el Trabajador nuevo: no se envía.
    /// </summary>
    [Fact]
    public async Task Enviar_un_paquete_preparado_antes_de_que_cambiara_la_visita_no_envia_ni_marca()
    {
        var escenario = new Escenario();
        var versionAlPreparar = escenario.Visita.Version;
        escenario.Visita.RegistrarCambioDeTrabajadores();

        var resultado = await escenario.EnviarPaqueteAsync(versionAlPreparar);

        resultado.Error.Codigo.Should().Be(Application.Common.ConcurrenciaOptimista.CodigoConflicto);
        escenario.Emisor.Enviados.Should().BeEmpty();
        escenario.Visita.DocumentacionGestionada.Should().BeFalse();
    }

    [Fact]
    public async Task Si_el_envio_falla_la_visita_sigue_por_gestionar()
    {
        var escenario = new Escenario();
        var fallo = Error.Crear("ConexionIntegracion.NoDisponible", "Este buzón no está disponible.");
        escenario.Emisor.Respuesta = Result.Fallo<Guid>(fallo);

        var resultado = await escenario.EnviarPaqueteAsync();

        resultado.Error.Should().Be(fallo);
        escenario.Visita.DocumentacionGestionada.Should().BeFalse();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    // ---------------------------------------------------------------- Lo que lee la lista

    private static VisitaListaDto Fila(
        bool documentosVigentes, DateTime? gestionadaEn, bool requiereGestionCae = true, bool cancelada = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Nave Norte", Guid.NewGuid(), "Cliente empresarial", Guid.NewGuid(), "Empresa",
            Fecha, Fecha.AddDays(1), TotalTrabajadores: 1, DocumentacionCompleta: documentosVigentes, NotificadoCliente: false,
            OrigenVisita.Correo, NivelUrgenciaVisita.Urgente, CentroRequiereGestionCae: requiereGestionCae,
            EstaCancelada: cancelada, DocumentacionGestionadaEnUtc: gestionadaEn);

    /// <summary>
    /// «Por gestionar» sale del estado guardado y no de los documentos: las dos combinaciones
    /// que distinguen una regla de la otra (vigentes sin marca, y con marca aunque falten).
    /// </summary>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void Por_gestionar_depende_de_la_marca_guardada_y_no_de_que_los_documentos_esten_vigentes(
        bool documentosVigentes, bool marcada, bool porGestionar)
    {
        var fila = Fila(documentosVigentes, marcada ? new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc) : null);

        fila.PorGestionar.Should().Be(porGestionar);
    }

    [Fact]
    public void Ni_una_visita_cancelada_ni_un_Centro_sin_gestion_CAE_estan_por_gestionar()
    {
        Fila(documentosVigentes: false, gestionadaEn: null, cancelada: true).PorGestionar.Should().BeFalse();
        Fila(documentosVigentes: false, gestionadaEn: null, requiereGestionCae: false).PorGestionar.Should().BeFalse();
    }

    /// <summary>La misma regla P1-X2 que la marca manual: sin gestión CAE no hay paquete que enviar ni marca que poner.</summary>
    [Fact]
    public async Task Enviar_el_paquete_en_un_Centro_sin_gestion_CAE_no_envia_ni_marca()
    {
        var escenario = new Escenario();
        escenario.Centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        var resultado = await escenario.EnviarPaqueteAsync();

        resultado.Error.Should().Be(MarcarDocumentacionGestionadaCommandHandler.CentroSinGestionCae);
        escenario.Emisor.Enviados.Should().BeEmpty();
        escenario.Visita.DocumentacionGestionada.Should().BeFalse();
    }

    /// <summary>
    /// El correo ya salió: si la marca no se puede guardar, responder con error invitaría a
    /// reenviar el mismo paquete. El envío se da por bueno y la Visita se marca a mano.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Si_el_correo_sale_y_la_marca_no_se_guarda_el_envio_no_se_da_por_fallido(bool porConcurrencia)
    {
        var escenario = new Escenario();
        escenario.UnitOfWork.ExcepcionAlGuardar = porConcurrencia
            ? new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("otra sesión tocó la Visita")
            : new Microsoft.EntityFrameworkCore.DbUpdateException("la base no respondió");

        var resultado = await escenario.EnviarPaqueteAsync();

        resultado.EsExitoso.Should().BeTrue();
        escenario.Emisor.Enviados.Should().ContainSingle();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0, "control: el guardado de la marca falló de verdad");
    }

    /// <summary>
    /// «Sin versión» apagaría la única garantía del comando (que el paquete no salga si la
    /// Visita cambió desde que se preparó): aquí no es opcional, a diferencia de la marca manual.
    /// </summary>
    [Fact]
    public void El_envio_del_paquete_exige_la_version_de_la_Visita()
    {
        var sinVersion = new EnviarPaqueteAcreditacionVisitaCommand(
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), ["titular@example.test"], "Acceso", "<p>x</p>",
            [new AdjuntoParaEnviarDto("paquete.zip", "application/zip", [1])]);
        var conVersion = sinVersion with { VersionVisita = Guid.NewGuid() };

        new EnviarPaqueteAcreditacionVisitaCommandValidator().Validate(sinVersion).IsValid.Should().BeFalse();
        new EnviarPaqueteAcreditacionVisitaCommandValidator().Validate(conVersion).IsValid.Should().BeTrue("control: lo único que faltaba era la versión");
    }

    [Fact]
    public void El_envio_del_paquete_exige_el_adjunto()
    {
        var sinAdjunto = new EnviarPaqueteAcreditacionVisitaCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ["titular@example.test"], "Acceso", "<p>x</p>", []);

        new EnviarPaqueteAcreditacionVisitaCommandValidator().Validate(sinAdjunto).IsValid.Should().BeFalse();
    }
}
