using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerSugerenciasPreventivas;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
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
/// Acción preventiva (pedido del usuario, 2026-08-08): agrupa el mismo cálculo
/// de "documento faltante" que ObtenerAlertasQueryFaltantesTests ya cubre, por
/// Centro+TipoDocumento, para detectar el patrón cuando afecta a más de un
/// técnico — con un único afectado no agrupa (esa alerta ya la cubre Alertas).
/// </summary>
public class ObtenerSugerenciasPreventivasQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _centroId;
    private Guid _tipoDocumentoId;
    private Guid _trabajador1Id;
    private Guid _trabajador2Id;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = new Cliente("Sugerencias Test S.L.", "B12345674", esCritico: false);
        var empresa = new Empresa("Contratista de Prueba S.L.", "B87654323");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro con exigencia nueva");
        contexto.Centros.Add(centro);

        var trabajador1 = Trabajador.DeEmpresa(empresa.Id, "Primer", "Técnico Prueba", "77189989B");
        var trabajador2 = Trabajador.DeEmpresa(empresa.Id, "Segundo", "Técnico Prueba", "12345678Z");
        contexto.Trabajadores.AddRange(trabajador1, trabajador2);

        var tipo = new TipoDocumento("Autorización uso de maquinaria", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, esObligatorio: true);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador1.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        contexto.Asignaciones.Add(new Asignacion(trabajador2.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow)));
        await contexto.SaveChangesAsync();

        _centroId = centro.Id;
        _tipoDocumentoId = tipo.Id;
        _trabajador1Id = trabajador1.Id;
        _trabajador2Id = trabajador2.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Dos_tecnicos_sin_el_mismo_documento_en_el_mismo_centro_se_agrupan()
    {
        await using var contexto = CrearContexto();
        var handler = CrearHandler(contexto);

        var sugerencias = await handler.Handle(new ObtenerSugerenciasPreventivasQuery(), CancellationToken.None);

        var sugerencia = sugerencias.Should().ContainSingle(s => s.CentroId == _centroId).Subject;
        sugerencia.TipoDocumentoId.Should().Be(_tipoDocumentoId);
        sugerencia.Trabajadores.Should().HaveCount(2);
        sugerencia.Trabajadores.Select(t => t.TrabajadorId).Should().BeEquivalentTo([_trabajador1Id, _trabajador2Id]);
    }

    [Fact]
    public async Task Un_solo_tecnico_afectado_no_genera_sugerencia()
    {
        await using (var contexto = CrearContexto())
        {
            // El segundo técnico ya tiene el documento — solo queda 1 afectado.
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajador2Id, _tipoDocumentoId, DateOnly.FromDateTime(DateTime.UtcNow), null));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = CrearHandler(lectura);

        var sugerencias = await handler.Handle(new ObtenerSugerenciasPreventivasQuery(), CancellationToken.None);

        sugerencias.Should().NotContain(s => s.CentroId == _centroId);
    }

    [Fact]
    public async Task Con_el_documento_creado_para_ambos_desaparece_la_sugerencia()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(_trabajador1Id, _tipoDocumentoId, DateOnly.FromDateTime(DateTime.UtcNow), null));
            contexto.Documentos.Add(Documento.DeTrabajador(_trabajador2Id, _tipoDocumentoId, DateOnly.FromDateTime(DateTime.UtcNow), null));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = CrearHandler(lectura);

        var sugerencias = await handler.Handle(new ObtenerSugerenciasPreventivasQuery(), CancellationToken.None);

        sugerencias.Should().NotContain(s => s.CentroId == _centroId);
    }

    private static ObtenerSugerenciasPreventivasQueryHandler CrearHandler(CaeManagerDbContext contexto) =>
        new(contexto, contexto, contexto, new AlcanceDatosServiceFalso(), new DocumentosFaltantesService(contexto, contexto));

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
