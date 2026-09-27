using CaeManager.Application.Common;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Reclamaciones;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// Por el camino sin Conexión Microsoft 365 la reclamación sale por
/// <see cref="IEmailService"/>, que devuelve <see cref="Result"/> en vez de
/// lanzar. Antes el resultado se ignoraba: con el SMTP caído, la reclamación
/// quedaba registrada en el historial y la UI la daba por enviada sin que
/// nadie la hubiera recibido.
///
/// <para>
/// Contrato: si ningún destinatario la recibe, no se registra nada y el
/// resultado es un fallo (igual que el camino con buzón conectado). Si solo
/// algunos la reciben, el historial registra a esos —un correo entregado no
/// se deshace— y el resultado sigue siendo un fallo que nombra a los demás,
/// para que la UI no lo pinte como enviado del todo.
/// </para>
/// </summary>
public class FalloSmtpDeLaReclamacionTests
{
    private const string Contacto = "contacto@refrielectric.example";
    private const string Prl = "prl@refrielectric.example";

    private sealed record Entorno(
        RegistroEnvioReclamacionService Servicio,
        ReclamacionDocumentalRepositorioQueCaptura Repositorio,
        UnitOfWorkFalso UnitOfWork);

    private static Entorno Construir(params string[] destinatariosQueFallan)
    {
        var repositorio = new ReclamacionDocumentalRepositorioQueCaptura();
        var unitOfWork = new UnitOfWorkFalso();

        var servicio = new RegistroEnvioReclamacionService(
            new IntegracionesQueryContextFalso(),
            new EmailServiceQueFallaPara(destinatariosQueFallan),
            repositorio,
            new CurrentUserServiceFalso(Guid.NewGuid()),
            new CorreoDelActorRealFalso(),
            new MediatorSinUsoFalso(),
            NullLogger<RegistroEnvioReclamacionService>.Instance,
            unitOfWork);

        return new Entorno(servicio, repositorio, unitOfWork);
    }

    private static Task<Result> Enviar(Entorno entorno, params string[] destinatarios) =>
        entorno.Servicio.EnviarYRegistrarAsync(
            new TitularReclamacion(Guid.NewGuid(), "Refrielectric SL", AmbitoAplicacion.Empresa),
            [Guid.NewGuid()],
            destinatarios,
            "Documentación pendiente",
            "<p>Nos faltan los EPI.</p>",
            CancellationToken.None);

    [Fact]
    public async Task Si_el_SMTP_falla_la_reclamacion_no_se_registra_y_el_resultado_es_un_fallo()
    {
        var entorno = Construir(Contacto);

        var resultado = await Enviar(entorno, Contacto);

        resultado.EsFallido.Should().BeTrue("nadie recibió la reclamación; darla por enviada engaña al Gestor CAE");
        resultado.Error.Codigo.Should().Be("Reclamacion.EnvioFallido");
        entorno.Repositorio.Agregadas.Should().BeEmpty();
        entorno.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Si_fallan_todos_los_destinatarios_no_se_registra_nada()
    {
        var entorno = Construir(Contacto, Prl);

        var resultado = await Enviar(entorno, Contacto, Prl);

        resultado.Error.Codigo.Should().Be("Reclamacion.EnvioFallido");
        entorno.Repositorio.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_falla_uno_de_varios_se_registra_solo_a_quien_la_recibio_y_se_avisa_del_que_fallo()
    {
        var entorno = Construir(Prl);

        var resultado = await Enviar(entorno, Contacto, Prl);

        resultado.EsFallido.Should().BeTrue("la UI no puede pintar como enviada del todo una reclamación que no llegó a alguien");
        resultado.Error.Codigo.Should().Be("Reclamacion.EnvioParcial");
        resultado.Error.Mensaje.Should().Contain(Prl).And.Contain(Contacto);
        entorno.Repositorio.Agregadas.Should().ContainSingle()
            .Which.DestinatarioEmail.Should().Be(Contacto, "el historial cuenta lo que pasó: solo Contacto la recibió");
        entorno.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Si_el_SMTP_entrega_a_todos_se_registra_a_todos_y_el_resultado_es_exito()
    {
        var entorno = Construir();

        var resultado = await Enviar(entorno, Contacto, Prl);

        resultado.EsExitoso.Should().BeTrue();
        entorno.Repositorio.Agregadas.Should().ContainSingle()
            .Which.DestinatarioEmail.Should().Be($"{Contacto}; {Prl}");
    }

    private sealed class EmailServiceQueFallaPara(IReadOnlyCollection<string> destinatariosQueFallan) : IEmailService
    {
        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(destinatariosQueFallan.Contains(destinatarioEmail)
                ? Result.Fallo(Error.Crear("Correo.EnvioFallido", "SMTP no disponible."))
                : Result.Exito());
    }

    private sealed class ReclamacionDocumentalRepositorioQueCaptura : IReclamacionDocumentalRepository
    {
        public List<ReclamacionDocumental> Agregadas { get; } = [];

        public void Agregar(ReclamacionDocumental reclamacion) => Agregadas.Add(reclamacion);
    }

    private sealed class CorreoDelActorRealFalso : ICorreoDelActorReal
    {
        public Task<string?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("marta@arcosspa.example");
    }

    /// <summary>Sin Conexión no se despacha nada por el mediador: cualquier uso revienta.</summary>
    private sealed class MediatorSinUsoFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => throw new NotSupportedException();

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
