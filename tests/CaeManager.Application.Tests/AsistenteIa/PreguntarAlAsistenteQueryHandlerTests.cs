using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.AsistenteIa;

public class PreguntarAlAsistenteQueryHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private sealed class AsistenteIaServiceFalso(Result<string> resultado) : IAsistenteIaService
    {
        public IReadOnlyList<MensajeChatDto>? HistorialRecibido { get; private set; }

        public Task<Result<string>> PreguntarAsync(IReadOnlyList<MensajeChatDto> historial, CancellationToken cancellationToken)
        {
            HistorialRecibido = historial;
            return Task.FromResult(resultado);
        }
    }

    /// <summary>Nivel 0 (DEC-33/REC-035) — el gate en sí tiene su propia suite (InstruccionTratamientoIaGateTests).</summary>
    private sealed class InstruccionTratamientoIaFalsa(bool habilitada) : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(habilitada);
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    [Fact]
    public async Task Reenvia_el_historial_completo_al_servicio_y_devuelve_su_resultado_exitoso()
    {
        var historial = new List<MensajeChatDto> { new(RolMensajeChat.Usuario, "¿Cada cuánto se renueva un reconocimiento médico?") };
        var servicio = new AsistenteIaServiceFalso(Result.Exito("Depende del puesto de trabajo…"));
        var handler = new PreguntarAlAsistenteQueryHandler(
            servicio, new InstruccionTratamientoIaFalsa(habilitada: true), new TenantActualFalso(TenantId), new CarteraFalsa());

        var resultado = await handler.Handle(new PreguntarAlAsistenteQuery(historial), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be("Depende del puesto de trabajo…");
        servicio.HistorialRecibido.Should().BeEquivalentTo(historial);
    }

    [Fact]
    public async Task Los_documentos_y_correos_no_salen_hacia_el_proveedor_y_el_mismo_documento_lleva_el_mismo_marcador()
    {
        var historial = new List<MensajeChatDto>
        {
            new(RolMensajeChat.Usuario, "¿Qué formación necesita el trabajador 12345678Z (ana.ruiz@ejemplo.es)?"),
            new(RolMensajeChat.Asistente, "Depende del puesto."),
            // Otro documento primero: turno a turno sería [DOC_1] y se confundiría con el del primer turno.
            new(RolMensajeChat.Usuario, "¿Y X1234567L, de la contrata B12345674, igual que 12345678Z?"),
        };
        var servicio = new AsistenteIaServiceFalso(Result.Exito("[DOC_2] necesita la misma formación que [DOC_1]."));
        var handler = new PreguntarAlAsistenteQueryHandler(
            servicio, new InstruccionTratamientoIaFalsa(habilitada: true), new TenantActualFalso(TenantId), new CarteraFalsa());

        var resultado = await handler.Handle(new PreguntarAlAsistenteQuery(historial), CancellationToken.None);

        var enviado = servicio.HistorialRecibido!;
        enviado.Should().HaveCount(3);
        enviado.Select(m => m.Rol).Should().Equal(RolMensajeChat.Usuario, RolMensajeChat.Asistente, RolMensajeChat.Usuario);
        string.Concat(enviado.Select(m => m.Texto)).Should()
            .NotContain("12345678Z").And.NotContain("ana.ruiz@ejemplo.es").And.NotContain("X1234567L").And.NotContain("B12345674");
        enviado[0].Texto.Should().Contain("[DOC_1]").And.Contain("[CORREO_1]");
        enviado[1].Texto.Should().Be("Depende del puesto.");
        enviado[2].Texto.Should().StartWith("¿Y [DOC_2], de la contrata [CIF_1]").And.EndWith("igual que [DOC_1]?");

        // Lo que vuelve se restaura: la persona ve sus identificadores.
        resultado.Valor.Should().Be("X1234567L necesita la misma formación que 12345678Z.");
    }

    [Fact]
    public async Task Propaga_un_resultado_fallido_del_servicio_sin_modificarlo()
    {
        var error = Error.Crear("AsistenteIa.NoConfigurado", "El asistente no está disponible ahora mismo.");
        var servicio = new AsistenteIaServiceFalso(Result.Fallo<string>(error));
        var handler = new PreguntarAlAsistenteQueryHandler(
            servicio, new InstruccionTratamientoIaFalsa(habilitada: true), new TenantActualFalso(TenantId), new CarteraFalsa());

        var resultado = await handler.Handle(new PreguntarAlAsistenteQuery([]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(error);
    }

    [Fact]
    public async Task Sin_instruccion_vigente_no_llama_al_servicio_y_falla()
    {
        // Control positivo de este test: el servicio lanza si se invoca —
        // si el gate fallara, este test fallaría con la excepción del
        // servicio, no con una aserción confusa sobre el mensaje de error.
        var servicioQueNuncaDebeLlamarse = new AsistenteIaServiceLanzaSiSeInvoca();
        var handler = new PreguntarAlAsistenteQueryHandler(
            servicioQueNuncaDebeLlamarse, new InstruccionTratamientoIaFalsa(habilitada: false), new TenantActualFalso(TenantId), new CarteraFalsa());

        var resultado = await handler.Handle(new PreguntarAlAsistenteQuery([]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AsistenteIa.SinInstruccion");
        resultado.Error.Mensaje.Should().StartWith("Esta organización todavía no tiene").And.NotContainEquivalentOf("tenant");
    }

    [Fact]
    public async Task Con_un_Tenant_de_la_cartera_sin_instruccion_no_llama_al_servicio_y_dice_cual_falta()
    {
        // La pantalla tiene instrucción, pero el Gestor CAE puede escribir el nombre
        // de una persona de otro Tenant de su cartera: falla cerrado.
        var otro = new TenantDeCarteraDto(Guid.NewGuid(), "Tenant beneficiario sin instrucción", false);
        var handler = new PreguntarAlAsistenteQueryHandler(
            new AsistenteIaServiceLanzaSiSeInvoca(), new InstruccionTratamientoIaFalsa(habilitada: true),
            new TenantActualFalso(TenantId), new CarteraFalsa(sin: [otro]));

        var resultado = await handler.Handle(
            new PreguntarAlAsistenteQuery([new(RolMensajeChat.Usuario, "¿Tiene Ana Ruiz el reconocimiento médico al día?")]),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(InstruccionIaCarteraDto.CodigoError);
        resultado.Error.Mensaje.Should().Contain("Tenant beneficiario sin instrucción");
    }

    /// <summary>Devuelve la comprobación de la cartera; la Query en sí tiene su propia suite.</summary>
    private sealed class CarteraFalsa(TenantDeCarteraDto[]? sin = null) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ComprobarInstruccionIaCarteraQuery
                ? Task.FromResult((TResponse)(object)new InstruccionIaCarteraDto([], sin ?? []))
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AsistenteIaServiceLanzaSiSeInvoca : IAsistenteIaService
    {
        public Task<Result<string>> PreguntarAsync(IReadOnlyList<MensajeChatDto> historial, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("El gate de Nivel 0 (instrucción de tratamiento IA) debía impedir esta llamada.");
    }
}
