using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Importacion;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// REC-108 (DEC-20): dos confirmaciones concurrentes de la MISMA operación de
/// importación (mismo <c>OperacionId</c>) no pueden materializar el mismo
/// documento dos veces — la deduplicación en memoria que ya tenía
/// EjecutarImportacionCommandHandler solo protegía contra reimportar en
/// SERIE, no contra dos circuitos confirmando a la vez el mismo plan. La
/// defensa ahora es el índice único de <c>(TenantId, OperacionId)</c> sobre
/// <see cref="Domain.Importacion.OperacionImportacion"/> — ver el comentario
/// de EjecutarImportacionCommand.cs para el diseño completo.
///
/// Mismo patrón que
/// <c>AutoConcederPrivilegioTests.Dos_arranques_simultaneos_dejan_exactamente_una_concesion_fundacional</c>:
/// <c>Barrier(2)</c> + <c>Task.Run</c> — un método async se ejecuta en el
/// hilo del llamante hasta su primer <c>await</c>, así que sin <c>Task.Run</c>
/// la primera invocación se queda esperando en <c>SignalAndWait()</c> a un
/// segundo participante que nunca llega a crearse.
/// </summary>
public class EjecutarImportacionConcurrenciaTests : IAsyncLifetime
{
    private const string Dni = "12345678Z";
    private const string NombreTipoDocumento = "Certificado de aptitud médica";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };
    private Guid _trabajadorId;
    private Guid _tipoDocumentoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var empresa = new Empresa("Empresa Concurrencia Importación S.L.");
        contexto.Empresas.Add(empresa);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Marta", "Ruiz", Dni);
        contexto.Trabajadores.Add(trabajador);
        var tipoDocumento = new TipoDocumento(NombreTipoDocumento, 12, aplicaVencimientoAutomatico: true, orden: 1, AmbitoAplicacion.Trabajador);
        contexto.TiposDocumento.Add(tipoDocumento);
        await contexto.SaveChangesAsync();

        _trabajadorId = trabajador.Id;
        _tipoDocumentoId = tipoDocumento.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Dos_confirmaciones_concurrentes_de_la_misma_operacion_dejan_un_solo_documento()
    {
        var plan = new PlanImportacionDto(
            OperacionId: Guid.NewGuid(),
            ClientesCentros: [], Empresas: [], Trabajadores: [],
            Documentos: [new DocumentoImportadoDto(Dni, NombreTipoDocumento, new DateOnly(2026, 2, 1), YaExiste: false)],
            Asignaciones: [], Advertencias: [], Omitidos: []);

        var barrera = new Barrier(2);

        Task<ResultadoImportacionDto> ConfirmarAsync() => Task.Run(async () =>
        {
            barrera.SignalAndWait();
            await using var contexto = CrearContexto();
            var resultado = await ConstruirHandler(contexto).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None);
            return resultado.Valor;
        });

        var resultados = await Task.WhenAll(ConfirmarAsync(), ConfirmarAsync());

        resultados.Sum(r => r.DocumentosCreados).Should().Be(1,
            "el plan solo pedía un documento — solo una de las dos confirmaciones puede haberlo creado");
        resultados.Count(r => r.DocumentosCreados == 0).Should().Be(1,
            "la perdedora de la carrera vuelve con un resultado idempotente, no una excepción cruda");

        await using var verificacion = CrearContexto();
        (await verificacion.Documentos.CountAsync(d => d.TrabajadorId == _trabajadorId && d.TipoDocumentoId == _tipoDocumentoId))
            .Should().Be(1, "confirmar la misma operación dos veces en paralelo no puede dejar dos documentos donde el plan pedía uno");
    }

    /// <summary>
    /// Blazor Server comparte un único <c>DbContext</c> por circuito
    /// (Importacion.razor.cs / PuertaAccesoDatos): tras confirmar, el wizard
    /// dispara <c>RegistrarHistorialImportacionCommand</c> sobre el MISMO
    /// contexto. Si el perdedor de la carrera de REC-108 dejara su plan a medio
    /// construir en el <c>ChangeTracker</c> (un <c>SaveChangesAsync</c> fallido
    /// NO revierte el estado <c>Added</c> en memoria, solo la transacción), ese
    /// guardado de historial —completamente ajeno a la importación— arrastraría
    /// esas entidades e intentaría persistirlas también, repitiendo el mismo
    /// choque de unicidad sin que nadie lo esperase.
    ///
    /// Tiene que ser una carrera REAL (Barrier, igual que el primer test): con
    /// dos ejecuciones en serie, la segunda vería la operación ya confirmada en
    /// el pre-check de <c>ExisteAsync</c> y nunca llegaría al choque de
    /// unicidad de <c>GuardarSiOperacionNuevaAsync</c> — ahí NO hay nada que
    /// limpiar, así que el test no probaría nada (lo confirmó una mutación:
    /// quitar el <c>ChangeTracker.Clear()</c> real seguía dando verde con dos
    /// ejecuciones en serie).
    /// </summary>
    [Fact]
    public async Task Perder_la_confirmacion_no_deja_el_contexto_compartido_envenenado_para_el_siguiente_guardado()
    {
        var plan = new PlanImportacionDto(
            OperacionId: Guid.NewGuid(),
            ClientesCentros: [], Empresas: [], Trabajadores: [],
            Documentos: [new DocumentoImportadoDto(Dni, NombreTipoDocumento, new DateOnly(2026, 2, 1), YaExiste: false)],
            Asignaciones: [], Advertencias: [], Omitidos: []);

        var barrera = new Barrier(2);

        Task<(CaeManagerDbContext Contexto, ResultadoImportacionDto Resultado)> ConfirmarAsync() => Task.Run(async () =>
        {
            barrera.SignalAndWait();
            var contexto = CrearContexto();
            var resultado = await ConstruirHandler(contexto).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None);
            return (contexto, resultado.Valor);
        });

        var resultados = await Task.WhenAll(ConfirmarAsync(), ConfirmarAsync());

        resultados.Sum(r => r.Resultado.DocumentosCreados).Should().Be(1, "el plan solo pedía un documento");
        await using var contextoGanador = resultados.Single(r => r.Resultado.DocumentosCreados == 1).Contexto;
        await using var contextoPerdedor = resultados.Single(r => r.Resultado.DocumentosCreados == 0).Contexto;

        // Un guardado completamente ajeno a la importación, sobre el MISMO
        // contexto que acaba de perder la carrera — exactamente lo que dispara
        // RegistrarHistorialImportacionCommand justo después en producción.
        contextoPerdedor.HistorialImportaciones.Add(
            HistorialImportacion.Exito("Test", "archivo.xlsx", Guid.NewGuid(), 0, 0, 0));
        var guardarHistorial = () => contextoPerdedor.SaveChangesAsync();

        await guardarHistorial.Should().NotThrowAsync(
            "el contexto compartido no puede quedar envenenado tras perder la carrera de idempotencia");

        await using var verificacion = CrearContexto();
        (await verificacion.Documentos.CountAsync(d => d.TrabajadorId == _trabajadorId && d.TipoDocumentoId == _tipoDocumentoId))
            .Should().Be(1, "el guardado del historial, ajeno a la importación, no debe colar un segundo documento");
    }

    /// <summary>
    /// Si algo revienta DESPUÉS de registrar la marca de operación pero ANTES
    /// del <c>SaveChangesAsync</c> final, esa marca no puede colarse igualmente
    /// en el siguiente guardado del mismo contexto compartido — dejaría la
    /// operación "confirmada" sin haberse confirmado de verdad, y un reintento
    /// legítimo se quedaría creyendo para siempre que ya se hizo.
    ///
    /// El disparador tiene que ser un fallo REAL que ocurra justo ahí: dos
    /// Empresas que solo difieren en mayúsculas colisionan al construir el
    /// diccionario case-insensitive (<c>ToDictionaryAsync</c> con
    /// <c>StringComparer.OrdinalIgnoreCase</c>, antes de entrar en los bucles
    /// por fila que sí tienen su propio <c>catch (ArgumentException)</c>) — un
    /// <c>CancellationToken</c> ya cancelado NO sirve para esto: se descubre
    /// en el pre-check de <c>ExisteAsync</c>, antes incluso de registrar la
    /// marca, así que no ejercitaría nada (lo confirmó una mutación: quitar el
    /// try/catch del Handle seguía dando verde con ese disparador).
    /// </summary>
    [Fact]
    public async Task Un_fallo_inesperado_antes_de_guardar_no_consume_la_operacion_ni_envenena_el_contexto()
    {
        await using var contexto = CrearContexto();
        contexto.Empresas.Add(new Empresa("Empresa Duplicada Por Mayusculas S.L."));
        contexto.Empresas.Add(new Empresa("empresa duplicada por mayusculas s.l."));
        await contexto.SaveChangesAsync();

        var operacionId = Guid.NewGuid();
        var plan = new PlanImportacionDto(
            OperacionId: operacionId,
            ClientesCentros: [], Empresas: [], Trabajadores: [],
            Documentos: [new DocumentoImportadoDto(Dni, NombreTipoDocumento, new DateOnly(2026, 2, 1), YaExiste: false)],
            Asignaciones: [], Advertencias: [], Omitidos: []);

        var intentoFallido = () => ConstruirHandler(contexto).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None);
        await intentoFallido.Should().ThrowAsync<ArgumentException>(
            "las dos Empresas del montaje solo difieren en mayúsculas y colisionan al construir el diccionario case-insensitive");

        // El mismo contexto se reutiliza para un guardado completamente ajeno
        // — exactamente lo que dispara RegistrarHistorialImportacionCommand
        // justo después, en el mismo circuito de Blazor Server.
        contexto.HistorialImportaciones.Add(
            HistorialImportacion.Fallo("Test", "archivo.xlsx", Guid.NewGuid(), "Fallo simulado"));
        await contexto.SaveChangesAsync();

        // Lo que de verdad importa: ese guardado ajeno no puede haber colado
        // la marca de operación como si la importación se hubiera confirmado.
        (await new OperacionImportacionRepository(contexto).ExisteAsync(operacionId)).Should().BeFalse(
            "el fallo ocurrió antes de guardar de verdad — un guardado ajeno no puede darla por confirmada");
    }

    private static EjecutarImportacionCommandHandler ConstruirHandler(CaeManagerDbContext contexto) =>
        new(
            new EmpresaRepository(contexto), new TrabajadorRepository(contexto), new DocumentoRepository(contexto),
            new AsignacionRepository(contexto), new OperacionImportacionRepository(contexto),
            contexto, contexto, contexto, contexto, contexto, contexto);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql =>
            {
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");
                // Fiel a ConfiguracionDeContexto.Aplicar (producción real): un test
                // que no activa reintentos no detectaría una transacción abierta a
                // mano sin envolver en CreateExecutionStrategy() — ver
                // hydra-postgres-retry-strategy-vs-transaccion-explicita. Este
                // handler no abre ninguna a mano, pero el test debe seguir siendo
                // fiel a la configuración real bajo la que corre en producción.
                npgsql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(30), errorCodesToAdd: null);
            })
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
