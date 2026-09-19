using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Reclamaciones;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// Decisión D3 (propietario, 2026-09-19): «la respuesta debe recibirla el
/// Gestor CAE que es quien está haciendo el reclamo».
///
/// <para>
/// Aquí se prueba la mitad de Application: de quién sale el correo que se
/// pone en <c>Reply-To</c>. La otra mitad —que el <c>MimeMessage</c> lleva
/// esa cabecera y conserva el <c>From</c> del buzón de TALVEG— se prueba en
/// <c>SmtpEmailServiceTests</c>, que es la capa que construye el mensaje.
/// Ninguna de las dos presta evidencia a la otra.
/// </para>
///
/// <para>
/// Solo el camino <b>sin</b> Conexión Microsoft 365. Con Conexión la
/// reclamación sale del buzón genérico del Tenant propietario y abre un hilo
/// en Comunicaciones anclado al titular: la respuesta vuelve a ese hilo, que
/// el Gestor CAE emisor ve por su Asignación de Cartera sobre la Empresa
/// contraparte. Ese camino no lleva <c>Reply-To</c> ni lo necesita, y este
/// cambio no lo toca — lo cubre el último test del archivo.
/// </para>
/// </summary>
public class ReplyToDeLaReclamacionPorSmtpTests
{
    private static readonly Guid TitularEmpresaId = Guid.NewGuid();

    private sealed record Entorno(
        RegistroEnvioReclamacionService Servicio,
        EmailServiceQueCapturaFalso Correo,
        IntegracionesQueryContextFalso Integraciones,
        MediatorDeEnvioFalso Mediator);

    private static Entorno Construir(string? correoDelActorReal, bool conBuzonConectado = false)
    {
        var integraciones = new IntegracionesQueryContextFalso();

        if (conBuzonConectado)
        {
            // Buzón genérico del Tenant propietario: sin ClienteId y sin
            // GestorPropietarioId, que es el único que el servicio acepta.
            integraciones.ConexionesLista.Add(
                new ConexionIntegracion("buzon@tenant.example", "Buzón genérico del tenant"));
        }

        var correo = new EmailServiceQueCapturaFalso();
        var mediator = new MediatorDeEnvioFalso();

        var servicio = new RegistroEnvioReclamacionService(
            integraciones,
            correo,
            new ReclamacionDocumentalRepositorioFalso(),
            new CurrentUserServiceFalso(Guid.NewGuid()),
            new CorreoDelActorRealFalso(correoDelActorReal),
            mediator,
            NullLogger<RegistroEnvioReclamacionService>.Instance,
            new UnitOfWorkFalso());

        return new Entorno(servicio, correo, integraciones, mediator);
    }

    private static Task<Result> Enviar(Entorno entorno) =>
        entorno.Servicio.EnviarYRegistrarAsync(
            new TitularReclamacion(TitularEmpresaId, "Refrielectric SL", AmbitoAplicacion.Empresa),
            [Guid.NewGuid()],
            ["contacto@refrielectric.example"],
            "Documentación pendiente",
            "<p>Nos faltan los EPI.</p>",
            CancellationToken.None);

    [Fact]
    public async Task La_reclamacion_por_SMTP_pide_que_la_respuesta_vuelva_al_Gestor_CAE_que_la_emite()
    {
        var entorno = Construir(correoDelActorReal: "marta@arcosspa.example");

        var resultado = await Enviar(entorno);

        resultado.EsExitoso.Should().BeTrue();
        entorno.Correo.Enviados.Should().ContainSingle()
            .Which.ResponderA.Should().Be(
                "marta@arcosspa.example",
                "decisión D3: la respuesta del contacto de la Empresa contraparte la recibe el Gestor CAE que reclama, " +
                "no el buzón de TALVEG del que sale el correo");
    }

    /// <summary>
    /// El correo sale del carril de auditoría —el actor real—, nunca del de
    /// autorización. Hoy no existe la impersonación, así que la diferencia no
    /// se puede observar de punta a punta; lo que sí queda fijado es que el
    /// servicio no usa <c>ICurrentUserService</c> para esto: el
    /// <c>CurrentUserServiceFalso</c> del entorno lleva otro usuario y su
    /// correo no aparece por ninguna parte.
    /// </summary>
    [Fact]
    public async Task El_Reply_To_no_sale_del_carril_de_autorizacion()
    {
        var entorno = Construir(correoDelActorReal: "actor.real@talveg.example");

        await Enviar(entorno);

        entorno.Correo.Enviados.Should().ContainSingle()
            .Which.ResponderA.Should().Be("actor.real@talveg.example");
    }

    [Fact]
    public async Task Sin_correo_en_la_cuenta_de_quien_reclama_el_envio_sigue_adelante_sin_Reply_To()
    {
        var entorno = Construir(correoDelActorReal: null);

        var resultado = await Enviar(entorno);

        resultado.EsExitoso.Should().BeTrue("la reclamación es la acción de negocio y no depende de tener a quién responder");
        entorno.Correo.Enviados.Should().ContainSingle()
            .Which.ResponderA.Should().BeNull();
    }

    [Fact]
    public async Task Cada_destinatario_del_lote_recibe_el_mismo_Reply_To()
    {
        var entorno = Construir(correoDelActorReal: "marta@arcosspa.example");

        await entorno.Servicio.EnviarYRegistrarAsync(
            new TitularReclamacion(TitularEmpresaId, "Refrielectric SL", AmbitoAplicacion.Empresa),
            [Guid.NewGuid()],
            ["contacto@refrielectric.example", "prl@refrielectric.example"],
            "Documentación pendiente",
            "<p>Nos faltan los EPI.</p>",
            CancellationToken.None);

        entorno.Correo.Enviados.Should().HaveCount(2);
        entorno.Correo.Enviados.Should().OnlyContain(e => e.ResponderA == "marta@arcosspa.example");
    }

    [Fact]
    public async Task La_reclamacion_por_SMTP_se_clasifica_como_Requerimiento()
    {
        var entorno = Construir(correoDelActorReal: "marta@arcosspa.example");

        await Enviar(entorno);

        entorno.Correo.Enviados.Should().ContainSingle()
            .Which.Tipo.Should().Be(
                TipoAvisoCorreo.Requerimiento,
                "es el tipo que decide el pie, y el pie solo invita a responder cuando hay Reply-To");
    }

    [Fact]
    public async Task Con_Conexion_habilitada_no_se_envia_ningun_correo_por_SMTP()
    {
        var entorno = Construir(correoDelActorReal: "marta@arcosspa.example", conBuzonConectado: true);

        var resultado = await Enviar(entorno);

        resultado.EsExitoso.Should().BeTrue();
        entorno.Correo.Enviados.Should().BeEmpty(
            "con buzón conectado la reclamación abre un hilo en Comunicaciones y la respuesta vuelve ahí; " +
            "este cambio no toca ese camino");
        entorno.Mediator.Enviados.Should().ContainSingle();
    }

    /// <summary>
    /// La garantía de que el <c>Reply-To</c> nunca puede llevar el correo de
    /// un usuario simulado no descansa solo en qué carril de identidad se
    /// consulta: hoy descansa, antes que eso, en que una Sesión Privilegiada
    /// de soporte <b>no puede emitir una reclamación en absoluto</b>. Es la
    /// inspección de solo lectura del plano 3 (ADR-011 § 4bis), y
    /// <c>AutorizacionEscrituraBehavior</c> la impone antes de que el handler
    /// llegue a correr.
    ///
    /// <para>
    /// Con los comandos REALES de reclamación, no con un <c>FalsoCommand</c>:
    /// lo que hay que demostrar es que estos dos concretos están dentro de la
    /// denegación, no que el behavior sabe denegar. El rol es "Administrador"
    /// —el peor caso, un técnico de TALVEG que es Administrador en su propio
    /// tenant— justamente para que la denegación no pueda venir del rol.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    public async Task Una_Sesion_Privilegiada_de_soporte_no_puede_emitir_una_reclamacion(CapacidadPrivilegio capacidad)
    {
        var sesion = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), capacidad, null);

        var behaviorEmpresa = new AutorizacionEscrituraBehavior<EnviarReclamacionEmpresaCommand, Result<EnvioReclamacionResultado>>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new SesionPrivilegiadaActualFalsa(sesion),
            new TenantActualFalso(null));

        var handlerLlamado = false;
        var resultado = await behaviorEmpresa.Handle(
            new EnviarReclamacionEmpresaCommand(TitularEmpresaId, [Guid.NewGuid()]),
            _ =>
            {
                handlerLlamado = true;
                return Task.FromResult(Result.Exito(new EnvioReclamacionResultado([], [])));
            },
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SesionPrivilegiadaSoloLectura");
        handlerLlamado.Should().BeFalse(
            "si el handler llegara a correr, la reclamación saldría con el Reply-To de quien soporte esté simulando");
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    public async Task Una_Sesion_Privilegiada_tampoco_puede_reclamar_en_ambito_Cliente(CapacidadPrivilegio capacidad)
    {
        var sesion = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), capacidad, null);

        var behaviorCliente = new AutorizacionEscrituraBehavior<EnviarReclamacionCommand, Result<EnvioReclamacionResultado>>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new SesionPrivilegiadaActualFalsa(sesion),
            new TenantActualFalso(null));

        var resultado = await behaviorCliente.Handle(
            new EnviarReclamacionCommand(Guid.NewGuid(), [Guid.NewGuid()]),
            _ => Task.FromResult(Result.Exito(new EnvioReclamacionResultado([], []))),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SesionPrivilegiadaSoloLectura");
    }

    private sealed class SesionPrivilegiadaActualFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class EmailServiceQueCapturaFalso : IEmailService
    {
        public List<(string Destinatario, TipoAvisoCorreo Tipo, string? ResponderA)> Enviados { get; } = [];

        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            Enviados.Add((destinatarioEmail, tipo, responderA));
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class CorreoDelActorRealFalso(string? correo) : ICorreoDelActorReal
    {
        public Task<string?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(correo);
    }

    private sealed class ReclamacionDocumentalRepositorioFalso : IReclamacionDocumentalRepository
    {
        public void Agregar(ReclamacionDocumental reclamacion) { }
    }

    /// <summary>
    /// Registra lo despachado y devuelve un id de conversación para el camino
    /// con Conexión. Cualquier otro request revienta a propósito, igual que
    /// los dobles hermanos de los tests de reclamación.
    /// </summary>
    private sealed class MediatorDeEnvioFalso : IMediator
    {
        public List<object> Enviados { get; } = [];
        public List<INotification> Publicados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not EnviarMensajeNuevoCommand)
                throw new NotSupportedException($"El doble no cubre {request.GetType().Name}.");

            Enviados.Add(request);
            return Task.FromResult((TResponse)(object)Result.Exito(Guid.NewGuid()));
        }

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            Publicados.Add(notification);
            return Task.CompletedTask;
        }

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
