using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
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
/// Z5-4 de la revisión de seguridad del 2026-10-23: el correo de la reclamación salía antes de guardar nada, así que
/// un fallo del guardado posterior (la base cae, el Gestor CAE cierra la pestaña) dejaba el correo entregado y sin
/// registro, y el reintento lo volvía a mandar a la Empresa contraparte.
///
/// <para>
/// Contrato: antes del primer correo queda guardado un registro previo del envío (<see cref="EnvioReclamacion"/>);
/// cada entrega se guarda en cuanto sale, sin el token de la pantalla; la reclamación se registra al final. Reintentar
/// la misma reclamación tras un fallo de guardado no vuelve a mandar el correo a quien ya consta que lo recibió, y un
/// fallo del correo nunca la deja registrada.
/// </para>
///
/// El doble <see cref="BaseFalsa"/> separa lo rastreado de lo guardado, como el contexto real: lo que un guardado
/// fallido no llegó a escribir desaparece al descartarse, y el reintento lee solo lo que quedó guardado.
/// </summary>
public class EnvioIdempotenteDeLaReclamacionTests
{
    private const string Contacto = "contacto@refrielectric.example";
    private const string Prl = "prl@refrielectric.example";

    private static readonly Guid TitularId = Guid.NewGuid();
    private static readonly Guid DocumentoId = Guid.NewGuid();

    private sealed class Entorno
    {
        public required RegistroEnvioReclamacionService Servicio { get; init; }
        public required BaseFalsa Base { get; init; }
        public required CorreoFalso Correo { get; init; }
        public required MediadorFalso Mediador { get; init; }
        public required List<string> Secuencia { get; init; }
    }

    private static Entorno Construir(Guid? usuarioId = null, bool conBuzon = false)
    {
        var secuencia = new List<string>();
        var baseFalsa = new BaseFalsa(secuencia);
        var correo = new CorreoFalso(secuencia);
        var mediador = new MediadorFalso(secuencia);
        var integraciones = new IntegracionesQueryContextFalso();
        if (conBuzon)
            integraciones.ConexionesLista.Add(new ConexionIntegracion("buzon@tenant.example", "Buzón genérico del tenant"));

        var servicio = new RegistroEnvioReclamacionService(
            integraciones, correo, baseFalsa, new CurrentUserServiceFalso(usuarioId ?? UsuarioPorDefecto),
            new CorreoDelActorRealFalso(), mediador, NullLogger<RegistroEnvioReclamacionService>.Instance,
            baseFalsa, baseFalsa, baseFalsa);

        return new Entorno { Servicio = servicio, Base = baseFalsa, Correo = correo, Mediador = mediador, Secuencia = secuencia };
    }

    private static readonly Guid UsuarioPorDefecto = Guid.NewGuid();

    private static Task<Result> Enviar(Entorno entorno, string[] destinatarios, CancellationToken token = default, Guid? documentoId = null) =>
        entorno.Servicio.EnviarYRegistrarAsync(
            new TitularReclamacion(TitularId, "Refrielectric SL", AmbitoAplicacion.Empresa),
            [documentoId ?? DocumentoId],
            destinatarios,
            "Documentación pendiente",
            "<p>Nos faltan los EPI.</p>",
            token);

    // ---------------------------------------------------------------- orden

    [Fact]
    public async Task El_registro_previo_se_guarda_antes_del_primer_correo_y_la_reclamacion_despues_del_ultimo()
    {
        var entorno = Construir();

        var resultado = await Enviar(entorno, [Contacto, Prl]);

        resultado.EsExitoso.Should().BeTrue();
        entorno.Secuencia.Should().Equal(
            ["guardado(envio)", $"correo({Contacto})", "guardado(envio)", $"correo({Prl})", "guardado(envio)", "guardado(envio+reclamacion)"],
            "sin el registro previo guardado no sale ningún correo, cada entrega se guarda antes del siguiente correo y la reclamación va al final");
        entorno.Base.ReclamacionesGuardadas.Should().ContainSingle().Which.DestinatarioEmail.Should().Be($"{Contacto}; {Prl}");
    }

    [Fact]
    public async Task Si_no_se_puede_guardar_el_registro_previo_no_sale_ningun_correo()
    {
        var entorno = Construir();
        entorno.Base.FallarEnGuardado.Add(1);

        var envio = () => Enviar(entorno, [Contacto]);

        await envio.Should().ThrowAsync<InvalidOperationException>();
        entorno.Correo.Entregados.Should().BeEmpty("el correo a un tercero no se deshace: sin registro previo no puede salir");
        entorno.Base.VecesDescartado.Should().Be(1, "el registro añadido no puede quedarse en el contexto del circuito para el siguiente comando");
    }

    [Fact]
    public async Task Sin_usuario_no_sale_ningun_correo_ni_se_guarda_nada()
    {
        var entorno = Construir();
        var sinUsuario = new RegistroEnvioReclamacionService(
            new IntegracionesQueryContextFalso(), entorno.Correo, entorno.Base, new CurrentUserServiceFalso(),
            new CorreoDelActorRealFalso(), entorno.Mediador, NullLogger<RegistroEnvioReclamacionService>.Instance,
            entorno.Base, entorno.Base, entorno.Base);

        var resultado = await sinUsuario.EnviarYRegistrarAsync(
            new TitularReclamacion(TitularId, "Refrielectric SL", AmbitoAplicacion.Empresa), [DocumentoId], [Contacto],
            "Asunto", "<p>Cuerpo</p>", CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Reclamacion.SinUsuario");
        entorno.Correo.Entregados.Should().BeEmpty("antes se comprobaba después de enviar: el correo salía sin poder registrarse");
        entorno.Secuencia.Should().BeEmpty();
    }

    // ------------------------------------------------- fallo de guardado tras enviar

    [Fact]
    public async Task Si_falla_el_guardado_de_la_reclamacion_el_reintento_la_registra_sin_volver_a_mandar_el_correo()
    {
        var entorno = Construir();
        entorno.Base.FallarEnGuardado.Add(3); // 1 registro previo, 2 entrega a Contacto, 3 reclamación

        var primero = await Enviar(entorno, [Contacto]);

        primero.Error.Codigo.Should().Be(RegistroEnvioReclamacionService.CodigoRegistroFallido,
            "la pantalla tiene que decir que el correo salió, no un error genérico ni una excepción");
        entorno.Base.ReclamacionesGuardadas.Should().BeEmpty();
        entorno.Base.VecesDescartado.Should().Be(1);

        var reintento = await Enviar(entorno, [Contacto]);

        reintento.EsExitoso.Should().BeTrue();
        entorno.Correo.Entregados.Should().Equal([Contacto], "el reintento no vuelve a mandar el correo a quien ya lo recibió");
        entorno.Base.ReclamacionesGuardadas.Should().ContainSingle().Which.DestinatarioEmail.Should().Be(Contacto);
        entorno.Base.EnviosGuardados.Should().ContainSingle("el reintento reanuda el mismo registro previo, no abre otro")
            .Which.ReclamacionDocumentalId.Should().Be(entorno.Base.ReclamacionesGuardadas[0].Id);
    }

    [Fact]
    public async Task Si_falla_el_guardado_de_una_entrega_el_reintento_no_repite_las_anteriores()
    {
        var entorno = Construir();
        entorno.Base.FallarEnGuardado.Add(3); // 1 previo, 2 entrega a Contacto, 3 entrega a Prl

        var primero = await Enviar(entorno, [Contacto, Prl]);

        primero.Error.Codigo.Should().Be(RegistroEnvioReclamacionService.CodigoRegistroFallido);
        entorno.Correo.Entregados.Should().Equal([Contacto, Prl], "el segundo correo salió; lo que no se guardó fue su entrega");

        var reintento = await Enviar(entorno, [Contacto, Prl]);

        reintento.EsExitoso.Should().BeTrue();
        entorno.Correo.Entregados.Should().Equal([Contacto, Prl, Prl],
            "a Contacto no se le repite (su entrega quedó guardada); a Prl sí, porque la suya no llegó a constar");
        entorno.Base.ReclamacionesGuardadas.Should().ContainSingle().Which.DestinatarioEmail.Should().Be($"{Contacto}; {Prl}");
    }

    [Fact]
    public async Task Si_la_pantalla_se_retira_tras_el_primer_correo_el_envio_y_su_registro_terminan()
    {
        // El token de la pestaña (ReclamacionesTab._ciclo) se cancela al retirarla. Antes cortaba el guardado con el
        // correo ya entregado: no quedaba registro y el reintento duplicaba el correo.
        using var pantalla = new CancellationTokenSource();
        var entorno = Construir();
        entorno.Correo.AlEntregar = _ => pantalla.Cancel();

        var resultado = await Enviar(entorno, [Contacto, Prl], pantalla.Token);

        resultado.EsExitoso.Should().BeTrue();
        entorno.Correo.Entregados.Should().Equal([Contacto, Prl]);
        entorno.Base.ReclamacionesGuardadas.Should().ContainSingle();
        entorno.Base.TokensDeGuardado.Skip(1).Should().OnlyContain(t => !t.CanBeCanceled,
            "ningún guardado posterior al primer correo depende del token de la pantalla");
        entorno.Correo.Tokens.Should().OnlyContain(t => !t.CanBeCanceled, "un correo a medio mandar no se corta: podría haber salido sin constar");
    }

    // ---------------------------------------------------------------- fallo de envío

    [Fact]
    public async Task Si_el_SMTP_falla_no_queda_registrada_y_el_reintento_la_manda_de_verdad()
    {
        var entorno = Construir();
        entorno.Correo.QueFallan.Add(Contacto);

        var primero = await Enviar(entorno, [Contacto]);

        primero.Error.Codigo.Should().Be("Reclamacion.EnvioFallido");
        entorno.Base.ReclamacionesGuardadas.Should().BeEmpty("un fallo del correo no deja la reclamación registrada como enviada");
        entorno.Base.EnviosGuardados.Should().ContainSingle().Which.Entregados.Should().BeEmpty();

        entorno.Correo.QueFallan.Clear();
        var reintento = await Enviar(entorno, [Contacto]);

        reintento.EsExitoso.Should().BeTrue();
        entorno.Correo.Entregados.Should().Equal([Contacto], "lo que no salió la primera vez sí sale en el reintento");
        entorno.Base.ReclamacionesGuardadas.Should().ContainSingle().Which.DestinatarioEmail.Should().Be(Contacto);
    }

    [Fact]
    public async Task Tras_un_envio_parcial_el_reintento_solo_manda_a_quien_le_fallo()
    {
        var entorno = Construir();
        entorno.Correo.QueFallan.Add(Prl);
        entorno.Base.FallarEnGuardado.Add(3); // 1 previo, 2 entrega a Contacto, 3 reclamación (parcial)

        await Enviar(entorno, [Contacto, Prl]);
        entorno.Correo.QueFallan.Clear();
        var reintento = await Enviar(entorno, [Contacto, Prl]);

        reintento.EsExitoso.Should().BeTrue();
        entorno.Correo.Entregados.Should().Equal([Contacto, Prl]);
        entorno.Base.ReclamacionesGuardadas.Should().ContainSingle().Which.DestinatarioEmail.Should().Be($"{Contacto}; {Prl}");
    }

    // ---------------------------------------------------------------- límites de la idempotencia

    [Fact]
    public async Task Una_reclamacion_ya_registrada_que_se_vuelve_a_enviar_sale_otra_vez()
    {
        // «Reclamar de nuevo» es una reclamación nueva: solo se reanuda un envío pendiente, nunca uno ya registrado.
        var entorno = Construir();

        await Enviar(entorno, [Contacto]);
        var otra = await Enviar(entorno, [Contacto]);

        otra.EsExitoso.Should().BeTrue();
        entorno.Correo.Entregados.Should().Equal([Contacto, Contacto]);
        entorno.Base.ReclamacionesGuardadas.Should().HaveCount(2);
    }

    [Fact]
    public async Task Otra_reclamacion_distinta_no_reanuda_el_envio_pendiente_de_la_primera()
    {
        var entorno = Construir();
        entorno.Base.FallarEnGuardado.Add(3);

        await Enviar(entorno, [Contacto]);
        var otra = await Enviar(entorno, [Contacto], documentoId: Guid.NewGuid());

        otra.EsExitoso.Should().BeTrue();
        entorno.Correo.Entregados.Should().Equal([Contacto, Contacto], "son documentos distintos: es otro correo");
        entorno.Base.EnviosGuardados.Should().HaveCount(2);
    }

    [Fact]
    public async Task El_envio_pendiente_de_otro_usuario_no_se_reanuda()
    {
        var entorno = Construir();
        entorno.Base.FallarEnGuardado.Add(3);
        await Enviar(entorno, [Contacto]);

        var otroUsuario = new RegistroEnvioReclamacionService(
            new IntegracionesQueryContextFalso(), entorno.Correo, entorno.Base, new CurrentUserServiceFalso(Guid.NewGuid()),
            new CorreoDelActorRealFalso(), entorno.Mediador, NullLogger<RegistroEnvioReclamacionService>.Instance,
            entorno.Base, entorno.Base, entorno.Base);
        await otroUsuario.EnviarYRegistrarAsync(
            new TitularReclamacion(TitularId, "Refrielectric SL", AmbitoAplicacion.Empresa), [DocumentoId], [Contacto],
            "Documentación pendiente", "<p>Nos faltan los EPI.</p>", CancellationToken.None);

        entorno.Correo.Entregados.Should().Equal([Contacto, Contacto]);
    }

    [Fact]
    public void La_huella_no_depende_del_orden_ni_de_las_mayusculas_y_si_de_lo_que_se_reclama()
    {
        var otroDocumento = Guid.NewGuid();
        string Huella(Guid[] documentos, string[] destinatarios, string[]? copia = null) =>
            EnvioReclamacion.CalcularHuella(TitularId, AmbitoAplicacion.Empresa, false, documentos, [], destinatarios, copia ?? []);

        Huella([DocumentoId, otroDocumento], [Contacto, Prl])
            .Should().Be(Huella([otroDocumento, DocumentoId], [Prl.ToUpperInvariant(), Contacto]));
        Huella([DocumentoId], [Contacto]).Should().NotBe(Huella([DocumentoId], [Prl]));
        Huella([DocumentoId], [Contacto]).Should().NotBe(Huella([DocumentoId], [Contacto], [Prl]), "la copia también forma parte del correo");
        Huella([DocumentoId], [Contacto]).Should().NotBe(
            EnvioReclamacion.CalcularHuella(TitularId, AmbitoAplicacion.Empresa, true, [DocumentoId], [], [Contacto], []),
            "a la Empresa empleadora se le reclama otra cosa");
    }

    // ---------------------------------------------------------------- buzón conectado

    [Fact]
    public async Task Por_buzon_conectado_el_reintento_tras_un_fallo_de_guardado_no_vuelve_a_mandar_el_mensaje()
    {
        using var pantalla = new CancellationTokenSource();
        var entorno = Construir(conBuzon: true);
        entorno.Base.FallarEnGuardado.Add(3); // 1 previo, 2 hilo apuntado, 3 reclamación

        var primero = await Enviar(entorno, [Contacto], pantalla.Token);
        var reintento = await Enviar(entorno, [Contacto]);

        primero.Error.Codigo.Should().Be(RegistroEnvioReclamacionService.CodigoRegistroFallido);
        reintento.EsExitoso.Should().BeTrue();
        entorno.Mediador.Mensajes.Should().ContainSingle("el buzón ya lo mandó: el reintento solo lo registra");
        entorno.Base.ReclamacionesGuardadas.Should().ContainSingle()
            .Which.ConversacionId.Should().Be(entorno.Mediador.UltimaConversacion);
        entorno.Mediador.TokenDelMensaje.CanBeCanceled.Should().BeFalse();
    }

    // ================================================================ dobles

    /// <summary>
    /// Contexto de persistencia falso: lo agregado o modificado se escribe en «lo guardado» solo si el guardado no
    /// falla; descartar suelta lo rastreado, y lo que se vuelve a leer sale de lo guardado, como tras ChangeTracker.Clear.
    /// </summary>
    private sealed class BaseFalsa(List<string> secuencia)
        : IUnitOfWork, IDescarteCambiosPendientes, IEnvioReclamacionRepository, IReclamacionDocumentalRepository
    {
        private readonly List<EnvioReclamacion> _enviosRastreados = [];
        private readonly List<ReclamacionDocumental> _reclamacionesPorGuardar = [];
        private readonly Dictionary<Guid, EnvioReclamacion> _enviosGuardados = [];
        private int _guardados;

        public HashSet<int> FallarEnGuardado { get; } = [];
        public List<CancellationToken> TokensDeGuardado { get; } = [];
        public List<ReclamacionDocumental> ReclamacionesGuardadas { get; } = [];
        public IReadOnlyCollection<EnvioReclamacion> EnviosGuardados => _enviosGuardados.Values;
        public int VecesDescartado { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _guardados++;
            TokensDeGuardado.Add(cancellationToken);
            if (FallarEnGuardado.Contains(_guardados))
                throw new InvalidOperationException("La base no responde.");

            secuencia.Add(_reclamacionesPorGuardar.Count > 0 ? "guardado(envio+reclamacion)" : "guardado(envio)");
            foreach (var envio in _enviosRastreados)
                _enviosGuardados[envio.Id] = Copiar(envio);
            ReclamacionesGuardadas.AddRange(_reclamacionesPorGuardar);
            _reclamacionesPorGuardar.Clear();
            return Task.FromResult(1);
        }

        public void DescartarCambiosPendientes()
        {
            VecesDescartado++;
            _enviosRastreados.Clear();
            _reclamacionesPorGuardar.Clear();
        }

        public Task<EnvioReclamacion?> ObtenerPendienteAsync(
            Guid usuarioId, string huella, DateTime iniciadoDesdeUtc, CancellationToken cancellationToken)
        {
            var guardado = _enviosGuardados.Values
                .Where(e => e.UsuarioId == usuarioId && e.Huella == huella && !e.EstaRegistrado && e.IniciadoEnUtc >= iniciadoDesdeUtc)
                .OrderByDescending(e => e.IniciadoEnUtc)
                .FirstOrDefault();
            if (guardado is null)
                return Task.FromResult<EnvioReclamacion?>(null);

            // Resolución de identidad: si sigue rastreado, es esa instancia; si no, una lectura nueva de lo guardado.
            var rastreado = _enviosRastreados.FirstOrDefault(e => e.Id == guardado.Id);
            if (rastreado is not null)
                return Task.FromResult<EnvioReclamacion?>(rastreado);

            var leido = Copiar(guardado);
            _enviosRastreados.Add(leido);
            return Task.FromResult<EnvioReclamacion?>(leido);
        }

        public void Agregar(EnvioReclamacion envio) => _enviosRastreados.Add(envio);

        public void Agregar(ReclamacionDocumental reclamacion) => _reclamacionesPorGuardar.Add(reclamacion);

        private static EnvioReclamacion Copiar(EnvioReclamacion origen)
        {
            var copia = EnvioReclamacion.Iniciar(origen.UsuarioId, origen.Huella, origen.IniciadoEnUtc);
            Fijar(copia, nameof(Entity.Id), origen.Id);
            Fijar(copia, nameof(EnvioReclamacion.ConversacionId), origen.ConversacionId);
            Fijar(copia, nameof(EnvioReclamacion.ReclamacionDocumentalId), origen.ReclamacionDocumentalId);
            foreach (var d in origen.Entregados)
                copia.RegistrarEntrega(d, enCopia: false);
            foreach (var d in origen.EntregadosEnCopia)
                copia.RegistrarEntrega(d, enCopia: true);
            return copia;
        }

        private static void Fijar(EnvioReclamacion envio, string propiedad, object? valor)
        {
            var tipo = typeof(EnvioReclamacion);
            PropertyInfo? info = null;
            while (info?.SetMethod is null && tipo is not null)
            {
                info = tipo.GetProperty(propiedad, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                tipo = tipo.BaseType;
            }

            info!.SetValue(envio, valor);
        }
    }

    private sealed class CorreoFalso(List<string> secuencia) : IEmailService
    {
        public List<string> Entregados { get; } = [];
        public HashSet<string> QueFallan { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Action<string>? AlEntregar { get; set; }

        public Task<Result> EnviarAsync(
            string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            Tokens.Add(cancellationToken);
            if (QueFallan.Contains(destinatarioEmail))
                return Task.FromResult(Result.Fallo(Error.Crear("Correo.EnvioFallido", "SMTP no disponible.")));

            secuencia.Add($"correo({destinatarioEmail})");
            Entregados.Add(destinatarioEmail);
            AlEntregar?.Invoke(destinatarioEmail);
            return Task.FromResult(Result.Exito());
        }
    }

    private sealed class CorreoDelActorRealFalso : ICorreoDelActorReal
    {
        public Task<string?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("marta@operador.example");
    }

    private sealed class MediadorFalso(List<string> secuencia) : IMediator
    {
        public List<EnviarMensajeNuevoCommand> Mensajes { get; } = [];
        public Guid UltimaConversacion { get; private set; }
        public CancellationToken TokenDelMensaje { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not EnviarMensajeNuevoCommand mensaje)
                throw new NotSupportedException($"El doble no cubre {request.GetType().Name}.");

            secuencia.Add("mensaje");
            Mensajes.Add(mensaje);
            TokenDelMensaje = cancellationToken;
            UltimaConversacion = Guid.NewGuid();
            return Task.FromResult((TResponse)(object)Result.Exito(UltimaConversacion));
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
