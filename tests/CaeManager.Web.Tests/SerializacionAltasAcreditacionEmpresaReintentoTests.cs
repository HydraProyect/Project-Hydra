using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El reintento de <see cref="SerializacionAltasAcreditacionEmpresa"/> sin servidor: la transacción de comando se sustituye
/// por una que repite la operación tras un primer intento fallido, como la estrategia de reintentos, y el candado se
/// intercepta antes de llegar a ninguna base. Que el candado serializa de verdad lo mide
/// <c>SerializacionAltasAcreditacionEmpresaTests</c> en la suite de integración; aquí, solo la decisión de reintentar.
/// </summary>
public class SerializacionAltasAcreditacionEmpresaReintentoTests
{
    [Fact]
    public async Task Sin_cambios_pendientes_del_llamante_un_intento_fallido_se_reintenta()
    {
        using var contexto = CrearContextoSinServidor();
        var transaccion = new TransaccionQueReintentaUnaVez();
        var operacion = new OperacionQueFallaLaPrimeraVez();

        var resultado = await new SerializacionAltasAcreditacionEmpresa(contexto, transaccion, Tenant())
            .EjecutarAsync(operacion.EjecutarAsync);

        resultado.EsExitoso.Should().BeTrue();
        operacion.Ejecuciones.Should().Be(2, "el segundo intento vuelve a agregar todo lo que guarda");
    }

    [Fact]
    public async Task Con_cambios_pendientes_del_llamante_un_intento_fallido_no_se_reintenta()
    {
        // Revisión de la ronda 2 de Codex sobre la PR #2074: el intento fallido vacía el contexto, también lo que el
        // llamante dejó para este guardado, y el reintento confirmaría el alta sin ello.
        using var contexto = CrearContextoSinServidor();
        contexto.Documentos.Add(Documento.DeEmpresa(
            Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 15), VigenciaDocumento.NoCaduca));
        var transaccion = new TransaccionQueReintentaUnaVez();
        var operacion = new OperacionQueFallaLaPrimeraVez();

        var reintento = () => new SerializacionAltasAcreditacionEmpresa(contexto, transaccion, Tenant())
            .EjecutarAsync(operacion.EjecutarAsync);

        await reintento.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no se reintenta*");
        operacion.Ejecuciones.Should().Be(1, "el segundo intento se rechaza antes de volver a ejecutar la operación");
        transaccion.Intentos.Should().Be(2);
    }

    private static TenantActualAmbiental Tenant() => new() { TenantId = Guid.NewGuid() };

    private static CaeManagerDbContext CrearContextoSinServidor()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=sin-servidor;Username=x;Password=x")
            .AddInterceptors(new ConexionSinServidor(), new CandadoSinServidor())
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), Tenant());
    }

    private sealed class FalloTransitorio : Exception;

    /// <summary>Lo que hace la estrategia de reintentos ante un fallo transitorio: ejecutar otra vez la operación entera.</summary>
    private sealed class TransaccionQueReintentaUnaVez : ITransaccionDeComando
    {
        public int Intentos { get; private set; }

        public async Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
        {
            try
            {
                Intentos++;
                return await operacion(cancellationToken);
            }
            catch (FalloTransitorio)
            {
                Intentos++;
                return await operacion(cancellationToken);
            }
        }
    }

    private sealed class OperacionQueFallaLaPrimeraVez
    {
        public int Ejecuciones { get; private set; }

        public Task<Result> EjecutarAsync(CancellationToken cancellationToken) =>
            ++Ejecuciones == 1 ? throw new FalloTransitorio() : Task.FromResult(Result.Exito());
    }

    private sealed class ConexionSinServidor : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class CandadoSinServidor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
    }
}
