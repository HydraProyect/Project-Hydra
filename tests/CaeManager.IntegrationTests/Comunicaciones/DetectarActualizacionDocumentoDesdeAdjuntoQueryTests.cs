using CaeManager.Application.Comunicaciones.Queries.DetectarActualizacionDocumentoDesdeAdjunto;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// docs/COMUNICACIONES.md § 12.7 — decisión del usuario, 2026-08-08: la
/// llamada a IA externa sobre adjuntos de conversación queda apagada por
/// defecto (<see cref="ExtraccionDocumentoAdjuntoOptions"/>). Este es el test
/// de seguridad que fija ese comportamiento: sin activar el flag, la query
/// nunca debe llamar a ningún proveedor de IA — el resto del pipeline
/// (crear/renovar el Documento) sigue disponible con el gestor rellenando
/// los campos a mano. El caso "activado" no se cubre aquí porque requeriría
/// un IDocumentAIRouterService real u otro fake dedicado — fuera de alcance
/// de este PR (el kill switch es lo que hay que fijar con un test).
/// </summary>
public class DetectarActualizacionDocumentoDesdeAdjuntoQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Con_el_kill_switch_apagado_no_detecta_nada_y_no_llama_al_router()
    {
        await using var contexto = CrearContexto();

        var cliente = Empresa.CrearComoCliente("Cliente Deteccion Documento S.L.", "B10380194", false, null, null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();

        var conversacion = new Conversacion("Documentación recibida");
        conversacion.AsignarCliente(cliente.Id);
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "Adjunto el certificado");
        await contexto.SaveChangesAsync();

        var adjunto = new AdjuntoMensaje(mensaje.Id, "certificado.pdf", "application/pdf", 1024, "adjuntos/certificado.pdf");
        contexto.AdjuntosMensaje.Add(adjunto);
        await contexto.SaveChangesAsync();

        var opciones = Options.Create(new ExtraccionDocumentoAdjuntoOptions { Activa = false });
        var handler = new DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler(
            contexto, new AlcanceDatosServiceFalso(), new AlmacenamientoQueNuncaSeDebeLlamar(),
            new RouterQueNuncaSeDebeLlamar(), opciones, contexto, contexto, contexto,
            new InstruccionTratamientoIaSiempreHabilitada(), new TenantActualFalso(_tenant));

        var resultado = await handler.Handle(new DetectarActualizacionDocumentoDesdeAdjuntoQuery(adjunto.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TipoDocumentoId.Should().BeNull();
        resultado.Valor.TrabajadorId.Should().BeNull();
        resultado.Valor.EmpresaId.Should().BeNull();
        resultado.Valor.FechaEmision.Should().BeNull();
        resultado.Valor.ConfianzaGeneral.Should().Be(0);
    }

    [Fact]
    public async Task Un_adjunto_de_otro_tenant_no_es_visible()
    {
        await using var contexto = CrearContexto();
        var opciones = Options.Create(new ExtraccionDocumentoAdjuntoOptions { Activa = false });
        var handler = new DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler(
            contexto, new AlcanceDatosServiceFalso(), new AlmacenamientoQueNuncaSeDebeLlamar(),
            new RouterQueNuncaSeDebeLlamar(), opciones, contexto, contexto, contexto,
            new InstruccionTratamientoIaSiempreHabilitada(), new TenantActualFalso(_tenant));

        var resultado = await handler.Handle(new DetectarActualizacionDocumentoDesdeAdjuntoQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Adjunto.NoEncontrado");
    }

    /// <summary>Auditoría módulo 6: un hilo atado a un buzón personal ajeno no debe ser visible aunque comparta Cliente o no tenga Cliente asignado.</summary>
    [Fact]
    public async Task Un_adjunto_de_un_buzon_personal_ajeno_no_es_visible()
    {
        await using var contexto = CrearContexto();

        var conversacion = new Conversacion("Documentación recibida en buzón personal");
        var conexionAjenaId = Guid.NewGuid();
        conversacion.AsociarConexion(conexionAjenaId, "hilo-externo-ajeno");
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "Adjunto el certificado");
        await contexto.SaveChangesAsync();

        var adjunto = new AdjuntoMensaje(mensaje.Id, "certificado.pdf", "application/pdf", 1024, "adjuntos/certificado.pdf");
        contexto.AdjuntosMensaje.Add(adjunto);
        await contexto.SaveChangesAsync();

        var opciones = Options.Create(new ExtraccionDocumentoAdjuntoOptions { Activa = false });
        var handler = new DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler(
            contexto, new AlcanceDatosServiceFalso(conexionesIntegracionAjenas: [conexionAjenaId]), new AlmacenamientoQueNuncaSeDebeLlamar(),
            new RouterQueNuncaSeDebeLlamar(), opciones, contexto, contexto, contexto,
            new InstruccionTratamientoIaSiempreHabilitada(), new TenantActualFalso(_tenant));

        var resultado = await handler.Handle(new DetectarActualizacionDocumentoDesdeAdjuntoQuery(adjunto.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Adjunto.NoEncontrado");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private class AlmacenamientoQueNuncaSeDebeLlamar : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No debería llamarse con el kill switch apagado.");

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No debería llamarse con el kill switch apagado.");

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No debería llamarse con el kill switch apagado.");
    }

    private class RouterQueNuncaSeDebeLlamar : CaeManager.Application.DocumentosIa.IDocumentAIRouterService
    {
        public Task<Result<CaeManager.Application.DocumentosIa.Common.ExtraccionEstructuradaDto>> ProcesarAsync(
            byte[] contenido, string nombreArchivo, string tipoEsperado, Guid? documentoId = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No debería llamarse con el kill switch apagado.");
    }

    /// <summary>Nivel 0 (DEC-33/REC-035) siempre abierto: esta suite prueba el kill switch de despliegue y el alcance de datos, no el gate.</summary>
    private sealed class InstruccionTratamientoIaSiempreHabilitada : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}
