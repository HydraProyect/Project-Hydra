using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa;
using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Options;
using FluentValidation;
using System.Globalization;
using Xunit;

namespace CaeManager.Application.Tests.AsistenteIa;

public class PreguntarAlAsistenteQueryHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private sealed class ActorFalso(Guid? usuarioId, Guid? simuladoId = null, TipoViaAcceso via = TipoViaAcceso.Normal) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(new ActorAuditoria(usuarioId, simuladoId, via, null));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => null;
    }

    private static PreguntarAlAsistenteQueryHandler CrearHandler(IAsistenteIaService servicio,
        IInstruccionTratamientoIaService instruccion, ITenantActual tenant, IMediator mediator) =>
        new(servicio, instruccion, tenant, mediator, new ActorFalso(Guid.NewGuid()),
            new LimitadorConsultaIa(Options.Create(new AsistenteConsultaOptions()), TimeProvider.System),
            Options.Create(new AsistenteConsultaOptions()));

    [Theory]
    [InlineData(TipoViaAcceso.SesionPrivilegiada)]
    [InlineData(TipoViaAcceso.Normal)]
    [InlineData(TipoViaAcceso.OperacionDelegada)]
    public async Task Solo_la_Sesion_Privilegiada_impide_consultar_el_modelo(TipoViaAcceso via)
    {
        var servicio = new AsistenteIaServiceFalso(Result.Exito("Respuesta"));
        var handler = new PreguntarAlAsistenteQueryHandler(
            servicio, new InstruccionTratamientoIaFalsa(true), new TenantActualFalso(TenantId), new CarteraFalsa(),
            new ActorFalso(Guid.NewGuid(), via: via),
            new LimitadorConsultaIa(Options.Create(new AsistenteConsultaOptions()), TimeProvider.System),
            Options.Create(new AsistenteConsultaOptions()));

        var resultado = await handler.Handle(new([new(RolMensajeChat.Usuario, "Pregunta")]), CancellationToken.None);

        if (via == TipoViaAcceso.SesionPrivilegiada)
        {
            servicio.HistorialRecibido.Should().BeNull();
            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be("TareaAsistente.SesionPrivilegiada");
            resultado.Error.Mensaje.Should().Be("El asistente no está disponible en una sesión de soporte.");
        }
        else
        {
            resultado.EsExitoso.Should().BeTrue();
            resultado.Valor.Should().Be("Respuesta");
            servicio.HistorialRecibido.Should().ContainSingle().Which.Texto.Should().Be("Pregunta");
        }
    }

    [Fact]
    public async Task Rechazar_por_Sesion_Privilegiada_no_consume_cuota_ni_instruccion_ni_cartera()
    {
        var escenario = new Escenario(new AsistenteConsultaOptions { MaxPreguntasUsuario = 1 });
        var usuario = Guid.NewGuid();

        var rechazada = await escenario.Preguntar(usuario, TenantId, via: TipoViaAcceso.SesionPrivilegiada);
        var normal = await escenario.Preguntar(usuario, TenantId);

        rechazada.Error.Codigo.Should().Be("TareaAsistente.SesionPrivilegiada");
        normal.EsExitoso.Should().BeTrue();
    }

    private sealed class RelojFalso : TimeProvider
    {
        private DateTimeOffset _ahora = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _ahora;
        public void Avanzar(TimeSpan tiempo) => _ahora += tiempo;
    }

    private sealed class Escenario(AsistenteConsultaOptions config)
    {
        public readonly RelojFalso Reloj = new();
        public readonly AsistenteIaServiceFalso Servicio = new(Result.Exito("Respuesta"));
        private LimitadorConsultaIa? _limitador;

        public bool InstruccionHabilitada { get; set; } = true;
        public TenantDeCarteraDto[]? CarteraSinInstruccion { get; set; }

        public PreguntarAlAsistenteQueryHandler Handler(Guid? usuario, Guid? tenant, Guid? simulado = null, TipoViaAcceso via = TipoViaAcceso.Normal) =>
            new(Servicio, new InstruccionTratamientoIaFalsa(InstruccionHabilitada), new TenantActualFalso(tenant),
                new CarteraFalsa(CarteraSinInstruccion), new ActorFalso(usuario, simulado, via),
                _limitador ??= new(Options.Create(config), Reloj), Options.Create(config));

        public Task<Result<string>> Preguntar(Guid? usuario, Guid? tenant, Guid? simulado = null, TipoViaAcceso via = TipoViaAcceso.Normal) =>
            Handler(usuario, tenant, simulado, via).Handle(new([new(RolMensajeChat.Usuario, "Pregunta")]), CancellationToken.None);
    }

    [Theory]
    [InlineData("es-ES", "La pregunta no puede superar los 8 caracteres.")]
    [InlineData("ca-ES", "La pregunta no pot superar els 8 caràcters.")]
    public async Task Pregunta_demasiado_larga_rechazada_por_el_validador_del_pipeline_sin_consumir_cuota(string cultura, string mensaje)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var config = new AsistenteConsultaOptions { MaxCaracteresPregunta = 8, MaxPreguntasUsuario = 1 };
            var escenario = new Escenario(config);
            var usuario = Guid.NewGuid();
            var pregunta = new PreguntarAlAsistenteQuery([new(RolMensajeChat.Usuario, "123456789")]);
            var validador = new PreguntarAlAsistenteQueryValidator(Options.Create(config));

            var validacion = await validador.ValidateAsync(pregunta);
            validacion.IsValid.Should().BeFalse();
            validacion.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(mensaje);

            // El mismo ValidationBehavior que registra AddApplication(), delante del handler.
            var pipeline = new ValidationBehavior<PreguntarAlAsistenteQuery, Result<string>>([validador]);
            var accion = () => pipeline.Handle(
                pregunta, _ => escenario.Handler(usuario, TenantId).Handle(pregunta, CancellationToken.None), CancellationToken.None);
            await accion.Should().ThrowAsync<ValidationException>();
            escenario.Servicio.HistorialRecibido.Should().BeNull();

            // Con cuota de una sola pregunta: la rechazada no la gastó.
            (await escenario.Preguntar(usuario, TenantId)).EsExitoso.Should().BeTrue();
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Theory]
    // El historial de 30 turnos empieza en Asistente (índice par) y termina en Usuario (índice 29).
    // Cada turno mide 100 caracteres. La ventana son turnos enteros y nunca empieza por el asistente.
    [InlineData(10, 40000, 9)] // 10 turnos empezarían en Asistente: se descarta ese.
    [InlineData(10, 350, 3)]   // la pregunta + dos turnos enteros; el cuarto (100 > 50) no cabe, y no se parte.
    [InlineData(3, 350, 3)]
    [InlineData(4, 40000, 3)]  // 4 turnos empezarían en Asistente.
    [InlineData(10, 250, 1)]   // pregunta + un turno de Asistente que quedaría inicial: solo la pregunta.
    [InlineData(10, 100, 1)]   // solo la pregunta actual, aunque agote el tope.
    [InlineData(1, 40000, 1)]
    public async Task De_treinta_turnos_solo_envia_turnos_enteros_recientes_sin_modificar_el_historial_visible(int turnos, int caracteres, int enviados)
    {
        var escenario = new Escenario(new() { MaxCaracteresPregunta = 100, MaxTurnosHistorial = turnos, MaxCaracteresHistorial = caracteres });
        var historial = Enumerable.Range(0, 30)
            .Select(i => new MensajeChatDto(i % 2 == 0 ? RolMensajeChat.Asistente : RolMensajeChat.Usuario, new string((char)('a' + i % 26), 100)))
            .ToList();

        var resultado = await escenario.Handler(Guid.NewGuid(), TenantId).Handle(new(historial), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var enviado = escenario.Servicio.HistorialRecibido!;
        enviado.Should().Equal(historial.TakeLast(enviados), "turnos enteros y contiguos hasta la pregunta actual, ninguno cortado");
        enviado[0].Rol.Should().Be(RolMensajeChat.Usuario);
        historial.Should().HaveCount(30).And.OnlyContain(m => m.Texto.Length == 100);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task El_primer_mensaje_enviado_es_siempre_del_usuario_con_cualquier_tope_y_longitud_de_historial(int turnos)
    {
        foreach (var longitud in Enumerable.Range(1, 25))
        {
            // Alterna Usuario/Asistente y termina en la pregunta del usuario: con longitud par empieza por
            // Asistente, con impar por Usuario (a partir de la sexta pregunta con 10 turnos, la ventana
            // cruda empezaría en Asistente y la API de Anthropic respondería 400).
            var historial = Enumerable.Range(0, longitud)
                .Select(i => new MensajeChatDto((longitud - 1 - i) % 2 == 0 ? RolMensajeChat.Usuario : RolMensajeChat.Asistente, $"turno {i}"))
                .ToList();
            var escenario = new Escenario(new() { MaxTurnosHistorial = turnos });

            var resultado = await escenario.Handler(Guid.NewGuid(), TenantId).Handle(new(historial), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue($"longitud {longitud}");
            var enviado = escenario.Servicio.HistorialRecibido!;
            enviado.Should().NotBeEmpty();
            enviado[0].Rol.Should().Be(RolMensajeChat.Usuario, $"longitud {longitud}, tope {turnos}");
            enviado[^1].Texto.Should().Be($"turno {longitud - 1}", "la pregunta actual nunca se pierde");
            enviado.Count.Should().BeLessThanOrEqualTo(turnos);
        }
    }

    [Fact]
    public async Task El_historial_enmascarado_descarta_turnos_antiguos_enteros_y_conserva_la_pregunta_actual()
    {
        var escenario = new Escenario(new() { MaxCaracteresPregunta = 100, MaxCaracteresHistorial = 100 });
        var texto = string.Concat(Enumerable.Repeat("a@b.es ", 14));
        var historial = new List<MensajeChatDto>
        {
            new(RolMensajeChat.Usuario, "Una pregunta anterior"),
            new(RolMensajeChat.Asistente, "Una respuesta anterior"),
            new(RolMensajeChat.Usuario, texto),
        };

        await escenario.Handler(Guid.NewGuid(), TenantId).Handle(new(historial), CancellationToken.None);

        // Enmascarada, la pregunta actual ya agota el tope: ningún turno anterior cabe y ninguno se parte.
        var enviado = escenario.Servicio.HistorialRecibido!;
        enviado.Should().ContainSingle().Which.Rol.Should().Be(RolMensajeChat.Usuario);
        enviado[0].Texto.Should().Contain("[CORREO_1]").And.NotContain("a@b.es");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_pregunta_rechazada_por_instruccion_o_cartera_no_consume_cuota(bool porCartera)
    {
        var escenario = new Escenario(new() { MaxPreguntasUsuario = 1, MaxPreguntasTenant = 1 });
        var usuario = Guid.NewGuid();
        if (porCartera)
            escenario.CarteraSinInstruccion = [new TenantDeCarteraDto(Guid.NewGuid(), "Tenant beneficiario sin instrucción", false)];
        else
            escenario.InstruccionHabilitada = false;

        var rechazada = await escenario.Preguntar(usuario, TenantId);

        rechazada.EsFallido.Should().BeTrue();
        rechazada.Error.Codigo.Should().Be(porCartera ? InstruccionIaCarteraDto.CodigoError : "AsistenteIa.SinInstruccion");
        escenario.Servicio.HistorialRecibido.Should().BeNull();

        escenario.InstruccionHabilitada = true;
        escenario.CarteraSinInstruccion = null;
        (await escenario.Preguntar(usuario, TenantId)).EsExitoso.Should().BeTrue("la rechazada no gastó la única pregunta de la cuota");
    }

    [Fact]
    public async Task Cuota_de_usuario_agotada_rechaza_incluso_si_cambia_el_usuario_simulado_o_el_Tenant_efectivo()
    {
        var escenario = new Escenario(new() { MaxPreguntasUsuario = 2 });
        var usuario = Guid.NewGuid();
        (await escenario.Preguntar(usuario, TenantId, Guid.NewGuid())).EsExitoso.Should().BeTrue();
        (await escenario.Preguntar(usuario, TenantId, Guid.NewGuid())).EsExitoso.Should().BeTrue();
        var ultimaLlamada = escenario.Servicio.HistorialRecibido;

        var resultado = await escenario.Preguntar(usuario, Guid.NewGuid(), Guid.NewGuid());

        resultado.Error.Codigo.Should().Be("AsistenteIa.CuotaUsuario");
        escenario.Servicio.HistorialRecibido.Should().BeSameAs(ultimaLlamada);
    }

    [Fact]
    public async Task Cuota_del_Tenant_efectivo_agotada_rechaza_otro_usuario_y_no_afecta_a_otro_Tenant_efectivo()
    {
        var escenario = new Escenario(new() { MaxPreguntasUsuario = 1, MaxPreguntasTenant = 2 });
        (await escenario.Preguntar(Guid.NewGuid(), TenantId)).EsExitoso.Should().BeTrue();
        (await escenario.Preguntar(Guid.NewGuid(), TenantId)).EsExitoso.Should().BeTrue();
        var usuario = Guid.NewGuid();
        var ultimaLlamada = escenario.Servicio.HistorialRecibido;

        var resultado = await escenario.Preguntar(usuario, TenantId);

        resultado.Error.Codigo.Should().Be("AsistenteIa.CuotaTenant");
        escenario.Servicio.HistorialRecibido.Should().BeSameAs(ultimaLlamada);
        (await escenario.Preguntar(usuario, Guid.NewGuid())).EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    public async Task La_cuota_se_recupera_al_caducar_cada_pregunta_en_la_ventana_deslizante(int horas)
    {
        var ventana = TimeSpan.FromHours(horas);
        var escenario = new Escenario(new() { MaxPreguntasUsuario = 2, MaxPreguntasTenant = 2, VentanaCuota = ventana });
        var usuario = Guid.NewGuid();
        (await escenario.Preguntar(usuario, TenantId)).EsExitoso.Should().BeTrue();
        escenario.Reloj.Avanzar(TimeSpan.FromMinutes(10));
        (await escenario.Preguntar(usuario, TenantId)).EsExitoso.Should().BeTrue();
        escenario.Reloj.Avanzar(ventana - TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        (await escenario.Preguntar(usuario, TenantId)).EsFallido.Should().BeTrue();
        escenario.Reloj.Avanzar(TimeSpan.FromTicks(1));

        (await escenario.Preguntar(usuario, TenantId)).EsExitoso.Should().BeTrue();
        (await escenario.Preguntar(usuario, TenantId)).EsFallido.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sin_usuario_real_o_Tenant_efectivo_falla_cerrado(bool faltaUsuario)
    {
        var escenario = new Escenario(new() { MaxPreguntasUsuario = 1, MaxPreguntasTenant = 1 });
        var usuario = Guid.NewGuid();

        var resultado = await escenario.Preguntar(faltaUsuario ? null : usuario, faltaUsuario ? TenantId : null);

        resultado.Error.Codigo.Should().Be("AsistenteIa.SinIdentidad");
        escenario.Servicio.HistorialRecibido.Should().BeNull();

        // El gate de identidad va antes de la cuota: no reserva nada, ni a un usuario ni a un Tenant.
        (await escenario.Preguntar(usuario, TenantId)).EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData("es-ES", "Has agotado tu cuota", "Esta organización ha agotado")]
    [InlineData("ca-ES", "Has esgotat la teva quota", "Aquesta organització ha esgotat")]
    public async Task Los_rechazos_de_cuota_estan_localizados(string cultura, string usuario, string tenant)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var escenario = new Escenario(new() { MaxPreguntasUsuario = 1, MaxPreguntasTenant = 1 });
            var id = Guid.NewGuid();
            await escenario.Preguntar(id, TenantId);

            (await escenario.Preguntar(id, TenantId)).Error.Mensaje.Should().StartWith(usuario);
            (await escenario.Preguntar(Guid.NewGuid(), TenantId)).Error.Mensaje.Should().StartWith(tenant);
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public async Task La_reserva_concurrente_no_supera_la_cuota_del_Tenant_efectivo()
    {
        var limitador = new LimitadorConsultaIa(Options.Create(new AsistenteConsultaOptions { MaxPreguntasTenant = 10 }), new RelojFalso());

        var resultados = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => limitador.IntentarReservar(Guid.NewGuid(), TenantId))));

        resultados.Count(r => r is null).Should().Be(10);
        resultados.Where(r => r is not null).Should().OnlyContain(r => r!.Codigo == "AsistenteIa.CuotaTenant");
    }

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
    public async Task Reenvia_el_historial_dentro_de_los_limites_y_devuelve_su_resultado_exitoso()
    {
        var historial = new List<MensajeChatDto> { new(RolMensajeChat.Usuario, "¿Cada cuánto se renueva un reconocimiento médico?") };
        var servicio = new AsistenteIaServiceFalso(Result.Exito("Depende del puesto de trabajo…"));
        var handler = CrearHandler(
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
        var handler = CrearHandler(
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
        var handler = CrearHandler(
            servicio, new InstruccionTratamientoIaFalsa(habilitada: true), new TenantActualFalso(TenantId), new CarteraFalsa());

        var resultado = await handler.Handle(new PreguntarAlAsistenteQuery([new(RolMensajeChat.Usuario, "Consulta de prueba")]), CancellationToken.None);

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
        var handler = CrearHandler(
            servicioQueNuncaDebeLlamarse, new InstruccionTratamientoIaFalsa(habilitada: false), new TenantActualFalso(TenantId), new CarteraFalsa());

        var resultado = await handler.Handle(new PreguntarAlAsistenteQuery([new(RolMensajeChat.Usuario, "Consulta de prueba")]), CancellationToken.None);

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
        var handler = CrearHandler(
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
