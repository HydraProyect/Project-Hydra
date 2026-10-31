using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>Umbral de ausencia y throttle de escritura del resumen (Project-Hydra-Negocio/tecnico/docs/blueprints/OPERATIONAL-HOME.md § 6, DDL-068).</summary>
public class ActividadUsuarioServiceTests
{
    private static readonly DateTime Ahora = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Sin_actividad_previa_no_hay_ausencia_pero_si_se_escribe()
    {
        var (ausente, debeEscribir) = ActividadUsuarioService.Evaluar(anterior: null, Ahora);

        ausente.Should().BeFalse();
        debeEscribir.Should().BeTrue();
    }

    [Fact]
    public void Menos_de_diez_minutos_no_es_ausencia()
    {
        var (ausente, _) = ActividadUsuarioService.Evaluar(Ahora.AddMinutes(-9), Ahora);

        ausente.Should().BeFalse();
    }

    [Fact]
    public void Mas_de_diez_minutos_es_ausencia()
    {
        var (ausente, _) = ActividadUsuarioService.Evaluar(Ahora.AddMinutes(-11), Ahora);

        ausente.Should().BeTrue();
    }

    [Fact]
    public void Dentro_del_minuto_de_throttle_no_se_reescribe()
    {
        var (_, debeEscribir) = ActividadUsuarioService.Evaluar(Ahora.AddSeconds(-30), Ahora);

        debeEscribir.Should().BeFalse();
    }

    [Fact]
    public void Pasado_el_minuto_de_throttle_se_reescribe_aunque_no_haya_ausencia()
    {
        var (ausente, debeEscribir) = ActividadUsuarioService.Evaluar(Ahora.AddMinutes(-2), Ahora);

        ausente.Should().BeFalse();
        debeEscribir.Should().BeTrue();
    }

    /// <summary>
    /// MainLayout e Inicio comparten este servicio —tiene ámbito de circuito— y
    /// los dos lo invocan en la misma carga. La bandera «ya resuelto» se ponía
    /// antes de resolver nada, así que la segunda llamada se la encontraba
    /// puesta y devolvía los campos todavía vacíos: concluía que no hubo
    /// ausencia y el resumen «qué llegó sin ver» no aparecía, aunque la primera
    /// llamada acabara determinando lo contrario.
    /// </summary>
    [Fact]
    public async Task Una_segunda_llamada_mientras_la_primera_resuelve_espera_su_respuesta_en_vez_de_inventarse_una()
    {
        var ultimaActividad = Ahora.AddMinutes(-30);
        var enVuelo = new TaskCompletionSource();
        var servicio = new ActividadUsuarioService(
            new UsuarioActualRetenido(Guid.NewGuid(), enVuelo.Task), new PreferenciasFalsas(ultimaActividad), NullLogger<ActividadUsuarioService>.Instance);

        var primera = servicio.RegistrarYEvaluarAsync(interactivo: true);
        var segunda = servicio.RegistrarYEvaluarAsync(interactivo: true);

        enVuelo.SetResult();

        (await primera).Should().Be((true, (DateTime?)ultimaActividad), "la primera resuelve la ausencia de verdad");
        (await segunda).Should().Be(await primera,
            "quien pregunta mientras la respuesta está en vuelo espera a esa respuesta, no a los campos sin rellenar");
    }

    /// <summary>Control positivo: resuelta ya una vez, la siguiente llamada reutiliza el resultado sin volver a la base.</summary>
    [Fact]
    public async Task Resuelta_una_vez_no_se_vuelve_a_preguntar()
    {
        var usuarioId = Guid.NewGuid();
        var preferencias = new PreferenciasFalsas(Ahora.AddMinutes(-30));
        var usuarioActual = new UsuarioActualRetenido(usuarioId, Task.CompletedTask);
        var servicio = new ActividadUsuarioService(usuarioActual, preferencias, NullLogger<ActividadUsuarioService>.Instance);

        var primera = await servicio.RegistrarYEvaluarAsync(interactivo: true);
        var segunda = await servicio.RegistrarYEvaluarAsync(interactivo: true);

        segunda.Should().Be(primera);
        usuarioActual.Preguntas.Should().Be(1, "«ausente» se evalúa una vez por circuito, no en cada navegación");
        preferencias.Escrituras.Should().Be(1);
        preferencias.Cuentas.Should().Equal([usuarioId, usuarioId], "lectura y escritura son de la cuenta de la sesión");
    }

    /// <summary>
    /// <c>UpdateAsync</c> devolvía el conflicto como resultado y nadie lo miraba. Un conflicto que sobrevive al
    /// reintento no quita la ausencia ya leída, pero deja constancia.
    /// </summary>
    [Theory]
    [InlineData(ResultadoEscrituraCuenta.Conflicto)]
    [InlineData(ResultadoEscrituraCuenta.CuentaNoVisible)]
    public async Task Una_actividad_que_no_llega_a_guardarse_deja_aviso_y_conserva_la_ausencia(ResultadoEscrituraCuenta fallo)
    {
        var ultimaActividad = DateTime.UtcNow.AddMinutes(-30);
        var preferencias = new PreferenciasFalsas(ultimaActividad) { ResultadoDeEscritura = fallo };
        var registro = new RegistroQueCuentaAvisos();
        var servicio = new ActividadUsuarioService(new UsuarioActualRetenido(Guid.NewGuid(), Task.CompletedTask), preferencias, registro);

        var resultado = await servicio.RegistrarYEvaluarAsync(interactivo: true);

        resultado.Should().Be((true, (DateTime?)ultimaActividad));
        preferencias.Escrituras.Should().Be(1);
        registro.Avisos.Should().Be(1, "descartar el resultado de la escritura era el defecto");
    }

    [Fact]
    public async Task Dentro_del_throttle_no_se_escribe_en_la_cuenta()
    {
        var preferencias = new PreferenciasFalsas(DateTime.UtcNow.AddSeconds(-30));
        var servicio = new ActividadUsuarioService(
            new UsuarioActualRetenido(Guid.NewGuid(), Task.CompletedTask), preferencias, NullLogger<ActividadUsuarioService>.Instance);

        var (ausente, _) = await servicio.RegistrarYEvaluarAsync(interactivo: true);

        ausente.Should().BeFalse();
        preferencias.Escrituras.Should().Be(0);
    }

    [Fact]
    public async Task Sin_cuenta_visible_no_hay_ausencia_ni_escritura()
    {
        var preferencias = new PreferenciasFalsas(Ahora) { Visible = false };
        var servicio = new ActividadUsuarioService(
            new UsuarioActualRetenido(Guid.NewGuid(), Task.CompletedTask), preferencias, NullLogger<ActividadUsuarioService>.Instance);

        (await servicio.RegistrarYEvaluarAsync(interactivo: true)).Should().Be((false, (DateTime?)null));
        preferencias.Escrituras.Should().Be(0);
    }

    /// <summary>
    /// Una escritura rechazada por la base no es un conflicto: bajo Sesión Privilegiada <c>cae_app_soporte</c> no
    /// tiene <c>UPDATE</c>, y el fallo sigue llegando a <c>MainLayout</c> como hasta ahora.
    /// </summary>
    [Fact]
    public async Task Un_rechazo_de_la_escritura_sube_y_la_siguiente_llamada_vuelve_a_intentarlo()
    {
        var preferencias = new PreferenciasFalsas(DateTime.UtcNow.AddMinutes(-30))
        {
            FalloDeEscritura = new InvalidOperationException("rechazada"),
        };
        var servicio = new ActividadUsuarioService(
            new UsuarioActualRetenido(Guid.NewGuid(), Task.CompletedTask), preferencias, NullLogger<ActividadUsuarioService>.Instance);

        var acto = () => servicio.RegistrarYEvaluarAsync(interactivo: true);

        await acto.Should().ThrowAsync<InvalidOperationException>();
        await acto.Should().ThrowAsync<InvalidOperationException>();
        preferencias.Escrituras.Should().Be(2);
    }

    /// <summary>Devuelve el id del usuario, pero solo cuando la tarea retenida se suelta.</summary>
    private sealed class UsuarioActualRetenido(Guid id, Task retencion) : ICurrentUserService
    {
        public int Preguntas { get; private set; }

        public async Task<Guid?> ObtenerUsuarioActualIdAsync()
        {
            Preguntas++;
            await retencion;
            return id;
        }

        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => throw new NotSupportedException();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => throw new NotSupportedException();
        public Task<bool> TieneDobleFactorActivoAsync() => throw new NotSupportedException();
    }

    /// <summary>Solo lo que este servicio toca: leer la cuenta y registrar la actividad.</summary>
    private sealed class PreferenciasFalsas(DateTime? ultimaActividad) : IPreferenciasCuentaPropia
    {
        public bool Visible { get; init; } = true;
        public ResultadoEscrituraCuenta ResultadoDeEscritura { get; init; } = ResultadoEscrituraCuenta.Guardada;
        public Exception? FalloDeEscritura { get; init; }
        public int Escrituras { get; private set; }
        public List<Guid> Cuentas { get; } = [];

        public Task<PreferenciasDeCuenta?> ObtenerAsync(Guid usuarioId, CancellationToken ct = default)
        {
            Cuentas.Add(usuarioId);
            return Task.FromResult(Visible ? new PreferenciasDeCuenta(TemaPreferido.Sistema, IdiomaPreferido.Espanol, ultimaActividad) : null);
        }

        public Task<ResultadoEscrituraCuenta> RegistrarActividadAsync(Guid usuarioId, DateTime instanteUtc, CancellationToken ct = default)
        {
            Escrituras++;
            Cuentas.Add(usuarioId);
            return FalloDeEscritura is null ? Task.FromResult(ResultadoDeEscritura) : Task.FromException<ResultadoEscrituraCuenta>(FalloDeEscritura);
        }

        public Task<ResultadoEscrituraCuenta> GuardarTemaAsync(Guid usuarioId, TemaPreferido tema, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResultadoEscrituraCuenta> GuardarIdiomaAsync(Guid usuarioId, IdiomaPreferido idioma, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RegistroQueCuentaAvisos : ILogger<ActividadUsuarioService>
    {
        public int Avisos { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Avisos++;
        }
    }
}
