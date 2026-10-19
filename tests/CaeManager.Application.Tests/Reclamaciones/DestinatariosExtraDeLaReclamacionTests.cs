using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Reclamaciones;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// Hueco 5 del piloto Outbound (decidido 2026-10-16): «Pedir» y «Reclamar en lote» envían a los contactos de la agenda más los
/// destinatarios que el gestor añade a mano, con posibilidad de copia (CC). Aquí, la regla (formato, sin repetidos, tope) y el servicio
/// común de envío: a quién sale el correo por SMTP y por buzón conectado, y cómo queda en el registro.
/// </summary>
public class DestinatariosExtraDeLaReclamacionTests
{
    private const string Agenda = "marta@refrielectric.example";
    private const string Manual = "jefe.obra@subcontrata.example";
    private const string Copia = "coordinacion@arcosspa.example";

    // ---- La regla ----

    [Fact]
    public void Sin_extras_no_cambia_nada()
    {
        var resultado = DestinatariosExtraDeReclamacion.Resolver(null, null, [Agenda]);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Adicionales.Should().BeEmpty();
        resultado.Valor.EnCopia.Should().BeEmpty();
    }

    [Fact]
    public void Acepta_manuales_y_copia_y_los_recorta()
    {
        var resultado = DestinatariosExtraDeReclamacion.Resolver([$"  {Manual} "], [Copia], [Agenda]);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Adicionales.Should().Equal(Manual);
        resultado.Valor.EnCopia.Should().Equal(Copia);
    }

    [Theory]
    [InlineData("no-es-un-correo")]
    [InlineData("a@b")]
    [InlineData("Nombre <a@b.example>")]
    [InlineData("dos palabras@b.example")]
    [InlineData("a@b.example\r\nBcc: x@y.example")]
    [InlineData("\"a b\"@b.example")]
    [InlineData("a@[1.2.3.4]")]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_correo_con_formato_invalido_hace_fallar_el_envio(string correo)
    {
        DestinatariosExtraDeReclamacion.Resolver([correo], null, [Agenda]).Error.Codigo.Should().Be("Reclamacion.CorreoNoValido");
        DestinatariosExtraDeReclamacion.Resolver(null, [correo], [Agenda]).Error.Codigo.Should().Be("Reclamacion.CorreoNoValido");
    }

    [Fact]
    public void Un_correo_repetido_hace_fallar_el_envio_sea_cual_sea_la_lista_o_la_capitalizacion()
    {
        DestinatariosExtraDeReclamacion.Resolver([Manual, Manual.ToUpperInvariant()], null, [Agenda])
            .Error.Codigo.Should().Be("Reclamacion.CorreoRepetido", "dos veces el mismo manual");
        DestinatariosExtraDeReclamacion.Resolver([Manual], [Manual], [Agenda])
            .Error.Codigo.Should().Be("Reclamacion.CorreoRepetido", "el mismo en destinatarios y en copia");
        DestinatariosExtraDeReclamacion.Resolver([Agenda.ToUpperInvariant()], null, [Agenda])
            .Error.Codigo.Should().Be("Reclamacion.CorreoRepetido", "ya recibe el envío como contacto de la agenda");
        DestinatariosExtraDeReclamacion.Resolver(null, [Agenda], [Agenda])
            .Error.Codigo.Should().Be("Reclamacion.CorreoRepetido", "un contacto de la agenda tampoco va también en copia");
    }

    [Fact]
    public void Mas_de_diez_por_lista_hace_fallar_el_envio()
    {
        var once = Enumerable.Range(0, DestinatariosExtraDeReclamacion.MaximoPorLista + 1).Select(i => $"u{i}@e.example").ToList();

        DestinatariosExtraDeReclamacion.Resolver(once, null, [Agenda]).Error.Codigo.Should().Be("Reclamacion.DemasiadosDestinatarios");
    }

    [Fact]
    public void Si_el_registro_no_cabe_el_envio_falla_antes_de_enviar_nada()
    {
        // 10 correos largos (~60 caracteres) no caben en los 500 del registro; sin esta guarda el correo saldría y el guardado reventaría.
        var largos = Enumerable.Range(0, 10).Select(i => $"{new string('a', 40)}{i}@dominio-largo.example").ToList();

        DestinatariosExtraDeReclamacion.Resolver(largos, null, [Agenda]).Error.Codigo.Should().Be("Reclamacion.DemasiadosDestinatarios");
    }

    private static string Correo(string etiqueta, int longitud) =>
        etiqueta + new string('x', longitud - etiqueta.Length - "@d.example".Length) + "@d.example";

    [Fact]
    public void El_limite_del_registro_son_exactamente_500_caracteres_contando_el_sufijo_de_copia()
    {
        // Agenda (27) + 8 manuales de 50 + un último: cada uno suma 2 por el «; ». Con 55 el registro mide justo 500.
        List<string> Manuales(int largoDelUltimo) =>
            [.. Enumerable.Range(0, 8).Select(i => Correo($"m{i}", 50)), Correo("ultimo", largoDelUltimo)];

        var justo = DestinatariosExtraDeReclamacion.Resolver(Manuales(55), null, [Agenda]);
        var pasado = DestinatariosExtraDeReclamacion.Resolver(Manuales(56), null, [Agenda]);

        justo.EsExitoso.Should().BeTrue();
        DestinatariosExtraDeReclamacion.FormatoDeRegistro([Agenda, .. justo.Valor.Adicionales], []).Length.Should().Be(500);
        pasado.Error.Codigo.Should().Be("Reclamacion.DemasiadosDestinatarios");

        // El sufijo « (CC)» también cuenta: agenda + 8 manuales + uno de 40 miden 485; una copia de 11 caracteres sumaría 498 sin sufijo y 503 con él.
        var conCopia = DestinatariosExtraDeReclamacion.Resolver(Manuales(40), [Correo("c", 11)], [Agenda]);
        conCopia.Error.Codigo.Should().Be("Reclamacion.DemasiadosDestinatarios");
    }

    [Fact]
    public void El_registro_marca_la_copia()
    {
        DestinatariosExtraDeReclamacion.FormatoDeRegistro([Agenda, Manual], [Copia])
            .Should().Be($"{Agenda}; {Manual}; {Copia} (CC)");
    }

    // ---- El servicio de envío por SMTP ----

    private sealed record Entorno(
        RegistroEnvioReclamacionService Servicio,
        EmailQueCapturaYFalla Correo,
        ReclamacionesQueCapturan Repositorio,
        MediatorQueCaptura Mediator);

    private static Entorno Construir(bool conBuzonConectado = false, params string[] queFallan)
    {
        var integraciones = new IntegracionesQueryContextFalso();
        if (conBuzonConectado)
            integraciones.ConexionesLista.Add(new ConexionIntegracion("buzon@tenant.example", "Buzón genérico del tenant"));

        var correo = new EmailQueCapturaYFalla(queFallan);
        var repositorio = new ReclamacionesQueCapturan();
        var mediator = new MediatorQueCaptura();

        var servicio = new RegistroEnvioReclamacionService(
            integraciones, correo, repositorio, new CurrentUserServiceFalso(Guid.NewGuid()), new CorreoDelActorRealFalso(),
            mediator, NullLogger<RegistroEnvioReclamacionService>.Instance, new UnitOfWorkFalso());

        return new Entorno(servicio, correo, repositorio, mediator);
    }

    private static Task<Result> Enviar(Entorno entorno, IReadOnlyList<string> para, IReadOnlyList<string>? enCopia) =>
        entorno.Servicio.EnviarYRegistrarAsync(
            new TitularReclamacion(Guid.NewGuid(), "Refrielectric SL", AmbitoAplicacion.Empresa),
            [Guid.NewGuid()], para, "Documentación pendiente", "<p>Nos faltan los EPI.</p>", CancellationToken.None, enCopia: enCopia);

    [Fact]
    public async Task Por_SMTP_cada_destinatario_y_cada_copia_recibe_el_correo_y_el_registro_los_nombra_a_todos()
    {
        var entorno = Construir();

        var resultado = await Enviar(entorno, [Agenda, Manual], [Copia]);

        resultado.EsExitoso.Should().BeTrue();
        entorno.Correo.Enviados.Should().Equal([Agenda, Manual, Copia], "los de copia van después de los destinatarios");
        entorno.Correo.Cuerpos[0].Should().NotContain("Copia informativa", "un destinatario recibe el correo tal cual");
        entorno.Correo.Cuerpos[2].Should().StartWith("<p><em>Copia informativa de una reclamación enviada a ")
            .And.Contain($"{Agenda}, {Manual}", "quien está en copia sabe que lo es y a quién se le pidió")
            .And.EndWith("<p>Nos faltan los EPI.</p>");
        entorno.Repositorio.Agregadas.Should().ContainSingle()
            .Which.DestinatarioEmail.Should().Be($"{Agenda}; {Manual}; {Copia} (CC)");
    }

    [Fact]
    public async Task Por_SMTP_si_no_llega_a_ningun_destinatario_no_se_manda_ni_la_copia_ni_se_registra()
    {
        var entorno = Construir(queFallan: [Agenda, Manual]);

        var resultado = await Enviar(entorno, [Agenda, Manual], [Copia]);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.EnvioFallido");
        entorno.Correo.Enviados.Should().Equal(Agenda, Manual);
        entorno.Repositorio.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Por_SMTP_un_fallo_en_la_copia_es_un_envio_parcial_que_registra_lo_entregado()
    {
        var entorno = Construir(queFallan: [Copia]);

        var resultado = await Enviar(entorno, [Agenda, Manual], [Copia]);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(RegistroEnvioReclamacionService.CodigoEnvioParcial);
        resultado.Error.Mensaje.Should().Contain(Copia);
        entorno.Repositorio.Agregadas.Should().ContainSingle()
            .Which.DestinatarioEmail.Should().Be($"{Agenda}; {Manual}", "la copia no llegó, así que no consta como enviada");
    }

    [Fact]
    public async Task Sin_copia_el_registro_queda_como_siempre()
    {
        var entorno = Construir();

        await Enviar(entorno, [Agenda], null);

        entorno.Repositorio.Agregadas.Should().ContainSingle().Which.DestinatarioEmail.Should().Be(Agenda);
    }

    [Fact]
    public async Task Con_buzon_conectado_los_destinatarios_van_en_Para_y_la_copia_en_CC_de_un_solo_mensaje()
    {
        var entorno = Construir(conBuzonConectado: true);

        var resultado = await Enviar(entorno, [Agenda, Manual], [Copia]);

        resultado.EsExitoso.Should().BeTrue();
        entorno.Correo.Enviados.Should().BeEmpty("por el buzón conectado no se usa IEmailService");
        var mensaje = entorno.Mediator.Enviados.Should().ContainSingle().Which;
        mensaje.Destinatarios.Should().Equal(Agenda, Manual);
        mensaje.Copia.Should().Equal(Copia);
        entorno.Repositorio.Agregadas.Should().ContainSingle()
            .Which.DestinatarioEmail.Should().Be($"{Agenda}; {Manual}; {Copia} (CC)");
    }

    [Fact]
    public async Task Un_titular_Empresa_con_documentos_de_Trabajadores_se_registra_como_reclamacion_a_la_Empresa_empleadora()
    {
        // «Gestionar» (2026-10-18): el servicio real elige ParaEmpresaEmpleadora. Con ParaEmpresa la línea con Trabajador no
        // cabría (lo que falta a una Empresa es suyo, no de un Trabajador) y el envío reventaría tras salir el correo.
        var entorno = Construir();
        var empresaId = Guid.NewGuid();
        var trabajadorId = Guid.NewGuid();
        var tipoId = Guid.NewGuid();

        var resultado = await entorno.Servicio.EnviarYRegistrarAsync(
            new TitularReclamacion(empresaId, "Refrielectric SL", AmbitoAplicacion.Empresa, DocumentosDeTrabajadores: true),
            [], [Agenda], "Documentación pendiente", "<p>Falta la ficha de Ana.</p>", CancellationToken.None,
            [new DocumentoQueFaltaPedido(tipoId, trabajadorId)]);

        resultado.EsExitoso.Should().BeTrue();
        var reclamacion = entorno.Repositorio.Agregadas.Should().ContainSingle().Subject;
        reclamacion.EmpresaId.Should().Be(empresaId);
        reclamacion.ClienteId.Should().BeNull();
        reclamacion.Documentos.Should().ContainSingle(d => d.TrabajadorId == trabajadorId && d.TipoDocumentoId == tipoId);
    }

    // ---- Dobles ----

    private sealed class EmailQueCapturaYFalla(IReadOnlyCollection<string> queFallan) : IEmailService
    {
        public List<string> Enviados { get; } = [];
        public List<string> Cuerpos { get; } = [];

        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            Enviados.Add(destinatarioEmail);
            Cuerpos.Add(cuerpoHtml);
            return Task.FromResult(queFallan.Contains(destinatarioEmail)
                ? Result.Fallo(Error.Crear("Correo.EnvioFallido", "SMTP no disponible."))
                : Result.Exito());
        }
    }

    private sealed class ReclamacionesQueCapturan : IReclamacionDocumentalRepository
    {
        public List<ReclamacionDocumental> Agregadas { get; } = [];

        public void Agregar(ReclamacionDocumental reclamacion) => Agregadas.Add(reclamacion);
    }

    private sealed class CorreoDelActorRealFalso : ICorreoDelActorReal
    {
        public Task<string?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("marta@arcosspa.example");
    }

    private sealed class MediatorQueCaptura : IMediator
    {
        public List<EnviarMensajeNuevoCommand> Enviados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not EnviarMensajeNuevoCommand comando)
                throw new NotSupportedException($"El doble no cubre {request.GetType().Name}.");

            Enviados.Add(comando);
            return Task.FromResult((TResponse)(object)Result.Exito(Guid.NewGuid()));
        }

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

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
