using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Alertas;

/// <summary>
/// P1-15 de docs/business/MATURITY_REVIEW.md: un Trabajador con Asignación
/// activa a un Centro que exige un TipoDocumento obligatorio, sin ningún
/// Documento de ese tipo, no aparecía en ningún sitio — ni "Vencido" (no hay
/// fila que evaluar) ni "NoAplica" (ese estado es para Documentos existentes
/// sin vigencia). Estos tests prueban el hueco cerrado, contra Postgres real.
/// </summary>
public class ObtenerAlertasQueryFaltantesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _trabajadorId;
    private Guid _tipoDocumentoId;
    private Guid _centroId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Falta Documental S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Contratista de Prueba S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro con exigencia documental");
        contexto.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Falta", "Documento Prueba", "77189989B");
        contexto.Trabajadores.Add(trabajador);

        // Sin ninguna fila en TiposDocumentoCentros: aplica a todos los Centros.
        var tipo = new TipoDocumento("EPIs", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await contexto.SaveChangesAsync();

        _trabajadorId = trabajador.Id;
        _tipoDocumentoId = tipo.Id;
        _centroId = centro.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_tipo_obligatorio_sin_ningun_documento_genera_alerta_de_faltante()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerAlertasQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new ResolverClientePrincipalService(contexto, contexto, contexto),
            new AlcanceDatosServiceFalso(), new DocumentosFaltantesService(contexto, contexto));

        var alertas = await handler.Handle(new ObtenerAlertasQuery(), CancellationToken.None);

        var faltante = alertas.Should().ContainSingle(a => a.Estado == EstadoDocumento.Faltante).Subject;
        faltante.DocumentoId.Should().BeNull();
        faltante.TrabajadorId.Should().Be(_trabajadorId);
        faltante.TipoDocumentoId.Should().Be(_tipoDocumentoId);
        faltante.TipoDocumentoNombre.Should().Be("EPIs");
    }

    [Fact]
    public async Task Crear_el_documento_hace_desaparecer_la_alerta_de_faltante()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoId, DateOnly.FromDateTime(DateTime.UtcNow), null));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerAlertasQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura, lectura,
            new ResolverClientePrincipalService(lectura, lectura, lectura),
            new AlcanceDatosServiceFalso(), new DocumentosFaltantesService(lectura, lectura));

        var alertas = await handler.Handle(new ObtenerAlertasQuery(), CancellationToken.None);

        alertas.Should().NotContain(a => a.Estado == EstadoDocumento.Faltante);
    }

    [Fact]
    public async Task Un_trabajador_sin_asignacion_activa_al_centro_no_genera_alerta()
    {
        await using (var contexto = CrearContexto())
        {
            var asignacion = await contexto.Asignaciones.SingleAsync(a => a.TrabajadorId == _trabajadorId);
            asignacion.DarDeBaja(DateOnly.FromDateTime(DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerAlertasQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura, lectura,
            new ResolverClientePrincipalService(lectura, lectura, lectura),
            new AlcanceDatosServiceFalso(), new DocumentosFaltantesService(lectura, lectura));

        var alertas = await handler.Handle(new ObtenerAlertasQuery(), CancellationToken.None);

        alertas.Should().NotContain(a => a.Estado == EstadoDocumento.Faltante);
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
}
